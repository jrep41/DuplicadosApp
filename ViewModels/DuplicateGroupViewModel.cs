using System.Collections.ObjectModel;
using DuplicadosApp.Models;

namespace DuplicadosApp.ViewModels;

/// <summary>Grupo de archivos duplicados mostrado en la lista de resultados.</summary>
public sealed class DuplicateGroupViewModel : ObservableObject
{
    private readonly ObservableCollection<FileItemViewModel> _items;
    private string _summary = string.Empty;
    private string _recoverableText = string.Empty;

    public DuplicateGroupViewModel(
        DuplicateGroup group,
        int index,
        Action<FileItemViewModel, bool, long>? onSelectionChanged = null)
    {
        Index = index;
        Size = group.Size;
        DisplayName = group.DisplayName;
        SizeText = FileItemViewModel.FormatSize(group.Size);
        Header = $"#{index + 1} · {group.DisplayName}";

        _items = new ObservableCollection<FileItemViewModel>();
        foreach (var file in group.Files)
        {
            _items.Add(new FileItemViewModel(file, this, onSelectionChanged));
        }

        ActualizarResumen();
    }

    public int Index { get; }

    public long Size { get; }

    public string SizeText { get; }

    public string DisplayName { get; }

    public string Header { get; }

    public int FileCount => _items.Count;

    public long RecoverableBytes => _items.Count > 1 ? Size * (_items.Count - 1) : 0;

    /// <summary>Archivos del grupo; es la secuencia que muestra la lista virtualizada.</summary>
    public ObservableCollection<FileItemViewModel> Files => _items;

    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public string RecoverableText
    {
        get => _recoverableText;
        private set => SetProperty(ref _recoverableText, value);
    }

    /// <summary>Comprueba si el grupo coincide con un texto de búsqueda (nombre o cualquier ruta).</summary>
    public bool MatchesFilter(string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        if (DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var file in _items)
        {
            if (file.Path.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Selecciona todas las copias del grupo excepto la elegida por <paramref name="strategy"/>.</summary>
    public void SelectByStrategy(SelectionStrategy strategy)
    {
        if (_items.Count == 0)
        {
            return;
        }

        var keepIndex = ChooseKeepIndex(strategy);
        for (var i = 0; i < _items.Count; i++)
        {
            _items[i].IsSelected = i != keepIndex;
        }
    }

    /// <summary>Conserva únicamente el archivo indicado y marca el resto del grupo para borrar.</summary>
    public void KeepOnly(FileItemViewModel keepItem)
    {
        foreach (var item in _items)
        {
            item.IsSelected = !ReferenceEquals(item, keepItem);
        }
    }

    /// <summary>Quita un archivo del grupo y actualiza los contadores visibles.</summary>
    public void Quitar(FileItemViewModel item)
    {
        if (item.IsSelected)
        {
            item.IsSelected = false;
        }

        _items.Remove(item);
        ActualizarResumen();
    }

    private int ChooseKeepIndex(SelectionStrategy strategy)
    {
        if (strategy == SelectionStrategy.KeepFirst || _items.Count <= 1)
        {
            return 0;
        }

        var bestIndex = 0;
        for (var i = 1; i < _items.Count; i++)
        {
            var current = _items[i];
            var best = _items[bestIndex];

            var isBetter = strategy switch
            {
                SelectionStrategy.KeepOldest => current.LastWriteTimeUtc < best.LastWriteTimeUtc
                    || (current.LastWriteTimeUtc == best.LastWriteTimeUtc && current.Path.Length < best.Path.Length),
                SelectionStrategy.KeepNewest => current.LastWriteTimeUtc > best.LastWriteTimeUtc
                    || (current.LastWriteTimeUtc == best.LastWriteTimeUtc && current.Path.Length < best.Path.Length),
                SelectionStrategy.KeepShortestPath => current.Path.Length < best.Path.Length
                    || (current.Path.Length == best.Path.Length
                        && string.Compare(current.Path, best.Path, StringComparison.OrdinalIgnoreCase) < 0),
                _ => false,
            };

            if (isBetter)
            {
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    private void ActualizarResumen()
    {
        var recuperable = RecoverableBytes;
        RecoverableText = $"+{FileItemViewModel.FormatSize(recuperable)}";
        Summary = $"{_items.Count} copias · {SizeText} c/u · {FileItemViewModel.FormatSize(recuperable)} recuperables";
        OnPropertyChanged(nameof(FileCount));
        OnPropertyChanged(nameof(RecoverableBytes));
    }
}