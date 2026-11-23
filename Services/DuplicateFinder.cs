using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using DuplicadosApp.Models;

namespace DuplicadosApp.Services;

/// <summary>Progreso notificado durante el escaneo o el borrado.</summary>
public sealed record ScanProgress(double Percentage, string Text);

/// <summary>
/// Motor de búsqueda de duplicados multietapa de alto rendimiento:
/// 1) Recorrido paralelo con agrupación compacta sin asignar listas para tamaños únicos.
/// 2) Ordenación LPT (grupos más pesados primero) con paralelismo acotado.
/// 3) Huella rápida en una sola apertura (cabecera 8 KiB + cola 8 KiB + centro 4 KiB)
///    y deduplicación de enlaces duros (hard links) por FileIdentity.
/// 4) Para cubetas con 2 archivos: comparación directa byte a byte del rango no leído.
///    Para cubetas con 3+ archivos: SHA-256 en flujo O(M) + confirmación byte a byte.
/// </summary>
public sealed class DuplicateFinder
{
    private static readonly int MaxComparisonThreads = Math.Clamp(Environment.ProcessorCount, 2, 8);

    private readonly Action<string> _log;
    private readonly Action<ScanProgress> _progress;
    private readonly Action<string> _activity;
    private readonly Action<PhaseProgress> _phase;
    private readonly Func<bool> _isPaused;
    private readonly Func<bool> _isCancelled;
    private readonly Action? _waitIfPaused;
    private readonly ScanOptions _options;

    private int _errors;
    private int _skippedHardLinks;
    private int _groupsProcessed;
    private int _entriesProcessed;
    private long _lastProgressTicks;

    public DuplicateFinder(
        Action<string> log,
        Action<ScanProgress> progress,
        Func<bool> isPaused,
        Func<bool> isCancelled,
        Action<string>? activity = null,
        Action<PhaseProgress>? phase = null,
        ScanOptions? options = null,
        Action? waitIfPaused = null)
    {
        _log = log;
        _progress = progress;
        _isPaused = isPaused;
        _isCancelled = isCancelled;
        _activity = activity ?? (_ => { });
        _phase = phase ?? (_ => { });
        _options = options ?? ScanOptions.Default;
        _waitIfPaused = waitIfPaused;
    }

