using System.Buffers;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using DuplicadosApp.Models;
using Microsoft.Win32.SafeHandles;

namespace DuplicadosApp.Services;

/// <summary>
/// Operaciones de sistema de archivos de alto rendimiento: identidad (volumen + índice),
/// huella parcial (cabecera + cola + centro) en una sola apertura, hash completo en flujo,
/// comparación byte a byte acotada por rango y recorrido paralelo del árbol de carpetas.
/// </summary>
public static class FileSystemService
{
    /// <summary>Tamaño de bloque óptimo para lectura secuencial y caché L2 (256 KiB).</summary>
    private const int BufferSize = 256 * 1024;

    /// <summary>Bytes leídos al principio de cada archivo candidato.</summary>
    public const int HeaderSize = 8 * 1024;

    /// <summary>Bytes leídos al final de cada archivo candidato (> FullReadThreshold).</summary>
    public const int TailSize = 8 * 1024;

    /// <summary>Bytes leídos en el punto medio para archivos >= MiddleSampleMinSize.</summary>
    public const int MiddleSampleSize = 4 * 1024;

    /// <summary>Umbral a partir del cual también se muestrea el punto medio (64 KiB).</summary>
    public const long MiddleSampleMinSize = 64 * 1024;

    /// <summary>Los archivos de hasta 16 KiB se leen enteros en una única apertura.</summary>
    public const int FullReadThreshold = HeaderSize + TailSize;

    // Atributos de archivos solo en la nube (OneDrive Files On-Demand / Cloud Filter)
    private const FileAttributes CloudOnlyMask =
        FileAttributes.Offline
        | (FileAttributes)0x00040000  // FILE_ATTRIBUTE_RECALL_ON_OPEN
        | (FileAttributes)0x00400000; // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS

