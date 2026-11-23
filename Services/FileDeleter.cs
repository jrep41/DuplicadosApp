using System.Diagnostics;
using System.IO;
using DuplicadosApp.Models;

namespace DuplicadosApp.Services;

/// <summary>
/// Borrado seguro de archivos: antes de enviar cada archivo a la papelera se revalida
/// que siga siendo el mismo (identidad y tamaño) y que conserve al menos un hermano
/// intacto con el mismo contenido exacto.
/// Nunca se usa <see cref="File.Delete(string)"/> sobre los archivos del usuario.
/// </summary>
public sealed class FileDeleter
{
    private readonly Action<string> _log;
    private readonly Action<string> _activity;
    private readonly Action<PhaseProgress> _phase;
    private readonly Action<ScanProgress> _progress;
    private readonly Func<bool> _isPaused;
    private readonly Func<bool> _isCancelled;
    private readonly Action? _waitIfPaused;
    private readonly Func<string, bool?>? _customRecycle;
    private readonly Func<bool> _recycleDisponible;
    private readonly Func<TrashService>? _crearPapeleraPropia;
    private readonly bool _usarPapeleraPropia;
    private TrashService? _papeleraPropia;

    public FileDeleter(
        Action<string> log,
        Func<bool> isPaused,
        Func<bool> isCancelled,
        Func<string, bool?>? recycle = null,
        Func<bool>? recycleDisponible = null,
        Func<TrashService>? papeleraPropia = null,
        Action<string>? activity = null,
        Action<PhaseProgress>? phase = null,
        Action<ScanProgress>? progress = null,
        Action? waitIfPaused = null)
    {
        _log = log;
        _activity = activity ?? (_ => { });
        _phase = phase ?? (_ => { });
        _progress = progress ?? (_ => { });
        _isPaused = isPaused;
        _isCancelled = isCancelled;
        _waitIfPaused = waitIfPaused;
        _customRecycle = recycle;
        _recycleDisponible = recycleDisponible ?? (() => RecycleBin.IsAvailable());
        _crearPapeleraPropia = papeleraPropia;

        // Cuando se inyecta una papelera propia sin estrategia de reciclado externa (p. ej. pruebas).
        _usarPapeleraPropia = papeleraPropia is not null && recycle is null && recycleDisponible is null;
    }