    public ScanResult Run(string folder)
    {
        var result = new ScanResult();
        var stopwatch = Stopwatch.StartNew();
        var errors = 0;

        void Log(string text) => _log(text);
        void Progress(double percentage, string text) => _progress(new ScanProgress(percentage, text));
        void Activity(string text) => _activity(text);
        void Phase(string name, double percentage, bool indeterminate = false)
            => _phase(new PhaseProgress(name, percentage, indeterminate));
        string Elapsed() => $"{stopwatch.Elapsed.TotalSeconds:F1}s";

        Activity("Preparando el escaneo");
        Phase("Preparando el escaneo", 0, indeterminate: true);
        Log($"Iniciando búsqueda en: {folder}");
        Log(BuildModeDescription());

        // Agrupación compacta en memoria: para archivos con clave única no se reserva ninguna List<>.
        var buckets = new Dictionary<BucketKey, SizeBucket>(capacity: 4096);
        var extIntern = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var bucketLock = new object();
        var logEveryFolders = 250;

        var (foldersSeen, totalFiles, skippedCloud) = FileSystemService.ScanDirectoryParallel(
            folder,
            _options,
            (path, size, lastWriteUtc) =>
            {
                string ext;
                if (_options.RequireSameExtension)
                {
                    var rawExt = Path.GetExtension(path);
                    lock (bucketLock)
                    {
                        if (!extIntern.TryGetValue(rawExt, out ext!))
                        {
                            ext = rawExt.ToLowerInvariant();
                            extIntern[ext] = ext;
                        }

                        AddToBucketUnsafe(buckets, new BucketKey(size, ext), path, lastWriteUtc);
                    }
                }
                else
                {
                    lock (bucketLock)
                    {
                        AddToBucketUnsafe(buckets, new BucketKey(size, string.Empty), path, lastWriteUtc);
                    }
                }
            },
            message =>
            {
                Interlocked.Increment(ref errors);
                Log(message);
            },
            (folders, files, currentFolder) =>
            {
                Activity($"Recorriendo {currentFolder}");
                Phase($"Recorriendo carpetas ({folders:N0} carpetas, {files:N0} archivos)", 0, indeterminate: true);

                // Curva asintótica suave 0 -> 35 % para reflejar progreso constante sin saltos.
                var discoveryPct = 35.0 * (1.0 - (1.0 / (1.0 + (folders / 250.0))));
                Progress(
                    discoveryPct,
                    $"Recorriendo… {folders:N0} carpetas, {files:N0} archivos ({Elapsed()})");

                if (folders >= logEveryFolders)
                {
                    Log($"Recorridas {folders:N0} carpetas, {files:N0} archivos ({Elapsed()})");
                    logEveryFolders = folders + 250;
                }
            },
            WaitWhilePaused,
            _isCancelled);

        result.TotalFoldersScanned = foldersSeen;
        result.TotalFilesScanned = totalFiles;
        result.SkippedCloudFiles = skippedCloud;

        if (_isCancelled())
        {
            result.Cancelled = true;
            result.Errors = Volatile.Read(ref errors);
            result.Elapsed = stopwatch.Elapsed;
            return result;
        }

        var discoveryErrors = Volatile.Read(ref errors);
        Log($"Recorrido terminado: {foldersSeen:N0} carpetas y {totalFiles:N0} archivos en {Elapsed()}"
            + (skippedCloud > 0 ? $" ({skippedCloud:N0} solo en la nube omitidos)" : string.Empty));

        Activity($"Filtrando candidatos entre {totalFiles:N0} archivos");
        Phase($"Filtrando candidatos ({totalFiles:N0} archivos)", 0, indeterminate: true);
        Progress(35, $"Analizando {totalFiles:N0} archivos…");

        // Extrae solo los grupos con >= 2 archivos y los ordena por carga estimada descendente (LPT).
        var candidates = new List<(long Size, List<(string Path, DateTime Modified)> Entries)>();
        var totalEntries = 0;

        foreach (var (key, bucket) in buckets)
        {
            if (bucket.Rest is null)
            {
                continue;
            }

            var list = new List<(string Path, DateTime Modified)>(bucket.Rest.Count + 1)
            {
                (bucket.FirstPath, bucket.FirstModified),
            };
            list.AddRange(bucket.Rest);
            candidates.Add((key.Size, list));
            totalEntries += list.Count;
        }

        // Libera el diccionario temporal cuanto antes para reducir el pico de memoria.
        buckets.Clear();
        extIntern.Clear();

        candidates.Sort(static (a, b) =>
        {
            var weightA = (double)a.Size * a.Entries.Count;
            var weightB = (double)b.Size * b.Entries.Count;
            return weightB.CompareTo(weightA);
        });

        var totalGroups = candidates.Count;
        const string comparisonText = "Comparando contenidos…";
        Log(
            discoveryErrors > 0
                ? $"⚠ {totalGroups:N0} grupo(s) candidato(s) ({totalEntries:N0} archivos); {discoveryErrors} error(es) de lectura"
                : $"{totalGroups:N0} grupo(s) candidato(s) ({totalEntries:N0} archivos); comparando contenidos");

        var groups = new List<DuplicateGroup>();
        _errors = 0;
        _skippedHardLinks = 0;
        _groupsProcessed = 0;
        _entriesProcessed = 0;
        _lastProgressTicks = 0;
        var phaseName = comparisonText.TrimEnd('…');

        if (totalGroups > 0)
        {
            Phase($"{phaseName} (0 de {totalEntries:N0} archivos)", 0);

            var partitioner = Partitioner.Create(0, totalGroups, rangeSize: 1);
            Parallel.ForEach(
                partitioner,
                new ParallelOptions { MaxDegreeOfParallelism = MaxComparisonThreads },
                range =>
                {
                    for (var index = range.Item1; index < range.Item2; index++)
                    {
                        if (_isCancelled())
                        {
                            return;
                        }

                        WaitWhilePaused();
                        if (_isCancelled())
                        {
                            return;
                        }

                        var (size, entries) = candidates[index];
                        Activity($"{comparisonText} {FormatSize(size)} ({entries.Count} archivos)");

                        void OnFile()
                        {
                            var processed = Interlocked.Increment(ref _entriesProcessed);
                            ReportComparisonProgress(processed, totalEntries, phaseName, comparisonText, Elapsed());
                        }

                        var partial = new List<DuplicateGroup>();
                        ConfirmGroup(size, entries, partial, OnFile);

                        if (partial.Count > 0)
                        {
                            lock (groups)
                            {
                                groups.AddRange(partial);
                            }
                        }

                        var doneGroups = Interlocked.Increment(ref _groupsProcessed);
                        if (doneGroups == totalGroups || doneGroups % 25 == 0)
                        {
                            Log($"{phaseName}: {doneGroups:N0} de {totalGroups:N0} grupos ({Elapsed()})");
                        }
                    }
                });
        }

        if (_isCancelled())
        {
            result.Cancelled = true;
            result.Errors = discoveryErrors + Volatile.Read(ref _errors);
            result.SkippedHardLinks = Volatile.Read(ref _skippedHardLinks);
            result.Elapsed = stopwatch.Elapsed;
            return result;
        }

        var totalErrors = discoveryErrors + Volatile.Read(ref _errors);
        var hardLinks = Volatile.Read(ref _skippedHardLinks);

        Activity("Ordenando resultados");
        Phase("Ordenando resultados", 99);
        Progress(99, "Ordenando resultados…");

        // Orden por defecto: mayor espacio recuperable primero para priorizar lo más útil.
        groups.Sort(static (a, b) =>
        {
            var cmp = b.RecoverableBytes.CompareTo(a.RecoverableBytes);
            if (cmp != 0)
            {
                return cmp;
            }

            cmp = b.Size.CompareTo(a.Size);
            return cmp != 0 ? cmp : string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
        });

        stopwatch.Stop();
        result.Errors = totalErrors;
        result.SkippedHardLinks = hardLinks;
        result.Elapsed = stopwatch.Elapsed;
        result.Groups.AddRange(groups);

        var extraInfo = hardLinks > 0 ? $", {hardLinks:N0} enlace(s) duro(s) omitido(s)" : string.Empty;
        Log(
            totalErrors > 0
                ? $"⚠ Búsqueda terminada en {Elapsed()}: {groups.Count:N0} grupo(s) de duplicados, {totalErrors} error(es){extraInfo}"
                : $"Búsqueda terminada en {Elapsed()}: {groups.Count:N0} grupo(s) de duplicados, sin errores{extraInfo}");

        Progress(100, "Listo");
        return result;
    }

