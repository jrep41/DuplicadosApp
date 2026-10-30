using System.Buffers;
using System.Diagnostics;
using System.IO;
using DuplicadosApp.Models;

namespace DuplicadosApp.Services;

/// <summary>Progreso notificado durante el escaneo.</summary>
public sealed record ScanProgress(double Percentage, string Text);

/// <summary>
/// Lógica de búsqueda de duplicados, portada de duplica2.py.
/// </summary>
public sealed class DuplicateFinder
{
    private static readonly int MaxProcessingThreads = Environment.ProcessorCount;

    private readonly Action<string> _log;
    private readonly Action<ScanProgress> _progress;
    private readonly Action<string> _activity;
    private readonly Action<PhaseProgress> _phase;
    private readonly Func<bool> _isPaused;
    private readonly Func<bool> _isCancelled;

    private int _errors;
    private int _groupsProcessed;
    private int _entriesProcessed;

    public DuplicateFinder(
        Action<string> log,
        Action<ScanProgress> progress,
        Func<bool> isPaused,
        Func<bool> isCancelled,
        Action<string>? activity = null,
        Action<PhaseProgress>? phase = null)
    {
        _log = log;
        _progress = progress;
        _isPaused = isPaused;
        _isCancelled = isCancelled;
        _activity = activity ?? (_ => { });
        _phase = phase ?? (_ => { });
    }

    public ScanResult Run(string folder, bool compareByteByByte)
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
        Log("Modo: " + (compareByteByByte ? "comparación byte a byte" : "nombre y tamaño"));

        var bySize = new Dictionary<long, List<FileEntry>>();
        var foldersSeen = 0;
        var total = 0;
        var logEvery = 200;

        foreach (var (path, size) in FileSystemService.EnumerateFiles(
                     folder,
                     message => { errors++; Log(message); },
                     (folders, files, currentFolder) =>
                     {
                         foldersSeen = folders;
                         Activity($"Recorriendo {currentFolder}");
                         Phase("Recorriendo carpetas", 0, indeterminate: true);
                         Progress(
                             Math.Min(45, folders / 20),
                             $"Recorriendo carpetas… {folders} carpetas, {files} archivos ({Elapsed()})");

                         if (folders >= logEvery)
                         {
                             Log($"Leyendo {currentFolder}");
                             Log($"Recorridas {folders} carpetas, {files} archivos encontrados");
                             logEvery += 200;
                         }
                     },
                     _isPaused,
                     _isCancelled))
        {
            if (_isCancelled())
            {
                result.Cancelled = true;
                result.Errors = errors;
                return result;
            }

            WaitWhilePaused();
            total++;

            if (!bySize.TryGetValue(size, out var list))
            {
                list = [];
                bySize[size] = list;
            }

            list.Add(new FileEntry(path, size, FileIdentity.Unknown));

            if (total % 5000 == 0)
            {
                Progress(
                    Math.Min(45, foldersSeen / 20),
                    $"Agrupando por tamaño… {total} archivos ({Elapsed()})");
                Log($"Leídos {total} archivos ({bySize.Count} tamaños distintos, {errors} errores)");
            }
        }

        if (_isCancelled())
        {
            result.Cancelled = true;
            result.Errors = errors;
            return result;
        }

        Log($"Recorrido terminado: {foldersSeen} carpetas y {total} archivos en {Elapsed()}");
        Activity($"Agrupando por tamaño {total} archivos");
        Phase($"Agrupando por tamaño ({total} archivos)", 0, indeterminate: true);
        Progress(45, $"Leídos {total} archivos, agrupando por tamaño…");

        Log(
            errors > 0
                ? $"⚠ Tamaños agrupados: {bySize.Count} tamaños distintos, {errors} archivo(s) con error"
                : $"Tamaños agrupados: {bySize.Count} tamaños distintos, 0 archivos con error");
        var candidates = bySize.Where(pair => pair.Value.Count > 1).ToList();
        var totalGroups = candidates.Count;
        var comparisonText = compareByteByByte ? "Comparando contenidos…" : "Agrupando por nombre…";
        Log($"{totalGroups} tamaño(s) con más de un archivo; pasando a {comparisonText.TrimEnd('…').ToLowerInvariant()}");

        var groups = new List<DuplicateGroup>();
        _errors = 0;
        _groupsProcessed = 0;
        _entriesProcessed = 0;
        var phaseName = comparisonText.TrimEnd('…');
        var totalEntries = candidates.Sum(pair => pair.Value.Count);

