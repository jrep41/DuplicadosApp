namespace DuplicadosApp.Models;

/// <summary>Archivo encontrado durante el escaneo, con su tamaño e identidad de volumen.</summary>
public sealed record FileEntry(string Path, long Size, FileIdentity Identity);

/// <summary>Grupo de archivos considerados duplicados entre sí.</summary>
public sealed class DuplicateGroup
{
    public required long Size { get; init; }

    /// <summary>Nombre representativo del grupo (el del primer archivo encontrado).</summary>
    public required string DisplayName { get; init; }

    public required IReadOnlyList<FileEntry> Files { get; set; }

    /// <summary>True si el grupo se formó comparando contenido byte a byte.</summary>
    public bool ComparedByteByByte { get; init; }
}

/// <summary>
/// Avance de la acción concreta que se está ejecutando en este momento (recorrido,
/// agrupado, comparación, verificación, envío a la papelera…). Alimenta la barra de
/// progreso del panel «Actividad», que cambia según la fase activa.
/// </summary>
/// <param name="Phase">Nombre de la acción en curso.</param>
/// <param name="Percentage">Avance dentro de esa acción (0-100).</param>
/// <param name="IsIndeterminate">True cuando la acción no puede medirse.</param>
public sealed record PhaseProgress(string Phase, double Percentage, bool IsIndeterminate = false);

/// <summary>Resultado completo de una búsqueda.</summary>
public sealed class ScanResult
{
    public List<DuplicateGroup> Groups { get; } = [];

    public int Errors { get; set; }

    public string GeneralError { get; set; } = string.Empty;

    public bool Cancelled { get; set; }
}

/// <summary>Archivo candidato a borrado, con los datos necesarios para revalidarlo.</summary>
public sealed record DeleteCandidate(
    string Path,
    long Size,
    string Name,
    FileIdentity Identity,
    int GroupIndex,
    bool ComparedByteByByte);

/// <summary>Dónde acabó un archivo al borrarlo.</summary>
public enum DeleteDestination
{
    /// <summary>En la Papelera de reciclaje de Windows.</summary>
    PapeleraDelSistema,

    /// <summary>En la papelera propia de la aplicación (cuando la del sistema no está disponible).</summary>
    PapeleraDeLaApp,
}

/// <summary>
/// Datos que la vista necesita para redactar la confirmación de un borrado.
/// </summary>
/// <param name="Count">Archivos que se van a enviar a la papelera.</param>
/// <param name="ComparesContent">True si el grupo se formó comparando contenido byte a byte.</param>
/// <param name="GroupsKept">Grupos en los que se marcaron todas las copias y se conserva una.</param>
public sealed record DeletePrompt(int Count, bool ComparesContent, int GroupsKept);

/// <summary>Resultado de un borrado.</summary>
public sealed class DeleteOutcome
{
    /// <summary>Archivos que se movieron y se pudieron verificar en la papelera.</summary>
    public int Deleted { get; set; }

    /// <summary>Archivos que no se tocaron: seguían abiertos, cambiaron de identidad o la operación falló.</summary>
    public int Errors { get; set; }

    /// <summary>
    /// Archivos que ya no están en su ruta original pero cuyo destino no se pudo
    /// confirmar. No se cuentan como error: el archivo no está en su sitio, así que no
    /// tiene sentido decir que «no se ha borrado nada». Se informa por separado.
    /// </summary>
    public int SinVerificar { get; set; }

    public bool Cancelled { get; set; }

    /// <summary>Rutas que ya no existen porque se movieron a la papelera, verificadas o no.</summary>
    public List<string> Movidos { get; } = [];

    /// <summary>Dónde se guardaron los archivos borrados.</summary>
    public DeleteDestination Destino { get; set; } = DeleteDestination.PapeleraDelSistema;

    /// <summary>Carpeta de la papelera propia usada, si fue el caso.</summary>
    public string CarpetaPapeleraPropia { get; set; } = string.Empty;
}
