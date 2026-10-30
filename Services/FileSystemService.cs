using System.Buffers;
using System.ComponentModel;
using System.IO;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using DuplicadosApp.Models;
using Microsoft.Win32.SafeHandles;

namespace DuplicadosApp.Services;

/// <summary>
/// Operaciones de sistema de archivos equivalentes a las usadas por duplica2.py:
/// identidad (st_dev, st_ino), tamaño, hash SHA-256 y comparación byte a byte.
/// </summary>
public static class FileSystemService
{
    private const int BufferSize = 1024 * 1024;

    /// <summary>
    /// Tamaño de la cabecera que se compara primero. Si dos archivos difieren en sus
    /// primeros 8 KB, es imposible que sean idénticos, así que no hace falta recorrer
    /// el resto: la mayoría de candidatos se descartan leyendo solo un bloque pequeño.
    /// </summary>
    public const int HeaderSize = 8 * 1024;

    /// <summary>
    /// Obtiene la identidad del archivo (volumen + índice) y su tamaño.
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

            return new FileEntry(path, info.Value.Size, info.Value.Identity);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Abre el archivo una sola vez para obtener su identidad, verificar su tamaño y leer
    /// su cabecera (hasta <see cref="HeaderSize"/> bytes). Evita abrir el archivo por
    /// separado para medir la identidad y luego otra vez para leer los primeros 8 KB.
    /// </summary>
    public static FileEntry? TryGetEntryAndHeader(
        string path,
        long expectedSize,
        Span<byte> headerBuffer,
        out int bytesRead)
    {
        bytesRead = 0;
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

            var toRead = (int)Math.Min(headerBuffer.Length, expectedSize);
            if (toRead > 0)
            {
                bytesRead = ReadAt(handle, headerBuffer[..toRead], 0);
                if (bytesRead != toRead)
                {
                    return null;
                }
            }

            return new FileEntry(path, info.Value.Size, info.Value.Identity);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Comprueba que la ruta siga apuntando al mismo archivo con el mismo tamaño.</summary>
    public static bool MatchesIdentity(string path, FileIdentity identity, long size)
    {
        var entry = TryGetEntry(path);
        return entry is not null && entry.Identity == identity && entry.Size == size;
    }

    /// <summary>
    /// Compara dos archivos por su contenido exacto, sin calcular hashes.
    /// Si <paramref name="startOffset"/> es 0, compara primero los primeros 8 KB antes de
    /// alquilar búferes de 1 MiB. Si <paramref name="startOffset"/> es mayor que 0,
    /// continúa la lectura directamente desde ese desplazamiento sin releer la cabecera.
    /// </summary>
    public static bool CompareByteByByte(
        string pathA,
        string pathB,
        Func<bool>? isCancelled = null,
        long startOffset = 0)
    {
        using var handleA = File.OpenHandle(
            pathA, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            FileOptions.SequentialScan);
        using var handleB = File.OpenHandle(
            pathB, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            FileOptions.SequentialScan);

        var offset = startOffset;

        if (offset == 0)
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

                if (readA < HeaderSize)
                {
                    return true;
                }

                offset = readA;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(headerA);
                ArrayPool<byte>.Shared.Return(headerB);
            }
        }

        var bufferA = ArrayPool<byte>.Shared.Rent(BufferSize);
        var bufferB = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (true)
            {
                if (isCancelled?.Invoke() == true)
                {
                    return false;
                }

                var readA = ReadAt(handleA, bufferA.AsSpan(0, BufferSize), offset);
                var readB = ReadAt(handleB, bufferB.AsSpan(0, BufferSize), offset);
                if (readA != readB)
                {
                    return false;
                }

                if (readA == 0)
                {
                    return true;
                }

                if (!bufferA.AsSpan(0, readA).SequenceEqual(bufferB.AsSpan(0, readB)))
                {
                    return false;
                }

                offset += readA;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bufferA);
            ArrayPool<byte>.Shared.Return(bufferB);
        }
    }

    /// <summary>
    /// Recorre el árbol de carpetas devolviendo la ruta y el tamaño de cada archivo.
    /// </summary>
    public static IEnumerable<(string Path, long Size)> EnumerateFiles(
        string root,
        Action<string>? onError = null,
        Action<int, int, string>? onProgress = null,
        Func<bool>? isPaused = null,
        Func<bool>? isCancelled = null)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = 0,
            BufferSize = 64 * 1024,
        };

        var folders = 0;
        var files = 0;
        var lastFolders = -1;

        using var enumerator = new FileScanner(
            root,
            options,
            onError,
            () => folders++,
            () => files++);

        while (true)
        {
            if (isCancelled?.Invoke() == true)
            {
                yield break;
            }

            WaitWhilePaused(isPaused);

            (string Path, long Size) entry;
            try
            {
                if (!enumerator.MoveNext())
                {
                    break;
                }

                entry = enumerator.Current;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                onError?.Invoke($"⚠ No se pudo leer el contenido de {root}: {ex.Message}");
                break;
            }

            if (folders != lastFolders)
            {
                lastFolders = folders;
                onProgress?.Invoke(folders, files, enumerator.CurrentDirectory);
            }

            yield return entry;
        }

        onProgress?.Invoke(folders, files, enumerator.CurrentDirectory);
    }

    private sealed class FileScanner : FileSystemEnumerator<(string Path, long Size)>
    {
        private readonly Action<string>? _onError;
        private readonly Action _onFolder;
        private readonly Action _onFile;
        private readonly string _root;
        private string _currentDirectory;

        public string CurrentDirectory => _currentDirectory;

        public FileScanner(
            string root,
            EnumerationOptions options,
            Action<string>? onError,
            Action onFolder,
            Action onFile)
            : base(root, options)
        {
            _root = root;
            _currentDirectory = root;
            _onError = onError;
            _onFolder = onFolder;
            _onFile = onFile;
        }

        protected override bool ShouldIncludeEntry(ref FileSystemEntry entry)
        {
            if (!entry.Directory.SequenceEqual(_currentDirectory))
            {
                _currentDirectory = entry.Directory.ToString();
            }

            if (entry.IsDirectory)
            {
                _onFolder();
                return false;
            }

            _onFile();
            return true;
        }

        protected override bool ShouldRecurseIntoEntry(ref FileSystemEntry entry) =>
            (entry.Attributes & FileAttributes.ReparsePoint) == 0;

        protected override (string Path, long Size) TransformEntry(ref FileSystemEntry entry) =>
            (entry.ToFullPath(), entry.Length);

        protected override bool ContinueOnError(int error)
        {
            var donde = _currentDirectory.Length > 0 ? _currentDirectory : _root;
            _onError?.Invoke($"⚠ No se pudo leer {donde}: {new Win32Exception(error).Message}");
            return true;
        }
    }

    private static void WaitWhilePaused(Func<bool>? isPaused)
    {
        while (isPaused?.Invoke() == true)
        {
            Thread.Sleep(100);
        }
    }

    private static int ReadAt(SafeFileHandle handle, Span<byte> buffer, long offset)
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

    private static (long Size, FileIdentity Identity, FileAttributes Attributes)? GetInfo(
        SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info))
        {
            return null;
        }

        var size = ((long)info.FileSizeHigh << 32) | info.FileSizeLow;
        var index = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        return (size, new FileIdentity(info.VolumeSerialNumber, index), (FileAttributes)info.FileAttributes);
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