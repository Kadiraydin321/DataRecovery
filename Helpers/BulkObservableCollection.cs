using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace DataRecoveryApp.Helpers;

/// <summary>
/// Binlerce öğeyi tek bir Reset bildirimiyle ekleyen ObservableCollection.
/// Standart Add() her öğe için ayrı CollectionChanged tetikler ve DataGrid'i dondurur.
/// </summary>
public class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var i in items) Items.Add(i);
        RaiseReset();
    }

    public void AddRange(IEnumerable<T> items)
    {
        bool any = false;
        foreach (var i in items) { Items.Add(i); any = true; }
        if (any) RaiseReset();
    }

    private void RaiseReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