    private string BuildModeDescription()
    {
        var extText = _options.RequireSameExtension ? "mismo tamaño y misma extensión" : "mismo tamaño (cualquier extensión)";
        var minText = _options.MinFileSizeBytes > 0
            ? $", mínimo {FormatSize(_options.MinFileSizeBytes)}"
            : ", incluyendo archivos vacíos";
        return $"Modo: {extText}{minText}; huella rápida + confirmación byte a byte";
    }

    private static void AddToBucketUnsafe(
        Dictionary<BucketKey, SizeBucket> buckets,
        BucketKey key,
        string path,
        DateTime lastWriteUtc)
    {
        if (!buckets.TryGetValue(key, out var bucket))
        {
            buckets[key] = new SizeBucket(path, lastWriteUtc, null);
            return;
        }

        bucket.Rest ??= new List<(string, DateTime)>(2);
        bucket.Rest.Add((path, lastWriteUtc));
        buckets[key] = bucket;
    }

    /// <summary>
    /// Confirma duplicados dentro de un grupo candidato del mismo tamaño (y extensión si aplica):
    /// 1) Calcula huella rápida (o completa si &lt;= 16 KiB) en una sola apertura y filtra enlaces duros.
    /// 2) Agrupa por huella en O(1).
    /// 3) Confirma exactamente byte a byte (directamente si hay 2 archivos; vía SHA-256 en flujo O(M)
    ///    seguido de verificación byte a byte si hay 3 o más).
    /// </summary>
    private void ConfirmGroup(
        long size,
        List<(string Path, DateTime Modified)> entries,
        List<DuplicateGroup> groups,
        Action onFile)
    {
        var byFingerprint = new Dictionary<ContentHash, List<FileEntry>>();
        var seenIdentities = new HashSet<FileIdentity>();
        var isFullHash = size <= FileSystemService.FullReadThreshold;

        foreach (var (path, _) in entries)
        {
            onFile();

            if (_isCancelled())
            {
                return;
            }

            WaitWhilePaused();
            if (_isCancelled())
            {
                return;
            }

            var entry = FileSystemService.TryGetEntryAndFingerprint(
                path,
                size,
                out var fingerprint,
                out isFullHash);

            if (entry is null)
            {
                Interlocked.Increment(ref _errors);
                continue;
            }

            // Dos rutas con la misma FileIdentity son enlaces duros al mismo archivo físico:
            // no ocupan espacio extra ni son copias independientes.
            if (entry.Identity.IsValid && !seenIdentities.Add(entry.Identity))
            {
                Interlocked.Increment(ref _skippedHardLinks);
                continue;
            }

            if (!byFingerprint.TryGetValue(fingerprint, out var bucket))
            {
                bucket = new List<FileEntry>(2);
                byFingerprint[fingerprint] = bucket;
            }

            bucket.Add(entry);
        }

        foreach (var bucket in byFingerprint.Values)
        {
            if (_isCancelled())
            {
                return;
            }

            if (bucket.Count < 2)
            {
                continue;
            }

            if (size == 0)
            {
                AddVerifiedGroup(size, bucket, groups);
                continue;
            }

            if (isFullHash)
            {
                // Archivo <= 16 KiB ya coincidió en SHA-256 completo; confirmación byte a byte en microsegundos.
                ConfirmBucketByteByByte(size, bucket, startOffset: 0, endOffset: size, groups);
                continue;
            }

            var uncheckedStart = (long)FileSystemService.HeaderSize;
            var uncheckedEnd = size - FileSystemService.TailSize;

            if (bucket.Count == 2)
            {
                // Con solo 2 archivos, comparar directamente el rango no leído es óptimo:
                // se detiene en el primer byte distinto.
                ConfirmBucketByteByByte(size, bucket, uncheckedStart, uncheckedEnd, groups);
                continue;
            }

            // Con 3+ archivos en la misma cubeta, SHA-256 completo en flujo evita O(M²) lecturas.
            var byFullHash = new Dictionary<ContentHash, List<FileEntry>>();
            foreach (var entry in bucket)
            {
                if (_isCancelled())
                {
                    return;
                }

                WaitWhilePaused();
                if (!FileSystemService.TryComputeFullHash(entry, out var fullHash, _isCancelled, WaitWhilePaused))
                {
                    if (!_isCancelled())
                    {
                        Interlocked.Increment(ref _errors);
                    }

                    continue;
                }

                if (!byFullHash.TryGetValue(fullHash, out var subBucket))
                {
                    subBucket = new List<FileEntry>(2);
                    byFullHash[fullHash] = subBucket;
                }

                subBucket.Add(entry);
            }

            foreach (var subBucket in byFullHash.Values)
            {
                if (_isCancelled())
                {
                    return;
                }

                if (subBucket.Count < 2)
                {
                    continue;
                }

                ConfirmBucketByteByByte(size, subBucket, uncheckedStart, uncheckedEnd, groups);
            }
        }
    }

