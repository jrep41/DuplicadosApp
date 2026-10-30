namespace DuplicadosApp.Models;

/// <summary>
/// Equivalente a la pareja (st_dev, st_ino) de os.stat: identifica de forma única
/// un archivo dentro del volumen, de modo que se detecta si la ruta fue sustituida.
/// </summary>
public readonly record struct FileIdentity(uint VolumeSerialNumber, ulong FileIndex)
{
    public static FileIdentity Unknown => new(0, 0);
}
