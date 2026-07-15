using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace WinUI.TableView;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> that can apply a whole batch of changes and raise a
/// single <see cref="NotifyCollectionChangedAction.Reset"/> instead of one notification per item.
///
/// Flattening a tree touches a lot of rows at once — a rebuild of a 4,000-row scan, or collapsing
/// a folder with hundreds of children. Emitting a notification per row makes a virtualizing panel
/// process thousands of incremental changes and leaves its layout stale (rows that never reflow,
/// an empty gap where a collapsed block used to be, or a grid that arrives measured at zero
/// height). One Reset lets the panel rebind once, exactly like assigning a fresh list would.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    private bool _suppress;

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!_suppress)
            base.OnCollectionChanged(e);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (!_suppress)
            base.OnPropertyChanged(e);
    }

    /// <summary>Replaces the entire contents, raising one Reset.</summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        _suppress = true;
        try
        {
            Items.Clear();
            foreach (var item in items)
                Items.Add(item);
        }
        finally
        {
            _suppress = false;
        }
        RaiseReset();
    }

    /// <summary>Removes a contiguous run, raising one Reset.</summary>
    public void RemoveRange(int index, int count)
    {
        if (count <= 0)
            return;

        _suppress = true;
        try
        {
            // A default ObservableCollection backs Items with List<T>, whose
            // RemoveRange shifts the tail once instead of once per item (the
            // per-item loop is O(n·k); collapsing a folder near the front of a
            // large flat list is the worst case).
            if (Items is List<T> list)
                list.RemoveRange(index, count);
            else
                for (var i = 0; i < count; i++)
                    Items.RemoveAt(index);
        }
        finally
        {
            _suppress = false;
        }
        RaiseReset();
    }

    /// <summary>Inserts a block at <paramref name="index"/>, raising one Reset.</summary>
    public void InsertRange(int index, IReadOnlyList<T> items)
    {
        if (items.Count == 0)
            return;

        _suppress = true;
        try
        {
            // List<T>.InsertRange shifts the tail once; the per-item fallback
            // shifts it once per inserted row (O(n·k)).
            if (Items is List<T> list)
                list.InsertRange(index, items);
            else
                for (var i = 0; i < items.Count; i++)
                    Items.Insert(index + i, items[i]);
        }
        finally
        {
            _suppress = false;
        }
        RaiseReset();
    }

    private void RaiseReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