        if (totalGroups > 0)
        {
            Phase($"{phaseName} (0 de {totalEntries} archivos)", 0);

            Parallel.For(
                0,
                totalGroups,
                new ParallelOptions { MaxDegreeOfParallelism = MaxProcessingThreads },
                index =>
                {
                    if (_isCancelled())
                    {
                        return;
                    }

                    WaitWhilePaused();
                    var (size, entries) = candidates[index];
                    Activity($"{comparisonText} tamaño {FormatSize(size)} ({entries.Count} archivos)");

                    void OnFile() => ReportEntryProgress(
                        Interlocked.Increment(ref _entriesProcessed), totalEntries, phaseName);

                    var partial = new List<DuplicateGroup>();
                    if (compareByteByByte)
                    {
                        ConfirmByContent(size, entries, partial, OnFile);
                    }
                    else
                    {
                        ConfirmByName(size, entries, partial, OnFile);
                    }

                    if (partial.Count > 0)
                    {
                        lock (groups)
                        {
                            groups.AddRange(partial);
                        }
                    }

                    ReportGroupProgress(Interlocked.Increment(ref _groupsProcessed), totalGroups, comparisonText, Elapsed());
                });
        }

        if (_isCancelled())
        {
            result.Cancelled = true;
            result.Errors = errors + Volatile.Read(ref _errors);
            return result;
        }

        errors += Volatile.Read(ref _errors);

        Activity($"Verificando identidad de {groups.Count} grupo(s)");
        Phase($"Verificando identidad (0 de {groups.Count} grupos)", 0);
        ResolveIdentities(groups, ref errors, (done, count) =>
        {
            var percent = (long)done * 100 / Math.Max(1, count);
            Activity($"Verificando identidad de {done} de {count} grupo(s)");
            Phase($"Verificando identidad ({done} de {count} grupos)", percent);
        });

        if (_isCancelled())
        {
            result.Cancelled = true;
            result.Errors = errors;
            return result;
        }

