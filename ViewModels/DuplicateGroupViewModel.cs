using System.Collections.ObjectModel;
using DuplicadosApp.Models;

namespace DuplicadosApp.ViewModels;

/// <summary>Grupo de archivos duplicados mostrado en la lista de resultados.</summary>
public sealed class DuplicateGroupViewModel : ObservableObject
{
    private readonly ObservableCollection<FileItemViewModel> _items;
    private string _summary = string.Empty;

    public DuplicateGroupViewModel(
        DuplicateGroup group,
        int index,
        Action<FileItemViewModel, bool, long>? onSelectionChanged = null)
    {
        Index = index;
        Size = group.Size;
        ComparedByteByByte = group.ComparedByteByByte;
        DisplayName = group.DisplayName;
        SizeText = FileItemViewModel.FormatSize(group.Size);
        Header = $"Grupo de duplicados {index + 1}: {group.DisplayName}";

        _items = new ObservableCollection<FileItemViewModel>();
        foreach (var file in group.Files)
        {
            _items.Add(new FileItemViewModel(file, this, onSelectionChanged));
        }

        Summary = ConstruirResumen();
    }

    public int Index { get; }

    public long Size { get; }

    public string SizeText { get; }

    public bool ComparedByteByByte { get; }

    public string DisplayName { get; }

    /// <summary>
    /// Clave de agrupación para la lista virtualizada. Devuelve la propia instancia,
    /// de modo que cada grupo es su propio encabezado y se compara por referencia.
    /// </summary>
    public object GroupKey => this;

    public string Header { get; }

    /// <summary>Archivos del grupo; es la secuencia que muestra la lista virtualizada.</summary>
    public IReadOnlyList<FileItemViewModel> Files => _items;

    /// <summary>
    /// Quita un archivo del grupo (por ejemplo, porque ya se movió a la papelera). Al ser
    /// una colección observable, la vista deja de mostrarlo sin reconstruir la lista.
    /// </summary>
    public void Quitar(FileItemViewModel item)
    {
        if (item.IsSelected)
        {
            // Desmarcarlo primero mantiene los totales de selección en sintonía con la lista.
            item.IsSelected = false;
        }

        _items.Remove(item);

        // El resumen cuenta archivos y espacio recuperable: si no se recalcula, la cabecera
        // del grupo sigue prometiendo lo que había antes de borrar.
        Summary = ConstruirResumen();
    }

    private string ConstruirResumen()
    {
        var recuperable = _items.Count > 1 ? Size * (_items.Count - 1) : 0;
        return
            $"{_items.Count} archivo(s) · {SizeText} cada uno · " +
            $"espacio recuperable {FileItemViewModel.FormatSize(recuperable)}";
    }

    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }
}