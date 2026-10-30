using System.Diagnostics;
using System.IO;
using DuplicadosApp.Models;

namespace DuplicadosApp.Services;

/// <summary>
/// Borrado seguro de archivos, portado de duplica2.py: antes de eliminar cada
/// archivo se revalida que siga siendo el mismo (identidad y tamaño) y que su
/// contenido siga coincidiendo con el resto del grupo.
///
/// El borrado nunca destruye: envía el archivo a la Papelera de reciclaje de Windows
/// y, cuando esa no está disponible, a una papelera propia de la aplicación.
/// Nunca se usa <see cref="File.Delete(string)"/>.
/// </summary>
public sealed class FileDeleter
{
    private readonly Action<string> _log;
    private readonly Action<string> _activity;
    private readonly Action<PhaseProgress> _phase;
    private readonly Func<bool> _isPaused;
    private readonly Func<bool> _isCancelled;
    private readonly Func<string, bool?> _recycle;
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
        Action<PhaseProgress>? phase = null)
    {
        _log = log;
        _activity = activity ?? (_ => { });
        _phase = phase ?? (_ => { });
        _isPaused = isPaused;
        _isCancelled = isCancelled;
        _recycle = recycle ?? RecycleBin.MoveToRecycleBin;
        _recycleDisponible = recycleDisponible ?? (() => RecycleBin.IsAvailable());
        _crearPapeleraPropia = papeleraPropia;

        // Cuando se inyecta una papelera propia y no hay ninguna forma de reciclar, se
        // usa la propia sin sondear el shell: es la configuración de las pruebas.
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
                $"Enviando {total} archivo(s) a la papelera propia de la aplicación, en " +
                $"{_papeleraPropia.Carpeta}…");
        }
        else if (_recycleDisponible())
        {
            outcome.Destino = DeleteDestination.PapeleraDelSistema;
            _log($"Enviando {total} archivo(s) a la Papelera de reciclaje…");
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

        // Se indexan los candidatos por GroupIndex una sola vez para no recorrer toda
        // la lista en cada archivo durante la verificación de hermanos.
        var candidatesByGroup = new Dictionary<int, List<DeleteCandidate>>();
        foreach (var candidate in allCandidates)
        {
            if (!candidatesByGroup.TryGetValue(candidate.GroupIndex, out var groupList))
            {
                groupList = [];
                candidatesByGroup[candidate.GroupIndex] = groupList;
            }

            groupList.Add(candidate);
        }

        _activity($"Preparando el envío de {total} archivo(s) a la papelera");
        var destino = outcome.Destino == DeleteDestination.PapeleraDeLaApp
            ? "papelera de la aplicación"
            : "Papelera de reciclaje";
        _phase(new PhaseProgress($"Enviando a la {destino} (0 de {total})", 0));

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
                var item = selected[index - 1];
                var nombre = Path.GetFileName(item.Path);
                var justoAntes = (long)(index - 1) * 100 / Math.Max(1, total);
                _phase(new PhaseProgress($"Comprobando «{nombre}» ({index} de {total})", justoAntes));
                _activity($"Comprobando «{nombre}» ({index} de {total})");

                try
                {
                    var current = FileSystemService.TryGetEntry(item.Path);
                    if (current is null || current.Identity != item.Identity || current.Size != item.Size)
                    {
                        outcome.Errors++;
                        continue;
                    }

                    var contentValid = item.ComparedByteByByte
                        ? HasMatchingSibling(item, candidatesByGroup)
                        : string.Equals(
                            Path.GetFileName(item.Path),
                            item.Name,
                            StringComparison.OrdinalIgnoreCase);

                    if (!contentValid || !FileSystemService.MatchesIdentity(item.Path, item.Identity, item.Size))
                    {
                        outcome.Errors++;
                        continue;
                    }

                    _activity($"Guardando «{nombre}» ({index} de {total})");
                    _phase(new PhaseProgress($"Guardando «{nombre}» ({index} de {total})", justoAntes));

                    // null = el archivo sigue en su sitio (no se movió); true = movido y
                    // verificado en la papelera; false = movido pero sin poder confirmar dónde.
                    var movido = Mover(item.Path);
                    if (movido is null)
                    {
                        outcome.Errors++;
                        continue;
                    }

                    outcome.Movidos.Add(item.Path);
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
                    outcome.Errors++;
                }

                if (index % 25 == 0 || index == total)
                {
                    _log(
                        $"Guardados {index} de {total} ({outcome.Deleted} verificados, " +
                        $"{outcome.SinVerificar} sin verificar, {outcome.Errors} errores, " +
                        $"{stopwatch.Elapsed.TotalSeconds:F1}s)");
                }
            }
        }
        finally
        {
            // Se guarda siempre, aunque una excepción interrumpa el bucle: cada archivo ya
            // movido necesita su ruta original en el manifiesto para poder volver a su sitio.
            _papeleraPropia?.GuardarManifiesto();
        }

        _activity("Guardando el manifiesto de la papelera");
        _phase(new PhaseProgress("Guardando el manifiesto de la papelera", 100));
        var nombreDestino = outcome.Destino == DeleteDestination.PapeleraDeLaApp
            ? "papelera de la aplicación"
            : "Papelera de reciclaje";

        _log(
            outcome.Errors > 0 || outcome.SinVerificar > 0
                ? $"⚠ Envío a la {nombreDestino} terminado: {outcome.Deleted} verificado(s), " +
                  $"{outcome.SinVerificar} sin verificar, {outcome.Errors} error(es) en " +
                  $"{stopwatch.Elapsed.TotalSeconds:F1}s"
                : $"Envío a la {nombreDestino} terminado: {outcome.Deleted} movido(s), sin " +
                  $"errores en {stopwatch.Elapsed.TotalSeconds:F1}s");

        return outcome;
    }

    private bool? Mover(string ruta)
    {
        if (_papeleraPropia is not null)
        {
            // La papelera propia no necesita verificación: Guardar solo devuelve true
            // cuando el archivo quedó dentro de su carpeta.
            return _papeleraPropia.Guardar(ruta) ? true : null;
        }

        return _recycle(ruta);
    }

    private bool HasMatchingSibling(
        DeleteCandidate item,
        Dictionary<int, List<DeleteCandidate>> candidatesByGroup)
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

            if (other.Path == item.Path ||
                !other.ComparedByteByByte ||
                other.Size != item.Size)
            {
                continue;
            }

            try
            {
                if (FileSystemService.CompareByteByByte(item.Path, other.Path, _isCancelled))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Se ignora: si ningún hermano se puede comparar, el archivo no se borra.
            }
        }

        return false;
    }

    private void WaitWhilePaused()
    {
        while (_isPaused())
        {
            Thread.Sleep(100);
        }
    }
}