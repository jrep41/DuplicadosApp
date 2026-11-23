using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace DuplicadosApp.ViewModels;

/// <summary>
/// Colección observable con carga masiva en una sola notificación <see cref="NotifyCollectionChangedAction.Reset"/>,
/// evitando miles de actualizaciones individuales de WPF al mostrar miles de grupos.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

