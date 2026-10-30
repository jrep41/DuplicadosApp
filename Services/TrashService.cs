using System.IO;
using System.Text;

namespace DuplicadosApp.Services;

/// <summary>
/// Papelera propia de la aplicación: mueve los archivos a una carpeta gestionada por
/// la app, conservando su ruta original en un manifiesto.
///
/// Existe porque la Papelera de Windows no está disponible en todos los entornos:
/// dentro de un AppContainer el shell no puede escribir en ella y destruiría los
/// archivos de forma permanente.
/// </summary>
public sealed class TrashService
{
    private const string NombreManifiesto = "manifiesto.txt";

    private readonly string _carpeta;
    private readonly StringBuilder _manifiesto = new();

    public TrashService(string carpetaRaiz, DateTime momento)
    {
        _carpeta = RutaUnica(carpetaRaiz, momento);
        _manifiesto.AppendLine("Archivos enviados a la papelera de la aplicacion.");
        _manifiesto.AppendLine($"Fecha: {momento:yyyy-MM-dd HH:mm:ss}");
        _manifiesto.AppendLine("Cada linea es la ruta original de un archivo guardado aqui.");
        _manifiesto.AppendLine();
    }

    /// <summary>
    /// Elige y reserva la carpeta de esta tanda. El nombre tiene resolución de un segundo,
    /// así que dos tandas dentro del mismo segundo compartirían carpeta y la segunda
    /// pisaría el manifiesto de la primera, perdiendo las rutas originales de esos
    /// archivos. La carpeta se crea aquí mismo para que el nombre quede reservado antes de
    /// que otra tanda pueda elegirlo.
    /// </summary>
    private static string RutaUnica(string carpetaRaiz, DateTime momento)
    {
        var nombre = momento.ToString("yyyy-MM-dd_HH-mm-ss");
        Directory.CreateDirectory(carpetaRaiz);

        for (var intento = 1; intento < 100; intento++)
        {
            var destino = Path.Combine(carpetaRaiz, intento == 1 ? nombre : $"{nombre}-{intento}");

            // Se reserva el nombre moviendo un directorio temporal: si el destino ya existe
            // (dos tandas, o dos instancias de la app, en el mismo segundo) el movimiento
            // falla y se prueba el siguiente sufijo. Comprobar con Directory.Exists y crear
            // después no bastaría: CreateDirectory no falla ante un directorio existente.
            var reserva = Path.Combine(carpetaRaiz, $".reserva-{Guid.NewGuid():N}");
            Directory.CreateDirectory(reserva);

            try
            {
                Directory.Move(reserva, destino);
                return destino;
            }
            catch (IOException)
            {
                // Ese nombre ya está reservado por otra tanda.
                try
                {
                    Directory.Delete(reserva, recursive: false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Da igual: es un directorio vacío temporal.
                }
            }
        }

        // Salvaguarda: nunca se comparte carpeta con una tanda anterior.
        var alternativa = Path.Combine(carpetaRaiz, $"{nombre}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(alternativa);
        return alternativa;
    }

    /// <summary>Carpeta donde se guardan los archivos de esta tanda.</summary>
    public string Carpeta => _carpeta;

    public int Guardados { get; private set; }

    /// <summary>
    /// Guarda un archivo en la papelera propia. Devuelve false si no se pudo (en cuyo
    /// caso el llamante debe contar el archivo como error y no ha perdido nada).
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
            destino = RutaDestino(rutaOriginal);
            Directory.CreateDirectory(Path.GetDirectoryName(destino)!);

            // Si el archivo tiene el atributo ReadOnly y se mueve entre volúmenes
            // distintos (p. ej. de G:\ a %LOCALAPPDATA% en C:\), MoveFileEx copia el
            // archivo y falla al eliminar el origen. Quitamos ReadOnly antes de mover
            // y lo restauramos en el destino.
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

            _manifiesto.AppendLine(rutaOriginal);
            Guardados++;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Si falló un movimiento entre unidades a mitad y el archivo original sigue
            // intacto, limpiamos cualquier copia parcial en el destino y restauramos atributos.
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
                // Ignorado: prioridad a no lanzar excepción hacia el bucle de borrado.
            }

            return false;
        }
    }

    /// <summary>
    /// Escribe el manifiesto. Se llama al terminar; si no se puede escribir, los
    /// archivos siguen guardados (solo se pierde la lista de rutas originales).
    /// Se añade al final si ya existe, para no borrar el de una tanda anterior.
    /// </summary>
    public void GuardarManifiesto()
    {
        if (Guardados == 0)
        {
            // No se guardó nada: se retira la carpeta reservada en el constructor para no
            // dejar directorios vacíos en la papelera de la aplicación.
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
            // El contenido ya está a salvo; el manifiesto es una comodidad, no un requisito.
        }
    }

    /// <summary>
    /// Calcula el destino dentro de la papelera conservando la estructura de carpetas
    /// para que los archivos no se pisen entre sí, y añade un sufijo si ya existe.
    /// </summary>
    private string RutaDestino(string rutaOriginal)
    {
        var relativa = RutaRelativa(rutaOriginal);
        var destino = Path.Combine(_carpeta, relativa);

        if (!File.Exists(destino))
        {
            return destino;
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

        return destino;
    }

    /// <summary>
    /// Convierte la ruta absoluta en una relativa utilizable: sin la letra de unidad
    /// ni la barra inicial, y sustituyendo los dos puntos para que sea válida.
    /// </summary>
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