    private void ConfirmBucketByteByByte(
        long size,
        List<FileEntry> bucket,
        long startOffset,
        long endOffset,
        List<DuplicateGroup> groups)
    {
        var subGroups = new List<List<FileEntry>>(1);

        foreach (var entry in bucket)
        {
            if (_isCancelled())
            {
                return;
            }

            WaitWhilePaused();
            List<FileEntry>? match = null;

            foreach (var existing in subGroups)
            {
                if (_isCancelled())
                {
                    return;
                }

                var representative = existing[0];
                try
                {
                    if (FileSystemService.CompareByteByByte(
                            entry.Path,
                            representative.Path,
                            _isCancelled,
                            startOffset: startOffset,
                            endOffset: endOffset,
                            waitIfPaused: WaitWhilePaused,
                            expectedIdentityA: entry.Identity,
                            expectedIdentityB: representative.Identity))
                    {
                        match = existing;
                        break;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Interlocked.Increment(ref _errors);
                }
            }

            if (_isCancelled())
            {
                return;
            }

            if (match is null)
            {
                subGroups.Add([entry]);
            }
            else
            {
                match.Add(entry);
            }
        }

        foreach (var subGroup in subGroups)
        {
            if (subGroup.Count > 1)
            {
                AddVerifiedGroup(size, subGroup, groups);
            }
        }
    }

    private static void AddVerifiedGroup(long size, List<FileEntry> files, List<DuplicateGroup> groups)
    {
        // Orden determinista dentro del grupo: más antiguo primero (o ruta más corta en empate).
        files.Sort(static (a, b) =>
        {
            var cmp = a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc);
            if (cmp != 0)
            {
                return cmp;
            }

            cmp = a.Path.Length.CompareTo(b.Path.Length);
            return cmp != 0 ? cmp : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
        });

        groups.Add(new DuplicateGroup
        {
            Size = size,
            DisplayName = Path.GetFileName(files[0].Path),
            Files = files,
        });
    }