    public DeleteOutcome Delete(
        IReadOnlyList<DeleteCandidate> selected,
        IReadOnlyList<DeleteCandidate> allCandidates)
    {
        var outcome = new DeleteOutcome();
        var stopwatch = Stopwatch.StartNew();
        var total = selected.Count;

        if (_usarPapeleraPropia)
        {
            _papeleraPropia = _crearPapeleraPropia!();
            outcome.Destino = DeleteDestination.PapeleraDeLaApp;
            outcome.CarpetaPapeleraPropia = _papeleraPropia.Carpeta;
            _log(
                $"Enviando {total:N0} archivo(s) a la papelera propia de la aplicación, en " +
                $"{_papeleraPropia.Carpeta}…");
        }
        else if (_recycleDisponible())
        {
            outcome.Destino = DeleteDestination.PapeleraDelSistema;
            _log($"Enviando {total:N0} archivo(s) a la Papelera de reciclaje…");
        }
        else
        {
            if (_crearPapeleraPropia is null)
            {
                _log(
                    "✖ No se borró nada: la Papelera de reciclaje no está disponible y no " +
                    "hay papelera propia configurada. No se destruyen archivos sin deshacer.");
                outcome.Errors = total;
                return outcome;
            }

            _papeleraPropia = _crearPapeleraPropia();
            outcome.Destino = DeleteDestination.PapeleraDeLaApp;
            outcome.CarpetaPapeleraPropia = _papeleraPropia.Carpeta;
            _log(
                "⚠ La Papelera de reciclaje no está disponible en este entorno; se usará " +
                $"la papelera propia de la aplicación en {_papeleraPropia.Carpeta}");
        }

        var selectedSet = new HashSet<string>(
            selected.Select(item => item.Path),
            StringComparer.OrdinalIgnoreCase);

        // Indexa por grupo poniendo primero los archivos que NO se van a borrar (los conservados),
        // de modo que la verificación de hermano acierte en la primera comparación.
        var candidatesByGroup = new Dictionary<int, List<DeleteCandidate>>();
        foreach (var candidate in allCandidates)
        {
            if (!candidatesByGroup.TryGetValue(candidate.GroupIndex, out var groupList))
            {
                groupList = [];
                candidatesByGroup[candidate.GroupIndex] = groupList;
            }

            if (!selectedSet.Contains(candidate.Path))
            {
                groupList.Insert(0, candidate);
            }
            else
            {
                groupList.Add(candidate);
            }
        }

        var movidosSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        _activity($"Preparando el envío de {total:N0} archivo(s) a la papelera");
        var destino = outcome.Destino == DeleteDestination.PapeleraDeLaApp
            ? "papelera de la aplicación"
            : "Papelera de reciclaje";
        _phase(new PhaseProgress($"Enviando a la {destino} (0 de {total:N0})", 0));
        _progress(new ScanProgress(0, $"Preparando envío de {total:N0} archivo(s)…"));

        using var recycleSession = _papeleraPropia is null && _customRecycle is null
            ? RecycleBin.CreateSession()
            : null;

        try
        {
            for (var index = 1; index <= total; index++)
            {
                if (_isCancelled())
                {
                    outcome.Cancelled = true;
                    break;
                }

                WaitWhilePaused();
                if (_isCancelled())
                {
                    outcome.Cancelled = true;
                    break;
                }

                var item = selected[index - 1];
                var nombre = Path.GetFileName(item.Path);
                var pctAntes = (double)(index - 1) * 100.0 / Math.Max(1, total);
                _phase(new PhaseProgress($"Comprobando «{nombre}» ({index:N0} de {total:N0})", pctAntes));
                _activity($"Comprobando «{nombre}» ({index:N0} de {total:N0})");
                _progress(new ScanProgress(
                    pctAntes,
                    $"Enviando a la papelera… {index - 1:N0} de {total:N0} ({stopwatch.Elapsed.TotalSeconds:F1}s)"));

                try
                {
                    var current = FileSystemService.TryGetEntry(item.Path);
                    if (current is null || current.Identity != item.Identity || current.Size != item.Size)
                    {
                        _log($"⚠ Omitido «{item.Path}»: el archivo cambió o ya no está accesible.");
                        outcome.Errors++;
                        continue;
                    }

                    // Solo se mueve si al menos un hermano distinto del grupo sigue intacto con contenido idéntico.
                    if (!HasMatchingSibling(item, candidatesByGroup, movidosSet)
                        || !FileSystemService.MatchesIdentity(item.Path, item.Identity, item.Size))
                    {
                        _log($"⚠ Omitido «{item.Path}»: no se pudo confirmar una copia hermana intacta.");
                        outcome.Errors++;
                        continue;
                    }

                    _activity($"Guardando «{nombre}» ({index:N0} de {total:N0})");
                    _phase(new PhaseProgress($"Guardando «{nombre}» ({index:N0} de {total:N0})", pctAntes));

                    var movido = Mover(item.Path, recycleSession);
                    if (movido is null)
                    {
                        _log($"⚠ No se pudo mover «{item.Path}» a la {destino}.");
                        outcome.Errors++;
                        continue;
                    }

                    outcome.Movidos.Add(item.Path);
                    movidosSet.Add(item.Path);
                    if (movido == true)
                    {
                        outcome.Deleted++;
                    }
                    else
                    {
                        outcome.SinVerificar++;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _log($"⚠ Error al procesar «{item.Path}»: {ex.Message}");
                    outcome.Errors++;
                }

                var pctDespues = (double)index * 100.0 / Math.Max(1, total);
                _progress(new ScanProgress(
                    pctDespues,
                    $"Enviando a la papelera… {index:N0} de {total:N0} ({stopwatch.Elapsed.TotalSeconds:F1}s)"));

                if (index % 25 == 0 || index == total)
                {
                    _log(
                        $"Procesados {index:N0} de {total:N0} ({outcome.Deleted:N0} verificados, " +
                        $"{outcome.SinVerificar:N0} sin verificar, {outcome.Errors:N0} errores, " +
                        $"{stopwatch.Elapsed.TotalSeconds:F1}s)");
                }
            }
        }
        finally
        {
            if (_papeleraPropia is not null)
            {
                _activity("Guardando el manifiesto de la papelera");
                _phase(new PhaseProgress("Guardando el manifiesto de la papelera", 100));
                _papeleraPropia.GuardarManifiesto();
            }
        }

        _progress(new ScanProgress(100, "Finalizado"));

        _log(
            outcome.Errors > 0 || outcome.SinVerificar > 0
                ? $"⚠ Envío a la {destino} terminado: {outcome.Deleted:N0} verificado(s), " +
                  $"{outcome.SinVerificar:N0} sin verificar, {outcome.Errors:N0} error(es) en " +
                  $"{stopwatch.Elapsed.TotalSeconds:F1}s"
                : $"Envío a la {destino} terminado: {outcome.Deleted:N0} movido(s), sin " +
                  $"errores en {stopwatch.Elapsed.TotalSeconds:F1}s");

        return outcome;
    }

    private bool? Mover(string ruta, RecycleBin.RecycleBinSession? session)
    {
        if (_papeleraPropia is not null)
        {
            return _papeleraPropia.Guardar(ruta) ? true : null;
        }

        if (_customRecycle is not null)
        {
            return _customRecycle(ruta);
        }

        return session!.MoveToRecycleBin(ruta);
    }

    private bool HasMatchingSibling(
        DeleteCandidate item,
        Dictionary<int, List<DeleteCandidate>> candidatesByGroup,
        HashSet<string> movidosSet)
    {
        if (!candidatesByGroup.TryGetValue(item.GroupIndex, out var siblings))
        {
            return false;
        }

        foreach (var other in siblings)
        {
            if (_isCancelled())
            {
                return false;
            }

            if (string.Equals(other.Path, item.Path, StringComparison.OrdinalIgnoreCase)
                || movidosSet.Contains(other.Path)
                || other.Size != item.Size)
            {
                continue;
            }

            // Si ambos tienen identidad válida idéntica, son enlaces duros al mismo archivo físico.
            if (item.Identity.IsValid && other.Identity.IsValid && item.Identity == other.Identity)
            {
                continue;
            }

            try
            {
                if (FileSystemService.CompareByteByByte(
                        item.Path,
                        other.Path,
                        _isCancelled,
                        waitIfPaused: WaitWhilePaused,
                        expectedIdentityA: item.Identity,
                        expectedIdentityB: other.Identity.IsValid ? other.Identity : null))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Se intenta con el siguiente hermano.
            }
        }

        return false;
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
}