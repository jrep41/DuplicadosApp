using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace DuplicadosApp.Services;

/// <summary>
/// Envía archivos a la Papelera de reciclaje en lugar de borrarlos para siempre, y
/// comprueba que de verdad llegaron allí.
///
/// Por qué se comprueba: <c>SHFileOperation</c>/<c>IFileOperation</c> devuelven éxito
/// aunque Windows acabe borrando el archivo para siempre. Ocurre, por ejemplo, dentro
/// de un AppContainer (procesos enjaulados): el shell no puede escribir en la Papelera
/// y aplica un borrado permanente sin avisar. El resultado se verifica y, si el
/// archivo no aparece en la Papelera, se considera un error para no destruir nada.
/// </summary>
internal static class RecycleBin
{
    private const uint FOF_ALLOWUNDO = 0x0040;
    private const uint FOF_NOCONFIRMATION = 0x0010;
    private const uint FOF_SILENT = 0x0004;
    private const uint FOF_NOERRORUI = 0x0400;

    private static readonly Guid ClsidFileOperation = new("3ad05575-8857-4850-9277-11b85bdb8e09");
    private static readonly Guid IidShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    /// <summary>SID del usuario actual en caché para evitar fugas de handles nativos en cada consulta.</summary>
    private static readonly string? UserSid = ObtenerSidUsuarioActual();

    private static bool? _disponible;
    private static string? _ubicacionSondeada;
    private static readonly Lock _candado = new();

    private static string? ObtenerSidUsuarioActual()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Margen que se resta al inicio de la operación al filtrar los metadatos ($I) de la
    /// Papelera. Cubre el redondeo de la fecha de escritura en NTFS (resolución ~2 s) y
    /// el desfase con el reloj del sistema.
    /// </summary>
    private static readonly TimeSpan MargenDeFecha = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Comprueba si este entorno puede reciclar de verdad: envía un archivo de sonda
    /// a la Papelera y mira si aparece. El resultado se recuerda por ubicación.
    /// </summary>
    public static bool IsAvailable(string? carpetaDeTrabajo = null)
    {
        var candidatas = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(carpetaDeTrabajo) && Directory.Exists(carpetaDeTrabajo))
        {
            candidatas.Add(carpetaDeTrabajo);
        }

        candidatas.Add(Path.GetTempPath());

