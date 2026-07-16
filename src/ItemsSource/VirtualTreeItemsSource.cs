// FOBO fork addition.
//
// The tree-shaped sibling of <see cref="SqlBackedItemsSource"/>: an
// <see cref="ITableViewItemsSource"/> over a <see cref="VirtualTreeModel"/>,
// for tree grids whose group rows fit in memory while leaf rows are fetched
// windowed from a backing store (SQL paging). Pair with
// <see cref="TableViewTreeColumn"/> for the indent/chevron rendering; the
// group objects implement <see cref="ITreeGridRow"/> and their IsExpanded
// toggles flow through the model into insert/remove notifications here.
//
// Like SqlBackedItemsSource, this implements only what TableView actually
// exercises: read-only IList access, Count, and the change-notification
// plumbing. Sort and filter descriptions are accepted for API symmetry but
// ignored — hosts re-shape the tree by rebuilding the model's skeleton
// (search pushdown, regrouping) rather than through column headers.

using Microsoft.UI.Xaml.Data;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Windows.Foundation;
using Windows.Foundation.Collections;

namespace WinUI.TableView;

/// <summary>
/// A virtualized tree items source: in-memory group skeleton, windowed
/// on-demand leaf pages. Wraps <see cref="VirtualTreeModel"/> — construct
/// the model, hand it here, assign to <see cref="TableView.ItemsSource"/>.
/// </summary>
public sealed class VirtualTreeItemsSource : ITableViewItemsSource, IList
{
    private readonly VirtualTreeModel _model;

    private uint _deferCounter;
    private bool _refreshPendingFromDefer;

    public VirtualTreeItemsSource(VirtualTreeModel model)
    {
        _model = model;
        _model.ResetRaised += OnModelReset;
        _model.RangeInserted += OnModelRangeInserted;
        _model.RangeRemoved += OnModelRangeRemoved;
        _model.ItemReplaced += OnModelItemReplaced;
        _model.LeafPropertyChanged += OnModelLeafPropertyChanged;
    }

    /// <summary>The underlying model, for host-side structure updates.</summary>
    public VirtualTreeModel Model => _model;

    // ── Model change translation ────────────────────────────────────

    private static readonly bool Trace = Environment.GetEnvironmentVariable("TREEGRID_TRACE") == "1";

    private void OnModelReset()
    {
        if (Trace) Console.WriteLine("[vtis] Reset");
        RaisePropertyChanged(nameof(Count));
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        VectorChanged?.Invoke(this, new VectorChangedEventArgs(CollectionChange.Reset));
    }

