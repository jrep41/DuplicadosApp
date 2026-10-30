using DuplicadosApp.Models;

namespace DuplicadosApp.ViewModels;

/// <summary>Un archivo dentro de un grupo de duplicados, con su casilla de selección.</summary>
public sealed class FileItemViewModel : ObservableObject
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    private readonly Action<FileItemViewModel, bool, long>? _onSelectionChanged;
    private bool _isSelected;

    public FileItemViewModel(
        FileEntry entry,
        DuplicateGroupViewModel group,
        Action<FileItemViewModel, bool, long>? onSelectionChanged = null)
    {
        Entry = entry;
        Group = group;
        _onSelectionChanged = onSelectionChanged;
    }

    public FileEntry Entry { get; }

    /// <summary>Grupo al que pertenece el archivo (la plantilla lo usa para la cabecera).</summary>
    public DuplicateGroupViewModel Group { get; }

    public bool ComparedByteByByte => Group.ComparedByteByByte;

    public string Path => Entry.Path;

    public long Size => Entry.Size;

    public string Name => System.IO.Path.GetFileName(Entry.Path);

    /// <summary>Reutiliza el texto de tamaño ya calculado por el grupo para no asignarlo por cada fila.</summary>
    public string SizeText => Group.SizeText;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                _onSelectionChanged?.Invoke(this, value, Size);
            }
        }
    }

    public DeleteCandidate ToCandidate() => new(
        Entry.Path,
        Entry.Size,
        Name,
        Entry.Identity,
        Group.Index,
        Group.ComparedByteByByte);

    public static string FormatSize(long bytes)
    {
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.##} {Units[unit]}";
    }
}