        result.Errors = errors;
        Log(
            errors > 0
                ? $"⚠ Búsqueda terminada en {Elapsed()}: {groups.Count} grupo(s) de duplicados, {errors} error(es)"
                : $"Búsqueda terminada en {Elapsed()}: {groups.Count} grupo(s) de duplicados, sin errores");
        Progress(100, "Finalizando…");
        result.Groups.AddRange(groups);
        return result;
    }

    private void ResolveIdentities(
        List<DuplicateGroup> groups,
        ref int errors,
        Action<int, int> onGroup)
    {
        var total = groups.Count;
        var done = 0;
        for (var i = groups.Count - 1; i >= 0; i--)
        {
            if (_isCancelled())
            {
                return;
            }

            WaitWhilePaused();
            var group = groups[i];
            var resolved = new List<FileEntry>(group.Files.Count);
            foreach (var file in group.Files)
            {
                if (_isCancelled())
                {
                    return;
                }

                // En modo byte a byte la identidad ya se resolvió y verificó durante
                // ConfirmByContent, evitando volver a abrir cada archivo en disco.
                if (file.Identity != FileIdentity.Unknown)
                {
                    resolved.Add(file);
                    continue;
                }

                var entry = FileSystemService.TryGetEntry(file.Path);
                if (entry is null || entry.Size != file.Size)
                {
                    errors++;
                    continue;
                }

                resolved.Add(entry);
            }

            if (resolved.Count > 1)
            {
                group.Files = resolved;
            }
            else
            {
                groups.RemoveAt(i);
            }

            onGroup(++done, total);
        }
    }

    private void ConfirmByName(
        long size,
        List<FileEntry> entries,
        List<DuplicateGroup> groups,
        Action onFile)
    {
        // Caso rápido: cuando solo hay 2 candidatos del mismo tamaño, se comparan sus
        // nombres mediante ReadOnlySpan<char> sin alojar un Dictionary ni cadenas nuevas.
        if (entries.Count == 2)
        {
            onFile();
            onFile();
            if (_isCancelled())
            {
                return;
            }

            var name0 = Path.GetFileName(entries[0].Path.AsSpan());
            var name1 = Path.GetFileName(entries[1].Path.AsSpan());
            if (name0.Equals(name1, StringComparison.OrdinalIgnoreCase))
            {
                groups.Add(new DuplicateGroup
                {
                    Size = size,
                    DisplayName = Path.GetFileName(entries[0].Path),
                    Files = entries,
                    ComparedByteByByte = false,
                });
            }

            return;
        }

        var byName = new Dictionary<string, List<FileEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            onFile();

            if (_isCancelled())
            {
                return;
            }

            var name = Path.GetFileName(entry.Path);
            if (!byName.TryGetValue(name, out var list))
            {
                list = [];
                byName[name] = list;
            }

            list.Add(entry);
        }

        foreach (var (name, group) in byName)
        {
            if (group.Count > 1)
            {
                groups.Add(new DuplicateGroup
                {
                    Size = size,
                    DisplayName = name,
                    Files = group,
                    ComparedByteByByte = false,
                });
            }
        }
    }

    /// <summary>
    /// Confirma duplicados leyendo la identidad y los primeros 8 KB de cada candidato
    /// una sola vez. La cabecera del representante de cada subgrupo se conserva en un
    /// búfer de ArrayPool para descartar en memoria sin releer del disco. Solo cuando
    /// los primeros 8 KB coinciden y el tamaño supera 8 KB se compara el resto del
    /// archivo en disco empezando desde el desplazamiento 8192.
    /// </summary>
    private void ConfirmByContent(
        long size,
        List<FileEntry> entries,
        List<DuplicateGroup> groups,
        Action onFile)
    {
        var subGroups = new List<(byte[] Header, int HeaderLength, List<FileEntry> Files)>();
        var headerSize = (int)Math.Min(FileSystemService.HeaderSize, size);
        var entryHeader = headerSize > 0
            ? ArrayPool<byte>.Shared.Rent(headerSize)
            : [];

        try
        {
            foreach (var raw in entries)
            {
                onFile();

                if (_isCancelled())
                {
                    return;
                }

                WaitWhilePaused();

                var entry = FileSystemService.TryGetEntryAndHeader(
                    raw.Path,
                    size,
                    entryHeader.AsSpan(0, headerSize),
                    out var headerRead);

                if (entry is null || headerRead != headerSize)
                {
                    Interlocked.Increment(ref _errors);
                    continue;
                }

                List<FileEntry>? match = null;
                var entryHeaderSpan = entryHeader.AsSpan(0, headerSize);

                foreach (var candidate in subGroups)
                {
                    if (_isCancelled())
                    {
                        return;
                    }

                    // Descarte en memoria sin lecturas de disco.
                    if (!entryHeaderSpan.SequenceEqual(candidate.Header.AsSpan(0, candidate.HeaderLength)))
                    {
                        continue;
                    }

                    // Si el archivo mide <= 8 KB, la cabecera ya contiene todo el archivo.
                    if (size <= FileSystemService.HeaderSize)
                    {
                        match = candidate.Files;
                        break;
                    }

                    try
                    {
                        // Compara únicamente a partir del byte 8192.
                        if (FileSystemService.CompareByteByByte(
                                entry.Path,
                                candidate.Files[0].Path,
                                _isCancelled,
                                startOffset: FileSystemService.HeaderSize))
                        {
                            match = candidate.Files;
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

                if (size > FileSystemService.HeaderSize
                    && !FileSystemService.MatchesIdentity(entry.Path, entry.Identity, size))
                {
                    Interlocked.Increment(ref _errors);
                    continue;
                }

                if (match is null)
                {
                    var savedHeader = headerSize > 0
                        ? ArrayPool<byte>.Shared.Rent(headerSize)
                        : [];
                    if (headerSize > 0)
                    {
                        entryHeaderSpan.CopyTo(savedHeader);
                    }

                    subGroups.Add((savedHeader, headerSize, [entry]));
                }
                else
                {
                    match.Add(entry);
                }
            }

            foreach (var (_, _, subGroup) in subGroups)
            {
                if (subGroup.Count > 1)
                {
                    groups.Add(new DuplicateGroup
                    {
                        Size = size,
                        DisplayName = Path.GetFileName(subGroup[0].Path),
                        Files = subGroup,
                        ComparedByteByByte = true,
                    });
                }
            }
        }
        finally
        {
            if (headerSize > 0)
            {
                ArrayPool<byte>.Shared.Return(entryHeader);
                foreach (var (header, _, _) in subGroups)
                {
                    ArrayPool<byte>.Shared.Return(header);
                }
            }
        }
    }

    private void ReportEntryProgress(int processed, int total, string phaseName)
    {
        if (processed % 128 != 0 && processed != total)
        {
            return;
        }

        _phase(new PhaseProgress(
            $"{phaseName} ({processed} de {total} archivos)",
            (long)processed * 100 / Math.Max(1, total)));
    }

    private void ReportGroupProgress(int processed, int total, string comparisonText, string elapsed)
    {
        if (processed != 1 && processed % 5 != 0 && processed != total)
        {
            return;
        }

        _progress(new ScanProgress(
            90 + (int)((long)processed * 10 / Math.Max(1, total)),
            $"{comparisonText} {processed} de {total} ({elapsed})"));
        _log($"{comparisonText.TrimEnd('…')} grupo {processed} de {total} ({elapsed})");
    }

    private void WaitWhilePaused()
    {
        while (_isPaused())
        {
            Thread.Sleep(100);
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
}