    private void ReportComparisonProgress(
        int processed,
        int total,
        string phaseName,
        string comparisonText,
        string elapsed)
    {
        var isFinal = processed >= total;
        if (!isFinal)
        {
            var now = Environment.TickCount64;
            var prev = Interlocked.Read(ref _lastProgressTicks);
            if (now - prev < 90 || Interlocked.CompareExchange(ref _lastProgressTicks, now, prev) != prev)
            {
                return;
            }
        }

        var safeTotal = Math.Max(1, total);
        var phasePct = (double)processed * 100.0 / safeTotal;
        var globalPct = 35.0 + (63.0 * processed / safeTotal);

        _phase(new PhaseProgress(
            $"{phaseName} ({processed:N0} de {total:N0} archivos)",
            phasePct));

        _progress(new ScanProgress(
            globalPct,
            $"{comparisonText} {processed:N0} de {total:N0} archivos ({elapsed})"));
    }

    private void WaitWhilePaused()
    {
        if (_waitIfPaused is not null)
        {
            _waitIfPaused();
            return;
        }

        while (_isPaused() && !_isCancelled())
        {
            Thread.Sleep(50);
        }
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} {units[unit]}"
            : $"{value:0.#} {units[unit]}";
    }

    private readonly record struct BucketKey(long Size, string Extension);

    private struct SizeBucket(string firstPath, DateTime firstModified, List<(string Path, DateTime Modified)>? rest)
    {
        public string FirstPath = firstPath;
        public DateTime FirstModified = firstModified;
        public List<(string Path, DateTime Modified)>? Rest = rest;
    }
}