    /// <summary>
    /// Obtiene la identidad del archivo (volumen + índice), su tamaño y fecha de modificación.
    /// Devuelve null si la ruta no es un archivo regular o no se puede abrir.
    /// </summary>
    public static FileEntry? TryGetEntry(string path)
    {
        try
        {
            using var handle = File.OpenHandle(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                FileOptions.None);

            var info = GetInfo(handle);
            if (info is null || (info.Value.Attributes & FileAttributes.Directory) != 0)
            {
                return null;
            }

            return new FileEntry(path, info.Value.Size, info.Value.Identity, info.Value.LastWriteTimeUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Abre el archivo UNA sola vez para verificar su tamaño, obtener su identidad y fecha,
    /// y calcular su huella rápida (archivo completo si &lt;= 16 KiB; cabecera 8 KiB + cola 8 KiB
    /// [+ 4 KiB centrales si &gt;= 64 KiB] si es mayor).
    /// </summary>
    public static FileEntry? TryGetEntryAndFingerprint(
        string path,
        long expectedSize,
        out ContentHash fingerprint,
        out bool isFullHash)
    {
        fingerprint = default;
        isFullHash = false;

        try
        {
            using var handle = File.OpenHandle(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                FileOptions.SequentialScan);

            var info = GetInfo(handle);
            if (info is null
                || (info.Value.Attributes & FileAttributes.Directory) != 0
                || info.Value.Size != expectedSize)
            {
                return null;
            }

            Span<byte> hashBytes = stackalloc byte[32];

            if (expectedSize == 0)
            {
                SHA256.HashData(ReadOnlySpan<byte>.Empty, hashBytes);
                fingerprint = ContentHash.FromBytes(hashBytes);
                isFullHash = true;
                return new FileEntry(path, 0, info.Value.Identity, info.Value.LastWriteTimeUtc);
            }

            if (expectedSize <= FullReadThreshold)
            {
                var length = (int)expectedSize;
                var buffer = ArrayPool<byte>.Shared.Rent(length);
                try
                {
                    var read = ReadAt(handle, buffer.AsSpan(0, length), 0);
                    if (read != length)
                    {
                        return null;
                    }

                    SHA256.HashData(buffer.AsSpan(0, length), hashBytes);
                    fingerprint = ContentHash.FromBytes(hashBytes);
                    isFullHash = true;
                    return new FileEntry(path, info.Value.Size, info.Value.Identity, info.Value.LastWriteTimeUtc);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            // Archivo > 16 KiB: muestrea cabecera (8 KiB) + cola (8 KiB) [+ centro (4 KiB) si >= 64 KiB].
            var includeMiddle = expectedSize >= MiddleSampleMinSize;
            var sampleLength = HeaderSize + TailSize + (includeMiddle ? MiddleSampleSize : 0);
            var sampleBuffer = ArrayPool<byte>.Shared.Rent(sampleLength);
            try
            {
                var headSpan = sampleBuffer.AsSpan(0, HeaderSize);
                if (ReadAt(handle, headSpan, 0) != HeaderSize)
                {
                    return null;
                }

                var tailOffset = expectedSize - TailSize;
                var tailSpan = sampleBuffer.AsSpan(HeaderSize, TailSize);
                if (ReadAt(handle, tailSpan, tailOffset) != TailSize)
                {
                    return null;
                }

                if (includeMiddle)
                {
                    var midOffset = (expectedSize / 2) - (MiddleSampleSize / 2);
                    var midSpan = sampleBuffer.AsSpan(HeaderSize + TailSize, MiddleSampleSize);
                    if (ReadAt(handle, midSpan, midOffset) != MiddleSampleSize)
                    {
                        return null;
                    }
                }

                SHA256.HashData(sampleBuffer.AsSpan(0, sampleLength), hashBytes);
                fingerprint = ContentHash.FromBytes(hashBytes);
                isFullHash = false;
                return new FileEntry(path, info.Value.Size, info.Value.Identity, info.Value.LastWriteTimeUtc);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(sampleBuffer);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Calcula el SHA-256 completo del archivo en flujo (256 KiB por bloque), verificando
    /// su identidad y tamaño con el mismo handle antes y después de leer.
    /// Evita comparaciones O(M²) cuando 3 o más archivos comparten la misma huella rápida.
    /// </summary>
    public static bool TryComputeFullHash(
        FileEntry entry,
        out ContentHash fullHash,
        Func<bool>? isCancelled = null,
        Action? waitIfPaused = null)
    {
        fullHash = default;
        try
        {
            using var handle = File.OpenHandle(
                entry.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                FileOptions.SequentialScan);

            var startInfo = GetInfo(handle);
            if (startInfo is null
                || startInfo.Value.Size != entry.Size
                || (entry.Identity.IsValid && startInfo.Value.Identity != entry.Identity))
            {
                return false;
            }

            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                long offset = 0;
                while (offset < entry.Size)
                {
                    if (isCancelled?.Invoke() == true)
                    {
                        return false;
                    }

                    waitIfPaused?.Invoke();
                    if (isCancelled?.Invoke() == true)
                    {
                        return false;
                    }

                    var toRead = (int)Math.Min(BufferSize, entry.Size - offset);
                    var read = ReadAt(handle, buffer.AsSpan(0, toRead), offset);
                    if (read == 0)
                    {
                        return false;
                    }

                    hasher.AppendData(buffer, 0, read);
                    offset += read;
                }

                // Verifica que no haya cambiado de tamaño ni identidad mientras lo leíamos.
                var endInfo = GetInfo(handle);
                if (endInfo is null
                    || endInfo.Value.Size != entry.Size
                    || (entry.Identity.IsValid && endInfo.Value.Identity != entry.Identity))
                {
                    return false;
                }

                Span<byte> digest = stackalloc byte[32];
                hasher.GetHashAndReset(digest);
                fullHash = ContentHash.FromBytes(digest);
                return true;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Comprueba que la ruta siga apuntando al mismo archivo con el mismo tamaño.</summary>
    public static bool MatchesIdentity(string path, FileIdentity identity, long size)
    {
        var entry = TryGetEntry(path);
        return entry is not null && entry.Identity == identity && entry.Size == size;
    }

    /// <summary>
    /// Compara dos archivos por su contenido exacto byte a byte, verificando en los propios
    /// handles que ambos mantienen el mismo tamaño y que no cambiaron durante la lectura.
    /// Admite <paramref name="startOffset"/> y <paramref name="endOffset"/> para evitar releer
    /// cabecera y cola cuando ya fueron confirmadas.
    /// </summary>
    public static bool CompareByteByByte(
        string pathA,
        string pathB,
        Func<bool>? isCancelled = null,
        long startOffset = 0,
        long? endOffset = null,
        Action? waitIfPaused = null,
        FileIdentity? expectedIdentityA = null,
        FileIdentity? expectedIdentityB = null)
    {
        using var handleA = File.OpenHandle(
            pathA, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            FileOptions.SequentialScan);
        using var handleB = File.OpenHandle(
            pathB, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            FileOptions.SequentialScan);

        var infoA = GetInfo(handleA);
        var infoB = GetInfo(handleB);
        if (infoA is null || infoB is null || infoA.Value.Size != infoB.Value.Size)
        {
            return false;
        }

        if (expectedIdentityA is { IsValid: true } idA && infoA.Value.Identity != idA)
        {
            return false;
        }

        if (expectedIdentityB is { IsValid: true } idB && infoB.Value.Identity != idB)
        {
            return false;
        }

        var fileSize = infoA.Value.Size;
        var limit = endOffset.HasValue ? Math.Min(endOffset.Value, fileSize) : fileSize;
        var offset = startOffset;

        if (offset >= limit)
        {
            return true;
        }

        // Si el rango pendiente es pequeño, no alquila 256 KiB innecesarios.
        var chunkSize = (int)Math.Min(BufferSize, limit - offset);
        if (offset == 0 && limit > HeaderSize)
        {
            if (isCancelled?.Invoke() == true)
            {
                return false;
            }

            var headerA = ArrayPool<byte>.Shared.Rent(HeaderSize);
            var headerB = ArrayPool<byte>.Shared.Rent(HeaderSize);
            try
            {
                var readA = ReadAt(handleA, headerA.AsSpan(0, HeaderSize), 0);
                var readB = ReadAt(handleB, headerB.AsSpan(0, HeaderSize), 0);
                if (readA != readB || !headerA.AsSpan(0, readA).SequenceEqual(headerB.AsSpan(0, readB)))
                {
                    return false;
                }

                offset = readA;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(headerA);
                ArrayPool<byte>.Shared.Return(headerB);
            }
        }

        if (offset >= limit)
        {
            return true;
        }

        chunkSize = (int)Math.Min(BufferSize, limit - offset);
        var bufferA = ArrayPool<byte>.Shared.Rent(chunkSize);
        var bufferB = ArrayPool<byte>.Shared.Rent(chunkSize);
        try
        {
            while (offset < limit)
            {
                if (isCancelled?.Invoke() == true)
                {
                    return false;
                }

                waitIfPaused?.Invoke();
                if (isCancelled?.Invoke() == true)
                {
                    return false;
                }

                var toRead = (int)Math.Min(chunkSize, limit - offset);
                var readA = ReadAt(handleA, bufferA.AsSpan(0, toRead), offset);
                var readB = ReadAt(handleB, bufferB.AsSpan(0, toRead), offset);
                if (readA != readB || readA == 0)
                {
                    return false;
                }

                if (!bufferA.AsSpan(0, readA).SequenceEqual(bufferB.AsSpan(0, readB)))
                {
                    return false;
                }

                offset += readA;
            }

            // Confirma en los mismos handles que ningún archivo cambió de tamaño o identidad durante la lectura.
            var finalA = GetInfo(handleA);
            var finalB = GetInfo(handleB);
            return finalA is not null
                   && finalB is not null
                   && finalA.Value.Size == fileSize
                   && finalB.Value.Size == fileSize
                   && finalA.Value.Identity == infoA.Value.Identity
                   && finalB.Value.Identity == infoB.Value.Identity;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bufferA);
            ArrayPool<byte>.Shared.Return(bufferB);
        }
    }

    /// <summary>
    /// Recorre el árbol de carpetas en paralelo usando una cola de trabajo por directorios.
    /// En SSD/NVMe ofrece aceleraciones de 2x-4x manteniendo la seguridad frente a enlaces simbólicos,
    /// archivos solo en la nube y pausas/cancelaciones instantáneas.
    /// </summary>
    public static (int Folders, int Files, int SkippedCloud) ScanDirectoryParallel(
        string root,
        ScanOptions options,
        Action<string, long, DateTime> onFile,
        Action<string>? onError = null,
        Action<int, int, string>? onProgress = null,
        Action? waitIfPaused = null,
        Func<bool>? isCancelled = null)
    {
        var pendingDirs = new ConcurrentQueue<string>();
        pendingDirs.Enqueue(root);

        var activeOrPending = 1;
        var foldersCount = 1;
        var filesCount = 0;
        var skippedCloud = 0;
        var lastReportTicks = 0L;
        var lastFolderReported = root;

        var workerCount = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
        using var dirAvailable = new SemaphoreSlim(1);

        var enumOptions = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = options.IgnoreHiddenAndSystem
                ? FileAttributes.Hidden | FileAttributes.System
                : 0,
            BufferSize = 64 * 1024,
        };

        void TryReportProgress(string currentDir, bool force = false)
        {
            var now = Environment.TickCount64;
            var prev = Interlocked.Read(ref lastReportTicks);
            if (!force && now - prev < 120)
            {
                return;
            }

            if (force || Interlocked.CompareExchange(ref lastReportTicks, now, prev) == prev)
            {
                Volatile.Write(ref lastFolderReported, currentDir);
                onProgress?.Invoke(
                    Volatile.Read(ref foldersCount),
                    Volatile.Read(ref filesCount),
                    currentDir);
            }
        }

        void Worker()
        {
            while (true)
            {
                if (isCancelled?.Invoke() == true)
                {
                    return;
                }

                waitIfPaused?.Invoke();
                if (isCancelled?.Invoke() == true)
                {
                    return;
                }

                if (!pendingDirs.TryDequeue(out var dir))
                {
                    if (Volatile.Read(ref activeOrPending) <= 0)
                    {
                        return;
                    }

                    // Espera breve a que otro trabajador descubra subcarpetas o termine todo.
                    dirAvailable.Wait(50);
                    continue;
                }

                try
                {
                    ScanSingleDirectory(
                        dir,
                        enumOptions,
                        options,
                        subDir =>
                        {
                            Interlocked.Increment(ref foldersCount);
                            Interlocked.Increment(ref activeOrPending);
                            pendingDirs.Enqueue(subDir);
                            dirAvailable.Release();
                        },
                        (filePath, size, lastWriteUtc) =>
                        {
                            Interlocked.Increment(ref filesCount);
                            onFile(filePath, size, lastWriteUtc);
                        },
                        () => Interlocked.Increment(ref skippedCloud),
                        onError,
                        waitIfPaused,
                        isCancelled);

                    TryReportProgress(dir);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PathTooLongException)
                {
                    onError?.Invoke($"⚠ No se pudo leer {dir}: {ex.Message}");
                }
                finally
                {
                    if (Interlocked.Decrement(ref activeOrPending) == 0)
                    {
                        dirAvailable.Release(workerCount);
                    }
                }
            }
        }

        var workers = new Task[workerCount];
        for (var i = 0; i < workerCount; i++)
        {
            workers[i] = Task.Run(Worker);
        }

        Task.WaitAll(workers);

        var finalFolders = Volatile.Read(ref foldersCount);
        var finalFiles = Volatile.Read(ref filesCount);
        var finalSkippedCloud = Volatile.Read(ref skippedCloud);
        TryReportProgress(Volatile.Read(ref lastFolderReported), force: true);
        return (finalFolders, finalFiles, finalSkippedCloud);
    }

    private static void ScanSingleDirectory(
        string directory,
        EnumerationOptions enumOptions,
        ScanOptions scanOptions,
        Action<string> onSubDirectory,
        Action<string, long, DateTime> onFile,
        Action onSkippedCloud,
        Action<string>? onError,
        Action? waitIfPaused,
        Func<bool>? isCancelled)
    {
        using var enumerator = new SingleDirScanner(
            directory,
            enumOptions,
            scanOptions,
            onSubDirectory,
            onFile,
            onSkippedCloud,
            onError);

        while (true)
        {
            if (isCancelled?.Invoke() == true)
            {
                return;
            }

            waitIfPaused?.Invoke();
            if (isCancelled?.Invoke() == true)
            {
                return;
            }

            if (!enumerator.MoveNext())
            {
                break;
            }
        }
    }

    private sealed class SingleDirScanner : FileSystemEnumerator<byte>
    {
        private readonly string _directory;
        private readonly ScanOptions _scanOptions;
        private readonly Action<string> _onSubDirectory;
        private readonly Action<string, long, DateTime> _onFile;
        private readonly Action _onSkippedCloud;
        private readonly Action<string>? _onError;

        public SingleDirScanner(
            string directory,
            EnumerationOptions options,
            ScanOptions scanOptions,
            Action<string> onSubDirectory,
            Action<string, long, DateTime> onFile,
            Action onSkippedCloud,
            Action<string>? onError)
            : base(directory, options)
        {
            _directory = directory;
            _scanOptions = scanOptions;
            _onSubDirectory = onSubDirectory;
            _onFile = onFile;
            _onSkippedCloud = onSkippedCloud;
            _onError = onError;
        }

        protected override bool ShouldIncludeEntry(ref FileSystemEntry entry)
        {
            var attrs = entry.Attributes;

            if (entry.IsDirectory)
            {
                // Nunca seguimos enlaces simbólicos ni puntos de unión para evitar bucles infinitos.
                if ((attrs & FileAttributes.ReparsePoint) == 0)
                {
                    _onSubDirectory(entry.ToFullPath());
                }

                return false;
            }

            // Evita descargar archivos solo en la nube (OneDrive Files On-Demand).
            if (_scanOptions.SkipCloudOnlyFiles && (attrs & CloudOnlyMask) != 0)
            {
                _onSkippedCloud();
                return false;
            }

            var length = entry.Length;
            if (length < _scanOptions.MinFileSizeBytes)
            {
                return false;
            }

            var fullPath = entry.ToFullPath();

            // Si tiene ReparsePoint (pero no es solo nube), comprobamos si es un enlace simbólico de archivo.
            if ((attrs & FileAttributes.ReparsePoint) != 0 && IsSymbolicLink(fullPath))
            {
                return false;
            }

            _onFile(fullPath, length, entry.LastWriteTimeUtc.UtcDateTime);
            return false;
        }

        protected override bool ShouldRecurseIntoEntry(ref FileSystemEntry entry) => false;

        protected override byte TransformEntry(ref FileSystemEntry entry) => 0;

        protected override bool ContinueOnError(int error)
        {
            _onError?.Invoke($"⚠ No se pudo leer {_directory}: {new Win32Exception(error).Message}");
            return true;
        }
    }

    private static bool IsSymbolicLink(string path)
    {
        try
        {
            return File.ResolveLinkTarget(path, returnFinalTarget: false) is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    internal static int ReadAt(SafeFileHandle handle, Span<byte> buffer, long offset)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = RandomAccess.Read(handle, buffer[total..], offset + total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static (long Size, FileIdentity Identity, FileAttributes Attributes, DateTime LastWriteTimeUtc)? GetInfo(
        SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info))
        {
            return null;
        }

        var size = ((long)info.FileSizeHigh << 32) | info.FileSizeLow;
        var index = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        var fileTime = ((long)(uint)info.LastWriteTime.dwHighDateTime << 32) | (uint)info.LastWriteTime.dwLowDateTime;
        var lastWriteUtc = fileTime > 0 ? DateTime.FromFileTimeUtc(fileTime) : DateTime.MinValue;
        return (size, new FileIdentity(info.VolumeSerialNumber, index), (FileAttributes)info.FileAttributes, lastWriteUtc);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle handle,
        out ByHandleFileInformation info);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}