    private void OnModelRangeInserted(int index, int count)
    {
        if (Trace) Console.WriteLine($"[vtis] Insert {index}+{count}");
        RaisePropertyChanged(nameof(Count));
        for (var i = 0; i < count; i++)
        {
            var at = index + i;
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Add, _model.PeekAt(at), at));
            VectorChanged?.Invoke(this, new VectorChangedEventArgs(CollectionChange.ItemInserted, at));
        }
    }

    /// <summary>Stands in for the (already discarded) row in INCC Remove events. The Remove
    /// ctor rejects null; consumers on both platforms drive off the index — Uno's TableView
    /// hookup listens to VectorChanged, WinUI's ListView re-reads by index — so the item
    /// payload is never dereferenced (same fidelity SqlBackedItemsSource ships with).</summary>
    private static readonly object RemovedRow = new();

    private void OnModelRangeRemoved(int index, int count)
    {
        if (Trace) Console.WriteLine($"[vtis] Remove {index}+{count}");
        RaisePropertyChanged(nameof(Count));
        // Highest index first so each removal's index stays valid.
        for (var i = count - 1; i >= 0; i--)
        {
            var at = index + i;
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Remove, RemovedRow, at));
            VectorChanged?.Invoke(this, new VectorChangedEventArgs(CollectionChange.ItemRemoved, at));
        }
    }

    private void OnModelItemReplaced(int index, object newItem, object oldItem)
    {
        VectorChanged?.Invoke(this, new VectorChangedEventArgs(CollectionChange.ItemChanged, index, newItem));
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Replace, newItem, oldItem, index));
    }

    private void OnModelLeafPropertyChanged(object? sender, PropertyChangedEventArgs e)
        => ItemPropertyChanged?.Invoke(sender, e);

    // ── ITableViewItemsSource ───────────────────────────────────────

    /// <summary>
    /// Always throws — structure comes from the model's skeleton, not an
    /// in-memory enumerable.
    /// </summary>
    public IEnumerable Source
    {
        get => throw new NotSupportedException(
            "VirtualTreeItemsSource has no in-memory source; configure the VirtualTreeModel instead.");
        set => throw new NotSupportedException(
            "VirtualTreeItemsSource has no in-memory source; configure the VirtualTreeModel instead.");
    }

    /// <summary>Accepted for API symmetry; column-driven sort is not supported on tree sources.</summary>
    public IList<SortDescription> SortDescriptions { get; } = new ObservableCollection<SortDescription>();

    /// <summary>Accepted for API symmetry; filtering happens host-side via skeleton rebuilds.</summary>
    public IList<FilterDescription> FilterDescriptions { get; } = new ObservableCollection<FilterDescription>();

    /// <summary>No-op: leaves are not held in memory, there is no view to live-shape.</summary>
    public bool AllowLiveShaping { get; set; }

    public Deferral DeferRefresh()
    {
        _deferCounter++;
        return new Deferral(() =>
        {
            if (_deferCounter == 0) return;
            _deferCounter--;
            if (_deferCounter == 0 && _refreshPendingFromDefer)
            {
                _refreshPendingFromDefer = false;
                Refresh();
            }
        });
    }

    public void Refresh()
    {
        if (_deferCounter > 0)
        {
            _refreshPendingFromDefer = true;
            return;
        }
        _model.Refresh();
    }

    public void RefreshSorting() { /* column sort unsupported on tree sources */ }

    public void RefreshFilter() { /* filtering is host-side (skeleton rebuild) */ }

    public event PropertyChangedEventHandler? ItemPropertyChanged;

    // ── IList<object?> ─────────────────────────────────────────────

    public int Count => _model.Count;

    public bool IsReadOnly => true;

    public object? this[int index]
    {
        get => _model.GetAt(index);
        set => throw new NotSupportedException("VirtualTreeItemsSource is read-only.");
    }

    public bool Contains(object? item) => IndexOf(item) >= 0;

    public int IndexOf(object? item) => _model.IndexOf(item);

    public void Add(object? item) => throw new NotSupportedException("VirtualTreeItemsSource is read-only.");
    public void Clear() => throw new NotSupportedException("VirtualTreeItemsSource is read-only.");
    public void Insert(int index, object? item) => throw new NotSupportedException("VirtualTreeItemsSource is read-only.");
    public bool Remove(object? item) => throw new NotSupportedException("VirtualTreeItemsSource is read-only.");
    public void RemoveAt(int index) => throw new NotSupportedException("VirtualTreeItemsSource is read-only.");

    public void CopyTo(object?[] array, int arrayIndex)
    {
        // PeekAt: this runs from base WinUI container-prep code; a
        // fetch-triggering copy would recurse via Replace events (see
        // SqlBackedItemsSource.PeekAt).
        for (var i = 0; i < Count && arrayIndex + i < array.Length; i++)
            array[arrayIndex + i] = _model.PeekAt(i);
    }

    public IEnumerator<object?> GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
            yield return _model.PeekAt(i);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // ── non-generic IList ──────────────────────────────────────────
    //
    // Uno's ItemsControl.ItemFromIndex resolves items via
    // EnumerableExtensions.ElementAt(IEnumerable, int), whose O(1) fast
    // path needs non-generic IList — the generic IList<object> that
    // ICollectionView brings is NOT checked. Without this, every
    // IndexFromContainer at flat index N walked the enumerator through N
    // rows of the model (creating placeholders along the way): fast
    // scrolling cost O(offset) per realized row and froze the UI thread
    // for ~0.5s per fling. The members below mirror the generic surface.

    bool IList.IsFixedSize => false;
    bool IList.IsReadOnly => true;
    bool ICollection.IsSynchronized => false;
    object ICollection.SyncRoot => this;

    // PeekAt, NOT GetAt: this indexer serves Uno's per-container identity checks
    // (ItemFromIndex), which probe far more indices than the viewport shows. A
    // fetch-triggering read here floods the fetch queue (~20 junk pages per scroll
    // hop) and starves the viewport's own fill. Fetches are driven by the generic
    // indexer / explicit GetAt calls, exactly as before this fast path existed.
    object? IList.this[int index]
    {
        get => _model.PeekAt(index);
        set => throw new NotSupportedException("VirtualTreeItemsSource is read-only.");
    }

    int IList.Add(object? item) => throw new NotSupportedException("VirtualTreeItemsSource is read-only.");
    void IList.Remove(object? item) => throw new NotSupportedException("VirtualTreeItemsSource is read-only.");

    void ICollection.CopyTo(Array array, int index)
    {
        for (var i = 0; i < Count && index + i < array.Length; i++)
            array.SetValue(_model.PeekAt(i), index + i);
    }

    // ── ICollectionView (current item — degenerate) ─────────────────

    public object? CurrentItem => null;
    public int CurrentPosition => -1;
    public bool IsCurrentBeforeFirst => true;
    public bool IsCurrentAfterLast => true;
    public IObservableVector<object?>? CollectionGroups => null;

    public bool MoveCurrentTo(object? item) => false;
    public bool MoveCurrentToFirst() => false;
    public bool MoveCurrentToLast() => false;
    public bool MoveCurrentToNext() => false;
    public bool MoveCurrentToPrevious() => false;
    public bool MoveCurrentToPosition(int index) => false;

    public event CurrentChangingEventHandler? CurrentChanging
    {
        add { /* current-item tracking is unused for virtual tree grids */ }
        remove { }
    }
    public event EventHandler<object>? CurrentChanged
    {
        add { /* current-item tracking is unused for virtual tree grids */ }
        remove { }
    }

    // ── ISupportIncrementalLoading ─────────────────────────────────

    /// <summary>Always false — Count is authoritative; paging is random-access.</summary>
    public bool HasMoreItems => false;

    public IAsyncOperation<LoadMoreItemsResult>? LoadMoreItemsAsync(uint count) => null;

    // ── Change notifications ───────────────────────────────────────

    public event NotifyCollectionChangedEventHandler? CollectionChanged;
    public event VectorChangedEventHandler<object>? VectorChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    private void RaisePropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
