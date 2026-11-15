using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace DataTray.App.ViewModels;

/// <summary>An <see cref="ObservableCollection{T}"/> that can swap its whole content with a single Reset, so a
/// virtualised list rebuilt from thousands of items (a filter change, a trimmed log) redraws once instead of
/// once per item.</summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
