using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace DuplicadosApp.Services;

/// <summary>
/// Envía archivos a la Papelera de reciclaje en lugar de borrarlos para siempre, y
/// comprueba que de verdad llegaron allí.
///
/// Mejoras clave:
/// - Ejecuta <c>IFileOperation</c> en un hilo STA dedicado (requisito COM del shell de Windows).
/// - Mantiene caché de disponibilidad por volumen (evita confundir unidades con y sin Papelera).
/// - Usa sesiones de reciclado con registro de metadatos ($I) ya inspeccionados para
///   verificar cada envío en tiempo O(1) incluso con miles de archivos en la Papelera.
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

    private static readonly Dictionary<string, bool> DisponibilidadPorVolumen = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock Candado = new();

    private static readonly TimeSpan MargenDeFecha = TimeSpan.FromSeconds(15);

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
    /// Comprueba si el volumen de <paramref name="carpetaDeTrabajo"/> puede reciclar de verdad:
    /// envía una sonda y comprueba que aparezca en la Papelera de ese volumen.
    /// El resultado se recuerda por volumen para no mezclar discos locales con unidades USB/red.
    /// </summary>
    public static bool IsAvailable(string? carpetaDeTrabajo = null)
    {
        var carpeta = !string.IsNullOrWhiteSpace(carpetaDeTrabajo) && Directory.Exists(carpetaDeTrabajo)
            ? Path.GetFullPath(carpetaDeTrabajo)
            : Path.GetFullPath(Path.GetTempPath());

        var volumen = Path.GetPathRoot(carpeta);
        if (string.IsNullOrEmpty(volumen))
        {
            return false;
        }

        lock (Candado)
        {
            if (DisponibilidadPorVolumen.TryGetValue(volumen, out var conocido))
            {
                return conocido;
            }

            var resultado = Sondear(carpeta);
            if (resultado is null)
            {
                // Solo probamos Temp si está en el MISMO volumen; nunca asumimos disponibilidad de otro disco.
                var temp = Path.GetFullPath(Path.GetTempPath());
                if (string.Equals(Path.GetPathRoot(temp), volumen, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(temp, carpeta, StringComparison.OrdinalIgnoreCase))
                {
                    resultado = Sondear(temp);
                }
            }

            var disponible = resultado ?? false;
            DisponibilidadPorVolumen[volumen] = disponible;
            return disponible;
        }
    }

    /// <summary>Abre una sesión optimizada (hilo STA persistente + verificación O(1)) para una tanda de borrado.</summary>
    public static RecycleBinSession CreateSession() => new();

    /// <summary>Envía un único archivo a la Papelera y verifica su llegada.</summary>
    public static bool? MoveToRecycleBin(string path)
    {
        using var session = new RecycleBinSession();
        return session.MoveToRecycleBin(path);
    }

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
            var enviado = EjecutarEnSta(() => EnviarAlShellEnHiloActual(sonda));
            var exito = enviado
                        && BuscarMetadatosEnPapelera(sonda, inicio, archivosConocidos: null, out archivoMetadatos)
                        && ContarEnLaPapelera(sonda) is { } despues
                        && despues > antes;

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
                // Archivo temporal de 64 bytes.
            }
        }
    }

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
            // No afecta al funcionamiento.
        }
    }

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

    private static bool EjecutarEnSta(Func<bool> accion)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            return accion();
        }

        var resultado = false;
        var hilo = new Thread(() => resultado = accion())
        {
            IsBackground = true,
            Name = "RecycleBin-STA-Probe",
        };
        hilo.SetApartmentState(ApartmentState.STA);
        hilo.Start();
        hilo.Join();
        return resultado;
    }

    internal static bool EnviarAlShellEnHiloActual(string path)
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

    internal static HashSet<string> CapturarMetadatosActuales(string papelera)
    {
        var conocidos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Directory.Exists(papelera))
            {
                return conocidos;
            }

            using var enumerator = new MetadataFileEnumerator(papelera, DateTime.MinValue);
            while (enumerator.MoveNext())
            {
                conocidos.Add(enumerator.Current.Name);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Se ignora; la verificación seguirá funcionando por ventana temporal.
        }

        return conocidos;
    }

    internal static bool BuscarMetadatosEnPapelera(
        string rutaOriginal,
        DateTime inicio,
        HashSet<string>? archivosConocidos,
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

            var candidatos = new List<(string Name, DateTime LastWriteUtc)>(8);
            using (var enumerator = new MetadataFileEnumerator(papelera, inicio))
            {
                while (enumerator.MoveNext())
                {
                    var item = enumerator.Current;
                    if (archivosConocidos is not null && archivosConocidos.Contains(item.Name))
                    {
                        continue;
                    }

                    candidatos.Add(item);
                }
            }

            candidatos.Sort(static (a, b) => b.LastWriteUtc.CompareTo(a.LastWriteUtc));

            foreach (var (nombre, _) in candidatos)
            {
                var rutaCompletaI = Path.Combine(papelera, nombre);
                if (RutaOriginalDe(rutaCompletaI) is { } original
                    && string.Equals(original, rutaNormalizada, StringComparison.OrdinalIgnoreCase))
                {
                    archivosConocidos?.Add(nombre);
                    rutaMetadatosEncontrada = rutaCompletaI;
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Por seguridad se devuelve false.
        }

        return false;
    }

    internal static string? ObtenerRutaPapelera(string rutaArchivo)
    {
        var raiz = Path.GetPathRoot(Path.GetFullPath(rutaArchivo));
        if (raiz is null || UserSid is null)
        {
            return null;
        }

        return Path.Combine(raiz, "$Recycle.Bin", UserSid);
    }

    /// <summary>
    /// Lee la ruta original de un archivo de metadatos ($I) usando un búfer alquilado
    /// (evita asignaciones de arrays por cada archivo).
    /// </summary>
    private static string? RutaOriginalDe(string rutaMetadatos)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(1024);
        try
        {
            using var handle = File.OpenHandle(
                rutaMetadatos,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            var length = (int)Math.Min(RandomAccess.GetLength(handle), 4096);
            if (length < 28)
            {
                return null;
            }

            if (length > buffer.Length)
            {
                ArrayPool<byte>.Shared.Return(buffer);
                buffer = ArrayPool<byte>.Shared.Rent(length);
            }

            var read = FileSystemService.ReadAt(handle, buffer.AsSpan(0, length), 0);
            if (read < 28)
            {
                return null;
            }

            var version = BitConverter.ToInt64(buffer, 0);
            var inicio = version >= 2 ? 28 : 24;
            if (inicio >= read)
            {
                return null;
            }

            var texto = Encoding.Unicode.GetString(buffer, inicio, read - inicio);
            var fin = texto.IndexOf('\0');
            return fin >= 0 ? texto[..fin] : texto;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static IFileOperation CrearOperacion()
    {
        var tipo = Type.GetTypeFromCLSID(ClsidFileOperation, throwOnError: true)!;
        return (IFileOperation)Activator.CreateInstance(tipo)!;
    }

    /// <summary>
    /// Enumerador sin asignaciones de <c>FileInfo</c> para leer solo los archivos <c>$I*</c>
    /// recientes de la Papelera.
    /// </summary>
    private sealed class MetadataFileEnumerator : FileSystemEnumerator<(string Name, DateTime LastWriteUtc)>
    {
        private readonly DateTime _minWriteUtc;

        public MetadataFileEnumerator(string directory, DateTime minWriteUtc)
            : base(directory, new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false,
                AttributesToSkip = 0,
            })
        {
            _minWriteUtc = minWriteUtc;
        }

        protected override bool ShouldIncludeEntry(ref FileSystemEntry entry)
        {
            if (entry.IsDirectory)
            {
                return false;
            }

            var name = entry.FileName;
            if (name.Length < 3 || name[0] != '$' || (name[1] != 'I' && name[1] != 'i'))
            {
                return false;
            }

            return entry.LastWriteTimeUtc.UtcDateTime >= _minWriteUtc;
        }

        protected override (string Name, DateTime LastWriteUtc) TransformEntry(ref FileSystemEntry entry)
            => (entry.FileName.ToString(), entry.LastWriteTimeUtc.UtcDateTime);
    }

    /// <summary>
    /// Sesión de reciclado con un único hilo STA y tabla de metadatos <c>$I*</c> ya vistos
    /// por volumen para acelerar borrados de cientos o miles de archivos.
    /// </summary>
    internal sealed class RecycleBinSession : IDisposable
    {
        private readonly BlockingCollection<(string Path, TaskCompletionSource<bool> Result)> _queue = new();
        private readonly Dictionary<string, HashSet<string>> _conocidosPorPapelera = new(StringComparer.OrdinalIgnoreCase);
        private readonly Thread _staThread;
        private bool _disposed;

        public RecycleBinSession()
        {
            _staThread = new Thread(ProcessQueue)
            {
                IsBackground = true,
                Name = "RecycleBin-STA-Worker",
            };
            _staThread.SetApartmentState(ApartmentState.STA);
            _staThread.Start();
        }

        public bool? MoveToRecycleBin(string path)
        {
            if (_disposed || !File.Exists(path))
            {
                return null;
            }

            var papelera = ObtenerRutaPapelera(path);
            HashSet<string>? conocidos = null;
            if (papelera is not null && !_conocidosPorPapelera.TryGetValue(papelera, out conocidos))
            {
                conocidos = CapturarMetadatosActuales(papelera);
                _conocidosPorPapelera[papelera] = conocidos;
            }

            var inicio = DateTime.UtcNow.Add(-MargenDeFecha);
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add((path, tcs));
            var enviado = tcs.Task.GetAwaiter().GetResult();
            if (!enviado)
            {
                return null;
            }

            return BuscarMetadatosEnPapelera(path, inicio, conocidos, out _);
        }

        private void ProcessQueue()
        {
            foreach (var (path, tcs) in _queue.GetConsumingEnumerable())
            {
                try
                {
                    tcs.TrySetResult(EnviarAlShellEnHiloActual(path));
                }
                catch
                {
                    tcs.TrySetResult(false);
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _queue.CompleteAdding();
            if (_staThread.IsAlive)
            {
                _staThread.Join(TimeSpan.FromSeconds(5));
            }

            _queue.Dispose();
        }
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