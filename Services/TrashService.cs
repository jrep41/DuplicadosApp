using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DuplicadosApp.Services;

/// <summary>
/// Papelera propia de la aplicación: mueve los archivos a una carpeta gestionada por
/// la app, conservando su ruta original en un manifiesto y soportando rutas largas
/// de forma segura.
/// </summary>
public sealed class TrashService
{
    private const string NombreManifiesto = "manifiesto.txt";
    private const int MaxSafePathLength = 245;

    private readonly string _carpeta;
    private readonly StringBuilder _manifiesto = new();

    public TrashService(string carpetaRaiz, DateTime momento)
    {
        _carpeta = RutaUnica(carpetaRaiz, momento);
        _manifiesto.AppendLine("Archivos enviados a la papelera de la aplicacion.");
        _manifiesto.AppendLine($"Fecha: {momento:yyyy-MM-dd HH:mm:ss}");
        _manifiesto.AppendLine("Cada linea indica la ruta original (y la ruta guardada si se acorto por longitud).");
        _manifiesto.AppendLine();
    }

    private static string RutaUnica(string carpetaRaiz, DateTime momento)
    {
        var nombre = momento.ToString("yyyy-MM-dd_HH-mm-ss");
        Directory.CreateDirectory(carpetaRaiz);

        for (var intento = 1; intento < 100; intento++)
        {
            var destino = Path.Combine(carpetaRaiz, intento == 1 ? nombre : $"{nombre}-{intento}");
            var reserva = Path.Combine(carpetaRaiz, $".reserva-{Guid.NewGuid():N}");
            Directory.CreateDirectory(reserva);

            try
            {
                Directory.Move(reserva, destino);
                return destino;
            }
            catch (IOException)
            {
                try
                {
                    Directory.Delete(reserva, recursive: false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Directorio vacío temporal.
                }
            }
        }

        var alternativa = Path.Combine(carpetaRaiz, $"{nombre}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(alternativa);
        return alternativa;
    }

    /// <summary>Carpeta donde se guardan los archivos de esta tanda.</summary>
    public string Carpeta => _carpeta;

    public int Guardados { get; private set; }

    /// <summary>
    /// Guarda un archivo en la papelera propia. Devuelve false si no se pudo (en cuyo
    /// caso el archivo original queda intacto).
    /// </summary>
    public bool Guardar(string rutaOriginal)
    {
        if (string.IsNullOrWhiteSpace(rutaOriginal) || !File.Exists(rutaOriginal))
        {
            return false;
        }

        string? destino = null;
        var eraSoloLectura = false;
        var atributosOriginales = FileAttributes.Normal;

        try
        {
            var (rutaDestino, acortada) = RutaDestino(rutaOriginal);
            destino = rutaDestino;
            Directory.CreateDirectory(Path.GetDirectoryName(destino)!);

            atributosOriginales = File.GetAttributes(rutaOriginal);
            if ((atributosOriginales & FileAttributes.ReadOnly) != 0)
            {
                eraSoloLectura = true;
                File.SetAttributes(rutaOriginal, atributosOriginales & ~FileAttributes.ReadOnly);
            }

            File.Move(rutaOriginal, destino);

            if (eraSoloLectura)
            {
                File.SetAttributes(destino, atributosOriginales);
            }

            if (acortada)
            {
                var relDestino = Path.GetRelativePath(_carpeta, destino);
                _manifiesto.AppendLine($"{rutaOriginal}  ->  {relDestino}");
            }
            else
            {
                _manifiesto.AppendLine(rutaOriginal);
            }

            Guardados++;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PathTooLongException)
        {
            try
            {
                if (File.Exists(rutaOriginal))
                {
                    if (eraSoloLectura)
                    {
                        File.SetAttributes(rutaOriginal, atributosOriginales);
                    }

                    if (destino is not null && File.Exists(destino))
                    {
                        File.SetAttributes(destino, FileAttributes.Normal);
                        File.Delete(destino);
                    }
                }
            }
            catch (Exception)
            {
                // Prioridad a no propagar excepciones en la limpieza de fallo.
            }

            return false;
        }
    }

    public void GuardarManifiesto()
    {
        if (Guardados == 0)
        {
            try
            {
                Directory.Delete(_carpeta, recursive: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Si no está vacía o no se puede borrar, se deja como está.
            }

            return;
        }

        try
        {
            Directory.CreateDirectory(_carpeta);
            var ruta = Path.Combine(_carpeta, NombreManifiesto);
            if (File.Exists(ruta))
            {
                File.AppendAllText(ruta, _manifiesto.ToString(), Encoding.UTF8);
            }
            else
            {
                File.WriteAllText(ruta, _manifiesto.ToString(), Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // El contenido ya está a salvo.
        }
    }

    private (string Destino, bool Acortada) RutaDestino(string rutaOriginal)
    {
        var relativa = RutaRelativa(rutaOriginal);
        var destino = Path.Combine(_carpeta, relativa);
        var acortada = false;

        // Evita PathTooLongException al combinar la carpeta de papelera con rutas profundas.
        if (destino.Length >= MaxSafePathLength)
        {
            acortada = true;
            var dirHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetDirectoryName(rutaOriginal) ?? string.Empty)))[..12];
            var nombreOriginal = Path.GetFileName(rutaOriginal);
            var ext = Path.GetExtension(nombreOriginal);
            var baseName = Path.GetFileNameWithoutExtension(nombreOriginal);
            if (baseName.Length > 48)
            {
                baseName = baseName[..48];
            }

            destino = Path.Combine(_carpeta, "_rutas_largas", dirHash, baseName + ext);
        }

        if (!File.Exists(destino))
        {
            return (destino, acortada);
        }

        var carpeta = Path.GetDirectoryName(destino)!;
        var nombre = Path.GetFileNameWithoutExtension(destino);
        var extension = Path.GetExtension(destino);
        var contador = 2;
        do
        {
            destino = Path.Combine(carpeta, $"{nombre} ({contador}){extension}");
            contador++;
        }
        while (File.Exists(destino));

        return (destino, acortada);
    }

    private static string RutaRelativa(string rutaOriginal)
    {
        var ruta = Path.GetFullPath(rutaOriginal);

        var raiz = Path.GetPathRoot(ruta) ?? string.Empty;
        if (raiz.Length > 0 && ruta.Length > raiz.Length)
        {
            ruta = ruta[raiz.Length..];
        }

        ruta = ruta.Replace(':', '_');

        return ruta.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}