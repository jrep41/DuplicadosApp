using System.Buffers.Binary;

namespace DuplicadosApp.Models;

/// <summary>
/// Equivalente a la pareja (st_dev, st_ino) de os.stat: identifica de forma única
/// un archivo dentro del volumen, de modo que se detecta si la ruta fue sustituida
/// o si dos rutas son enlaces duros (hard links) al mismo archivo físico.
/// </summary>
public readonly record struct FileIdentity(uint VolumeSerialNumber, ulong FileIndex)
{
    public static FileIdentity Unknown => new(0, 0);

    /// <summary>Indica si la identidad contiene datos válidos del sistema de archivos.</summary>
    public bool IsValid => VolumeSerialNumber != 0 || FileIndex != 0;
}

/// <summary>
/// Resumen criptográfico de 256 bits (SHA-256) almacenado por valor sin asignaciones
/// en el montículo, para agrupar candidatos en diccionarios en tiempo O(1).
/// </summary>
public readonly record struct ContentHash(ulong P0, ulong P1, ulong P2, ulong P3)
{
    public static ContentHash FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 32)
        {
            throw new ArgumentException("Se requieren al menos 32 bytes para construir un ContentHash.", nameof(bytes));
        }

        return new ContentHash(
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[0..8]),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..16]),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..24]),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..32]));
    }
}
