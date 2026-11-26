using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace AmberTree.Models;

/// <summary>
/// Tek seferde toplu güncelleme (tek Reset olayı) destekleyen ObservableCollection.
/// Binlerce dosya/klasör eklenirken veya sıralanırken WPF arayüzünde O(N) olay fırtınasını önler.
/// </summary>
public class BulkObservableCollection<T> : ObservableCollection<T>
{
    public BulkObservableCollection() { }

    public BulkObservableCollection(IEnumerable<T> collection) : base(collection) { }

    public void ReplaceAll(IList<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        if (Items is List<T> list && list.Capacity < items.Count)
        {
            list.Capacity = items.Count;
        }
        for (int i = 0; i < items.Count; i++)
        {
            Items.Add(items[i]);
        }
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
