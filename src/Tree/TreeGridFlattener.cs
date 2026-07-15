using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;

namespace WinUI.TableView;

/// <summary>
/// Projects a tree of <see cref="ITreeGridRow"/> items into a flat
/// <see cref="ObservableCollection{T}"/> suitable as a <see cref="TableView"/> ItemsSource.
///
/// Usage:
/// <code>
///   var flattener = new TreeGridFlattener&lt;MyRow&gt;(r => r.Children);
///   flattener.SetRoots(rootRows);
///   tableView.ItemsSource = flattener.Flat;
/// </code>
/// Toggling any row's <see cref="ITreeGridRow.IsExpanded"/> (e.g. from the
/// <see cref="TableViewTreeColumn"/> chevron) inserts or removes its visible
/// descendants in place — no full rebind, so scroll position and selection of
/// unaffected rows survive.
/// </summary>
public sealed class TreeGridFlattener<T> where T : class, ITreeGridRow, INotifyPropertyChanged
{
    private readonly Func<T, IReadOnlyList<T>> _childrenOf;
    private readonly HashSet<T> _hooked = new();

    public TreeGridFlattener(Func<T, IReadOnlyList<T>> childrenOf)
        => _childrenOf = childrenOf;

    /// <summary>The flat projection — bind this to TableView.ItemsSource.</summary>
    public BulkObservableCollection<T> Flat { get; } = new();

    /// <summary>Replaces the whole tree and rebuilds the flat view.</summary>
    public void SetRoots(IReadOnlyList<T> roots)
    {
        foreach (var row in _hooked)
            row.PropertyChanged -= OnRowPropertyChanged;
        _hooked.Clear();

        // Build the whole projection first, then publish it in one shot. Inserting row by row
        // would emit a notification per row (thousands on a large scan), which leaves a
        // virtualizing panel with a stale layout.
        var flat = new List<T>();
        foreach (var root in roots)
            Collect(root, flat);

        Flat.ReplaceAll(flat);
    }

    /// <summary>Appends <paramref name="row"/> and its visible descendants, hooking each.</summary>
    private void Collect(T row, List<T> into)
    {
        into.Add(row);
        Hook(row);
        if (row.IsExpanded)
        {
            foreach (var child in _childrenOf(row))
                Collect(child, into);
        }
    }

    private void Hook(T row)
    {
        if (_hooked.Add(row))
            row.PropertyChanged += OnRowPropertyChanged;
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ITreeGridRow.IsExpanded) || sender is not T row)
            return;

        var index = Flat.IndexOf(row);
        if (index < 0)
            return;

        // Drop the row's contiguous descendant block first, identified by DEPTH —
        // never by expand state: IsExpanded has already flipped by the time this
        // handler runs (counting "visible" descendants of a now-collapsed row
        // yields zero, which left the children in place and duplicated them on
        // the next expand). Removing up-front also makes expand idempotent.
        //
        // Both the removal and the re-insert go through the batch API: dropping or adding a
        // folder's children one at a time emits a notification per row, and the panel does not
        // reflow cleanly through that storm (it leaves an empty gap where the block used to be).
        var count = 0;
        var next = index + 1;
        while (next + count < Flat.Count && Flat[next + count].Depth > row.Depth)
            count++;

        Flat.RemoveRange(next, count);

        if (row.IsExpanded)
        {
            var block = new List<T>();
            foreach (var child in _childrenOf(row))
                Collect(child, block);

            Flat.InsertRange(index + 1, block);
        }
    }
}