        lock (_candado)
        {
            foreach (var candidata in candidatas)
            {
                if (_disponible is { } conocido &&
                    string.Equals(_ubicacionSondeada, candidata, StringComparison.OrdinalIgnoreCase))
                {
                    return conocido;
                }

                var resultado = Sondear(candidata);
                if (resultado is null)
                {
                    continue;
                }

                _disponible = resultado;
                _ubicacionSondeada = candidata;
                return resultado.Value;
            }

            return false;
        }
    }

    /// <summary>Sondea una carpeta concreta. Devuelve null si no se pudo ni escribir.</summary>
    private static bool? Sondear(string carpeta)
    {
        var sonda = Path.Combine(carpeta, $"duplicados-sonda-{Guid.NewGuid():N}.bin");

        try
        {
            File.WriteAllBytes(sonda, new byte[64]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        try
        {
            var antes = ContarEnLaPapelera(sonda);
            if (antes is null)
            {
                return false;
            }

            string? archivoMetadatos = null;
            var inicio = DateTime.UtcNow.Add(-MargenDeFecha);
            var exito = EnviarAlShell(sonda)
                        && BuscarMetadatosEnPapelera(sonda, inicio, out archivoMetadatos)
                        && ContarEnLaPapelera(sonda) is { } despues
                        && despues > antes;

            // Si la sonda llegó a la Papelera, se limpian sus archivos $I y $R para no
            // dejar archivos residuales de prueba en la Papelera del usuario.
            if (exito && archivoMetadatos is not null)
            {
                LimpiarSondaReciclada(archivoMetadatos);
            }

            return exito;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(sonda))
                {
                    File.Delete(sonda);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Da igual: es un archivo temporal de 64 bytes.
            }
        }
    }

    /// <summary>Elimina silenciosamente el par $I/$R generado por la sonda en la Papelera.</summary>
    private static void LimpiarSondaReciclada(string rutaMetadatosI)
    {
        try
        {
            var directorio = Path.GetDirectoryName(rutaMetadatosI);
            var nombreI = Path.GetFileName(rutaMetadatosI);
            if (directorio is not null && nombreI.StartsWith("$I", StringComparison.OrdinalIgnoreCase))
            {
                var rutaDatosR = Path.Combine(directorio, string.Concat("$R", nombreI.AsSpan(2)));
                if (File.Exists(rutaDatosR))
                {
                    File.Delete(rutaDatosR);
                }
            }

            if (File.Exists(rutaMetadatosI))
            {
                File.Delete(rutaMetadatosI);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Si no se puede limpiar la sonda de la Papelera, no afecta al funcionamiento.
        }
    }

    /// <summary>Cuenta los elementos reciclados de la unidad del archivo dado.</summary>
    private static int? ContarEnLaPapelera(string rutaDeReferencia)
    {
        try
        {
            var papelera = ObtenerRutaPapelera(rutaDeReferencia);
            if (papelera is null)
            {
                return null;
            }

            return Directory.Exists(papelera)
                ? Directory.EnumerateFiles(papelera, "$R*", SearchOption.TopDirectoryOnly).Count()
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Mueve un archivo a la Papelera y comprueba si de verdad llegó allí.
    /// </summary>
    /// <returns>
    /// <c>null</c> si el archivo no se movió (sigue en su ruta original o la operación
    /// se abortó); <c>true</c> si se movió y se confirmó su presencia en la Papelera;
    /// <c>false</c> si desapareció de su ruta pero no se pudo confirmar el destino. Este
    /// último caso no es un fallo del borrado: el archivo ya no ocupa su sitio, solo que
    /// no se puede afirmar que se pueda restaurar.
    /// </returns>
    public static bool? MoveToRecycleBin(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        // Instante anterior al envío: acota qué metadatos ($I) pueden ser los de este archivo.
        var inicio = DateTime.UtcNow.Add(-MargenDeFecha);

        if (!EnviarAlShell(path))
        {
            return null;
        }

        return BuscarMetadatosEnPapelera(path, inicio, out _);
    }

    private static bool EnviarAlShell(string path)
    {
        IFileOperation? operacion = null;
        IShellItem? elemento = null;
        try
        {
            var rutaCompleta = Path.GetFullPath(path);
            operacion = CrearOperacion();
            operacion.SetOperationFlags(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI);

            var iid = IidShellItem;
            var codigo = SHCreateItemFromParsingName(rutaCompleta, IntPtr.Zero, ref iid, out elemento);
            if (codigo != 0 || elemento is null)
            {
                return false;
            }

            operacion.DeleteItem(elemento, IntPtr.Zero);
            operacion.PerformOperations();
            operacion.GetAnyOperationsAborted(out var abortado);
            return !abortado && !File.Exists(rutaCompleta);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            if (elemento is not null)
            {
                Marshal.ReleaseComObject(elemento);
            }

            if (operacion is not null)
            {
                Marshal.ReleaseComObject(operacion);
            }
        }
    }

    /// <summary>
    /// Busca entre los metadatos ($I) de la Papelera de la unidad si alguno corresponde a
    /// <paramref name="rutaOriginal"/>. Usa <see cref="DirectoryInfo.EnumerateFiles(string, SearchOption)"/>
    /// para obtener las fechas de modificación directamente del listado de directorio sin
    /// realizar una llamada al sistema adicional por cada archivo.
    /// </summary>
    /// <param name="inicio">
    /// Si se indica, solo se examinan metadatos escritos a partir de ese instante. Acotar
    /// por fecha, en lugar de quedarse con los N más recientes, evita falsos negativos
    /// cuando se reciclan muchos archivos seguidos: el archivo recién reciclado siempre
    /// cae dentro de la ventana, por grande que sea la tanda.
    /// </param>
    private static bool BuscarMetadatosEnPapelera(
        string rutaOriginal,
        DateTime? inicio,
        out string? rutaMetadatosEncontrada)
    {
        rutaMetadatosEncontrada = null;
        try
        {
            var rutaNormalizada = Path.GetFullPath(rutaOriginal);
            var papelera = ObtenerRutaPapelera(rutaNormalizada);
            if (papelera is null || !Directory.Exists(papelera))
            {
                return false;
            }

            var desde = inicio ?? DateTime.MinValue;
            var dirInfo = new DirectoryInfo(papelera);

            // Se ordena de más reciente a más antiguo DENTRO de la ventana temporal: el
            // archivo que se acaba de reciclar es casi siempre el primero que se mira, así
            // que el coste por archivo se queda en una lectura en lugar de recorrer toda la
            // tanda anterior.
            var metadatos = dirInfo
                .EnumerateFiles("$I*", SearchOption.TopDirectoryOnly)
                .Where(archivo => archivo.LastWriteTimeUtc >= desde)
                .OrderByDescending(archivo => archivo.LastWriteTimeUtc);

            foreach (var metadatosArchivo in metadatos)
            {
                if (RutaOriginalDe(metadatosArchivo.FullName) is { } original &&
                    string.Equals(original, rutaNormalizada, StringComparison.OrdinalIgnoreCase))
                {
                    rutaMetadatosEncontrada = metadatosArchivo.FullName;
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Si no se puede comprobar, se devuelve false por seguridad.
        }

        return false;
    }

    private static string? ObtenerRutaPapelera(string rutaArchivo)
    {
        var raiz = Path.GetPathRoot(Path.GetFullPath(rutaArchivo));
        if (raiz is null || UserSid is null)
        {
            return null;
        }

        return Path.Combine(raiz, "$Recycle.Bin", UserSid);
    }

    /// <summary>
    /// Lee la ruta original de un archivo de metadatos de la Papelera ($I).
    /// Formato: 8 bytes de versión, 8 de tamaño, 8 de fecha y la ruta en UTF-16.
    /// La versión 2 añade 4 bytes con la longitud en caracteres antes de la ruta.
    /// </summary>
    private static string? RutaOriginalDe(string rutaMetadatos)
    {
        try
        {
            var bytes = File.ReadAllBytes(rutaMetadatos);
            if (bytes.Length < 28)
            {
                return null;
            }

            var version = BitConverter.ToInt64(bytes, 0);
            var inicio = version >= 2 ? 28 : 24;
            if (inicio >= bytes.Length)
            {
                return null;
            }

            var texto = Encoding.Unicode.GetString(bytes, inicio, bytes.Length - inicio);
            var fin = texto.IndexOf('\0');
            return fin >= 0 ? texto[..fin] : texto;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IFileOperation CrearOperacion()
    {
        var tipo = Type.GetTypeFromCLSID(ClsidFileOperation, throwOnError: true)!;
        return (IFileOperation)Activator.CreateInstance(tipo)!;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(
        string pszPath,
        IntPtr pbc,
        ref Guid riid,
        out IShellItem ppv);

    [ComImport]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);

        void GetParent(out IShellItem ppsi);

        void GetDisplayName(uint sigdnName, out IntPtr ppszName);

        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);

        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    /// <summary>
    /// IFileOperation de shobjidl_core.h. El orden de los métodos importa: es la
    /// tabla virtual de COM y debe coincidir exactamente con la del encabezado.
    /// </summary>
    [ComImport]
    [Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise(IntPtr pfops, out uint pdwCookie);

        void Unadvise(uint dwCookie);

        void SetOperationFlags(uint dwOperationFlags);

        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string pszMessage);

        void SetProgressDialog(IntPtr popd);

        void SetProperties(IntPtr pproparray);

        void SetOwnerWindow(IntPtr hwndOwner);

        void ApplyPropertiesToItem(IShellItem psiItem);

        void ApplyPropertiesToItems(IntPtr punkItems);

        void RenameItem(IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, IntPtr pfopsItem);

        void RenameItems(IntPtr punkItems, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);

        void MoveItem(IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, IntPtr pfopsItem);

        void MoveItems(IntPtr punkItems, IShellItem psiDestinationFolder);

        void CopyItem(IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszCopyName, IntPtr pfopsItem);

        void CopyItems(IntPtr punkItems, IShellItem psiDestinationFolder);

        void DeleteItem(IShellItem psiItem, IntPtr pfopsItem);

        void DeleteItems(IntPtr punkItems);

        void NewItem(IShellItem psiDestinationFolder, uint dwFileAttributes, [MarshalAs(UnmanagedType.LPWStr)] string pszName, [MarshalAs(UnmanagedType.LPWStr)] string pszTemplateName, IntPtr pfopsItem);

        void PerformOperations();

        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool pfAnyOperationsAborted);
    }
}