// FOBO fork addition.
//
// A virtualized <see cref="ITableViewItemsSource"/> for very large data
// sets that live in a remote / on-disk store. The host application
// supplies two callbacks — one returning the total row count for a given
// query, one returning a contiguous page of rows — and this class takes
// care of:
//
//   * reporting Count up-front so ListView's UI virtualization sizes
//     the scrollbar correctly,
//   * returning a placeholder sentinel for indices not yet fetched,
//   * fetching pages on demand in fixed-size chunks (default 200 rows),
//   * keeping a small LRU window of recently-used pages in memory and
//     evicting older ones,
//   * raising VectorChanged(Replace) when a page lands so the bound
//     row containers swap their placeholders for real data.
//
// The class deliberately covers only what <see cref="TableView"/>
// actually exercises: read-only access via the IList<object?> indexer,
// Count, sort descriptions translated to a host-side query, and the
// usual change-notification + property-changed plumbing. Everything
// else on the ICollectionView surface (insert, remove, current-item
// tracking, incremental loading, collection groups) is either a no-op
// or throws — those features only make sense for an in-memory source.
//
// Filter descriptions are accepted for API symmetry but ignored in
// this first cut; the SQL-backed filter UX is delivered separately
// by an extension to <see cref="IColumnFilterHandler"/>.

using Microsoft.UI.Xaml.Data;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Foundation.Collections;

namespace WinUI.TableView;

/// <summary>
/// An <see cref="ITableViewItemsSource"/> that fetches rows on demand
/// from a host-supplied callback (typically a SQL query). Reports the
/// total row count up-front, returns a placeholder for not-yet-fetched
/// indices, and raises <see cref="VectorChanged"/> as pages arrive.
/// </summary>
/// <remarks>
/// <para>
/// Typical usage from a host application:
/// </para>
/// <code>
/// var source = new SqlBackedItemsSource(
///     countAsync: (query, ct)             =&gt; recordStore.CountAsync(formId, ToRecordQuery(query), ct),
///     pageAsync:  (query, offset, lim, ct) =&gt; recordStore.PageAsync (formId, ToRecordQuery(query), offset, lim, ct));
/// tableView.ItemsSource = source;
/// </code>
/// <para>
/// The host is responsible for translating <see cref="SqlBackedItemsSource.Query"/>
/// (which carries only sort descriptions today; filters arrive in a
/// later phase) into whatever store-specific query type it uses.
/// </para>
/// <para>
/// Cancellation: a <see cref="Refresh"/> or sort change cancels any
/// in-flight count or page fetch, so callbacks should respect the
/// supplied <see cref="CancellationToken"/>.
/// </para>
/// </remarks>
public sealed class SqlBackedItemsSource : ITableViewItemsSource
{
    /// <summary>
    /// The row-shape data the host needs in order to build a SQL query.
    /// Filter descriptions are passed through verbatim; this first cut
    /// of the SQL-backed source ignores them, but the host can opt to
    /// honour them once a structured filter API ships.
    /// </summary>
    public sealed record Query(
        IReadOnlyList<SortDescription>   Sorts,
        IReadOnlyList<FilterDescription> Filters);

    /// <summary>Returns the total row count for the given query.</summary>
    public delegate Task<int> CountAsyncFn(Query query, CancellationToken ct);

    /// <summary>
    /// Returns a contiguous page of rows for the given query. The host
    /// must return at most <paramref name="limit"/> items; returning
    /// fewer is treated as end-of-data for that page only (the next
    /// page may still have rows because <c>Count</c> is authoritative).
    /// </summary>
    public delegate Task<IReadOnlyList<object>> PageAsyncFn(
        Query query, int offset, int limit, CancellationToken ct);

    // ── Sentinel ────────────────────────────────────────────────────

    /// <summary>
    /// Singleton returned for indices whose page has not yet been
    /// fetched. <see cref="TableView"/> renders cells with this object
    /// as their data context — bindings simply produce empty values
    /// until the page lands and a Replace notification swaps in the
    /// real row.
    /// </summary>
    public static readonly object Placeholder = new PlaceholderInstance();
    private sealed class PlaceholderInstance
    {
        public override string ToString() => "";
    }

    // ── Configuration ───────────────────────────────────────────────

    private readonly CountAsyncFn _countAsync;
    private readonly PageAsyncFn  _pageAsync;
    private readonly int          _pageSize;
    private readonly int          _maxPagesCached;

    // ── State ───────────────────────────────────────────────────────

    /// <summary>Pages that have already been fetched, keyed by page index.</summary>
    private readonly Dictionary<int, object?[]> _pages = new();

    /// <summary>
    /// LRU order of cached pages — least-recently-used at the front,
    /// most-recently-used at the back. We pop from the front when the
    /// cache exceeds <see cref="_maxPagesCached"/>.
    /// </summary>
    private readonly LinkedList<int> _pageLru = new();
    private readonly Dictionary<int, LinkedListNode<int>> _pageLruNodes = new();

    /// <summary>Page indices whose fetch is in flight.</summary>
    private readonly HashSet<int> _pagesInFlight = new();

    /// <summary>
    /// Cancels any work tied to the current view (count fetch + all
    /// in-flight page fetches). Bumped whenever sort changes or the
    /// caller invokes <see cref="Refresh"/>.
    /// </summary>
    private CancellationTokenSource _cts = new();

    /// <summary>The number of rows the latest count fetch reported.</summary>
    private int _count;

    /// <summary>
    /// Most recently requested row index — proxy for "where is the
    /// visible viewport anchored". Updated on every <see cref="this[int]"/>
    /// access, used by <see cref="RefreshCountAsync"/> to decide which
    /// page to pre-fetch first so the viewport's rows update before
    /// any other.
    /// </summary>
    private int _lastAccessedIndex;

    /// <summary>
    /// True between <see cref="Refresh"/> and the resulting count
    /// fetch completing (or being cancelled). Surface so host UIs can
    /// show a spinner / loading overlay during sort / filter / search
    /// changes — the count fetch is the user-visible "is the grid
    /// re-shuffling?" signal.
    /// </summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value) return;
            _isBusy = value;
            RaisePropertyChanged(nameof(IsBusy));
        }
    }
    private bool _isBusy;

    /// <summary>
    /// Sort descriptions; observable so we can re-fetch when entries
    /// are added or removed. <see cref="TableView"/> mutates this
    /// collection directly when the user clicks a header.
    /// </summary>
    private readonly ObservableCollection<SortDescription>   _sortDescriptions   = new();

    /// <summary>
    /// Filter descriptions accepted for ITableViewItemsSource
    /// compatibility but currently unused in the SQL-backed path.
    /// </summary>
    private readonly ObservableCollection<FilterDescription> _filterDescriptions = new();

    /// <summary>
    /// Number of overlapping <see cref="DeferRefresh"/> scopes.
    /// Refresh is suppressed until the count returns to zero.
    /// </summary>
    private uint _deferCounter;

    /// <summary>
    /// True when at least one mutation happened during a defer; we
    /// trigger a single <see cref="Refresh"/> when the deferral ends.
    /// </summary>
    private bool _refreshPendingFromDefer;

    // ── Construction ────────────────────────────────────────────────

    /// <summary>
    /// Creates a new SQL-backed source.
    /// </summary>
    /// <param name="countAsync">Returns the total row count for a query.</param>
    /// <param name="pageAsync">Returns a contiguous page of rows for a query.</param>
    /// <param name="pageSize">
    /// Rows per fetched page. 200 is a sensible default — large enough
    /// that one page covers a viewport of ~30-40 rows plus a little
    /// scroll headroom, small enough that paging stays fast.
    /// </param>
    /// <param name="maxPagesCached">
    /// Upper bound on simultaneously-resident pages. Older pages are
    /// evicted in LRU order. 50 pages × 200 rows = 10 000 hot rows,
    /// which is more than any plausible viewport keeps live.
    /// </param>
    public SqlBackedItemsSource(
        CountAsyncFn countAsync,
        PageAsyncFn  pageAsync,
        int          pageSize       = 200,
        int          maxPagesCached = 50)
    {
        _countAsync     = countAsync;
        _pageAsync      = pageAsync;
        _pageSize       = pageSize > 0       ? pageSize       : throw new ArgumentOutOfRangeException(nameof(pageSize));
        _maxPagesCached = maxPagesCached > 0 ? maxPagesCached : throw new ArgumentOutOfRangeException(nameof(maxPagesCached));

        _sortDescriptions.CollectionChanged   += OnDescriptionsChanged;
        _filterDescriptions.CollectionChanged += OnDescriptionsChanged;
    }

    // ── ITableViewItemsSource ───────────────────────────────────────

    /// <summary>
    /// Always throws — the SQL-backed source has no in-memory source
    /// to set; configuration goes through the constructor callbacks.
    /// </summary>
    public IEnumerable Source
    {
        get => throw new NotSupportedException(
            "SqlBackedItemsSource has no in-memory source; rows are fetched on demand.");
        set => throw new NotSupportedException(
            "SqlBackedItemsSource has no in-memory source; rows are fetched on demand.");
    }

    public IList<SortDescription>   SortDescriptions   => _sortDescriptions;
    public IList<FilterDescription> FilterDescriptions => _filterDescriptions;

    /// <summary>
    /// Live shaping does not apply here — items are not held in memory,
    /// so item-level <c>PropertyChanged</c> events have no view to
    /// re-shape. Setter is accepted as a no-op for API parity.
    /// </summary>
    public bool AllowLiveShaping { get; set; }

    public Deferral DeferRefresh()
    {
        _deferCounter++;
        return new Deferral(OnDeferralCompleted);
    }

    private void OnDeferralCompleted()
    {
        if (_deferCounter == 0) return;
        _deferCounter--;
        if (_deferCounter == 0 && _refreshPendingFromDefer)
        {
            _refreshPendingFromDefer = false;
            Refresh();
        }
    }

    /// <summary>
    /// Drops every cached page and re-fetches the count. Pages will be
    /// re-fetched on demand as the viewport accesses them. Use this
    /// when the underlying store has changed, the host's filter
    /// context has changed, or the search text has moved.
    /// </summary>
    public void Refresh()
    {
        if (_deferCounter > 0)
        {
            _refreshPendingFromDefer = true;
            return;
        }

        // Cancel everything tied to the previous view, then start fresh.
        var oldCts = _cts;
        _cts = new CancellationTokenSource();
        oldCts.Cancel();
        oldCts.Dispose();

        UnhookAll();
        _pages.Clear();
        _pageLru.Clear();
        _pageLruNodes.Clear();
        _pagesInFlight.Clear();

        IsBusy = true;
        _ = RefreshCountAsync(_cts.Token);
    }

    /// <summary>Sort descriptions live in <see cref="_sortDescriptions"/> — refreshing sort means re-fetching.</summary>
    public void RefreshSorting() => Refresh();

    /// <summary>
    /// No-op in this first cut — filters are not honoured by the
    /// SQL-backed source yet (a structured filter API arrives in the
    /// next phase). The method exists so existing TableView code that
    /// calls <c>RefreshFilter</c> compiles unchanged.
    /// </summary>
    public void RefreshFilter() { /* deferred to a later phase */ }

    /// <summary>
    /// Relays per-row <see cref="INotifyPropertyChanged.PropertyChanged"/>
    /// from currently-cached page rows out to the TableView so
    /// <c>TableView.OnItemPropertyChanged</c> can re-run
    /// ConditionalCellStyle predicates and per-row tooltip producers
    /// without a full page refresh. Subscriptions are added when a page
    /// lands and removed on eviction / refresh / disposal so a long-
    /// running grid doesn't accumulate dead handler references.
    /// </summary>
    public event PropertyChangedEventHandler? ItemPropertyChanged;

    /// <summary>Items currently subscribed for property-changed relay (live across pages until evicted).</summary>
    private readonly HashSet<INotifyPropertyChanged> _subscribed = new();

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e) =>
        ItemPropertyChanged?.Invoke(sender, e);

    private void HookPage(object?[] buffer)
    {
        foreach (var item in buffer)
        {
            if (item is INotifyPropertyChanged npc && _subscribed.Add(npc))
                npc.PropertyChanged += OnRowPropertyChanged;
        }
    }

    private void UnhookPage(object?[] buffer)
    {
        foreach (var item in buffer)
        {
            if (item is INotifyPropertyChanged npc && _subscribed.Remove(npc))
                npc.PropertyChanged -= OnRowPropertyChanged;
        }
    }

    private void UnhookAll()
    {
        foreach (var npc in _subscribed) npc.PropertyChanged -= OnRowPropertyChanged;
        _subscribed.Clear();
    }

    // ── Sort/filter description bookkeeping ─────────────────────────

    private void OnDescriptionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Both sort and filter description collections funnel here.
        // Filter changes are silently absorbed (RefreshFilter is a
        // no-op), but a sort change triggers a full refresh because
        // the SQL ORDER BY needs to be re-issued.
        if (ReferenceEquals(sender, _sortDescriptions)) Refresh();
    }

    // ── Count / page fetch ──────────────────────────────────────────

    private async Task RefreshCountAsync(CancellationToken ct)
    {
        try
        {
            var query    = CurrentQuery();
            var newCount = await _countAsync(query, ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested) return;

            var oldCount = _count;
            _count = newCount;
            if (newCount != oldCount)
            {
                RaisePropertyChanged(nameof(Count));
                // Tell the bound grid about the count delta so its row
                // container manager grows / shrinks slots. Without this,
                // ListView only re-binds the indices it already knew
                // about and ignores the appended tail — symptom: new
                // records arrive but the visible row count never grows.
                // Placeholder rows materialise into real ones via the
                // existing per-page Replace events.
                if (newCount > oldCount)
                {
                    for (var i = oldCount; i < newCount; i++)
                    {
                        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(
                            NotifyCollectionChangedAction.Add, Placeholder, i));
                        VectorChanged?.Invoke(this, new VectorChangedEventArgs(
                            CollectionChange.ItemInserted, i));
                    }
                }
                else
                {
                    for (var i = oldCount - 1; i >= newCount; i--)
                    {
                        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(
                            NotifyCollectionChangedAction.Remove, Placeholder, i));
                        VectorChanged?.Invoke(this, new VectorChangedEventArgs(
                            CollectionChange.ItemRemoved, i));
                    }
                }
            }

            // Pre-fetch the page containing the most-recently accessed
            // index — that's where the visible viewport is anchored.
            // After it lands, per-row Replace events update only the
            // visible cells. We deliberately do NOT raise Reset here:
            // Reset is heavy in Uno's ListView at large item counts
            // (it tears down recycled containers and can run a
            // measurement pass proportional to the previously realized
            // range), and unnecessary — ListView's existing containers
            // re-bind to the new RecordRow when their indexed Replace
            // event fires.
            if (newCount > 0)
            {
                var anchor    = Math.Clamp(_lastAccessedIndex, 0, newCount - 1);
                var pageIndex = anchor / _pageSize;
                var offset    = pageIndex * _pageSize;
                var limit     = Math.Min(_pageSize, newCount - offset);

                var pageRows  = await _pageAsync(query, offset, limit, ct).ConfigureAwait(true);
                if (ct.IsCancellationRequested) return;

                var buffer = new object?[pageRows.Count];
                for (var i = 0; i < pageRows.Count; i++) buffer[i] = pageRows[i];
                _pages[pageIndex] = buffer;
                TouchLru(pageIndex);
                HookPage(buffer);

                RaisePageLoaded(offset, buffer);
            }
        }
        catch (OperationCanceledException) { /* superseded by a newer refresh */ }
        finally
        {
            // Only clear busy if this CTS is still the active one; a
            // newer Refresh() may already have bumped IsBusy back on.
            if (!ct.IsCancellationRequested) IsBusy = false;
        }
    }

    /// <summary>
    /// Schedules a fetch for the page containing <paramref name="pageIndex"/>
    /// if it is not already loaded or in flight. The fetch is async;
    /// callers receive the placeholder for now and a Replace
    /// notification per-row when the page lands.
    /// </summary>
    private void EnsurePageLoaded(int pageIndex)
    {
        if (_pages.ContainsKey(pageIndex))         return;
        if (_pagesInFlight.Contains(pageIndex))    return;

        _pagesInFlight.Add(pageIndex);
        _ = LoadPageAsync(pageIndex, _cts.Token);
    }

    private async Task LoadPageAsync(int pageIndex, CancellationToken ct)
    {
        try
        {
            var query  = CurrentQuery();
            var offset = pageIndex * _pageSize;
            var limit  = Math.Min(_pageSize, Math.Max(0, _count - offset));
            if (limit == 0)
            {
                _pagesInFlight.Remove(pageIndex);
                return;
            }

            var rows = await _pageAsync(query, offset, limit, ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested) return;

            // Materialise into a fixed-size buffer so page-cache lookups
            // can index without bounds checks even if the host returned
            // fewer rows than asked (we just leave Placeholder for the
            // tail — that mirrors what an out-of-range row should show
            // until the next refresh resolves the count).
            var buffer = new object?[limit];
            for (var i = 0; i < limit && i < rows.Count; i++) buffer[i] = rows[i];
            for (var i = rows.Count; i < limit; i++)          buffer[i] = Placeholder;

            _pages[pageIndex] = buffer;
            _pagesInFlight.Remove(pageIndex);
            TouchLru(pageIndex);
            // Subscribe BEFORE eviction so an item that lives in multiple
            // pages keeps one live subscription even if its other page
            // gets evicted.
            HookPage(buffer);
            EvictIfNeeded();

            // Per-row Replace events for each filled-in row. The
            // structural Reset already fired from RefreshCountAsync;
            // page-loads only need to patch the specific indices that
            // changed from Placeholder to a real row.
            RaisePageLoaded(offset, buffer);
        }
        catch (OperationCanceledException)
        {
            _pagesInFlight.Remove(pageIndex);
        }
    }

    private Query CurrentQuery() => new(
        Sorts:   _sortDescriptions  .ToArray(),
        Filters: _filterDescriptions.ToArray());

    // ── LRU page eviction ───────────────────────────────────────────

    private void TouchLru(int pageIndex)
    {
        if (_pageLruNodes.TryGetValue(pageIndex, out var existing))
        {
            _pageLru.Remove(existing);
            _pageLru.AddLast(existing);
        }
        else
        {
            _pageLruNodes[pageIndex] = _pageLru.AddLast(pageIndex);
        }
    }

    private void EvictIfNeeded()
    {
        while (_pages.Count > _maxPagesCached && _pageLru.First is { } victim)
        {
            _pageLru.RemoveFirst();
            _pageLruNodes.Remove(victim.Value);
            if (_pages.TryGetValue(victim.Value, out var evictedBuffer))
                UnhookPage(evictedBuffer);
            _pages.Remove(victim.Value);
            // No vector notification: the row indices still exist
            // (Count is unchanged); accessing them again will simply
            // return Placeholder and re-trigger the page fetch.
        }
    }

    // ── IList<object?> ─────────────────────────────────────────────

    public int Count => _count;

    public bool IsReadOnly => true;

    /// <summary>
    /// Returns the row at <paramref name="index"/>, or
    /// <see cref="Placeholder"/> if its page is not yet loaded. The
    /// page fetch is kicked off as a side-effect, so subsequent
    /// accesses (after the VectorChanged round-trip) return the
    /// real row.
    /// </summary>
    public object? this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count) return Placeholder;

            // Track the most recent access — Refresh uses it to decide
            // which page to pre-fetch so the user's current viewport
            // updates first, instead of always page 0.
            _lastAccessedIndex = index;

            var pageIndex   = index / _pageSize;
            var indexInPage = index % _pageSize;

            if (_pages.TryGetValue(pageIndex, out var page))
            {
                TouchLru(pageIndex);
                return page[indexInPage];
            }

            EnsurePageLoaded(pageIndex);
            return Placeholder;
        }
        set => throw new NotSupportedException("SqlBackedItemsSource is read-only.");
    }

    public bool Contains(object? item) => IndexOf(item) >= 0;

    /// <summary>
    /// Linear search restricted to currently-cached pages. Returns -1
    /// if the item is not in memory; we deliberately do not iterate
    /// the entire row range (that would defeat virtualization).
    /// </summary>
    public int IndexOf(object? item)
    {
        foreach (var (pageIndex, page) in _pages)
        {
            for (var i = 0; i < page.Length; i++)
            {
                if (Equals(page[i], item)) return pageIndex * _pageSize + i;
            }
        }
        return -1;
    }

    public void Add(object? item)        => throw new NotSupportedException("SqlBackedItemsSource is read-only.");
    public void Clear()                  => throw new NotSupportedException("SqlBackedItemsSource is read-only.");
    public void Insert(int index, object? item) => throw new NotSupportedException("SqlBackedItemsSource is read-only.");
    public bool Remove(object? item)     => throw new NotSupportedException("SqlBackedItemsSource is read-only.");
    public void RemoveAt(int index)      => throw new NotSupportedException("SqlBackedItemsSource is read-only.");

    /// <summary>
    /// Indexer-equivalent that does NOT trigger a page fetch when the
    /// requested page is not cached — returns <see cref="Placeholder"/>
    /// instead. Used by enumerator + <see cref="CopyTo"/>; both are
    /// invoked by WinUI's <c>PrepareContainerForItemOverride</c>, which
    /// itself fires on every Replace notification we raise. If those
    /// paths re-triggered fetches we'd recurse into infinite RaisePageLoaded
    /// → PrepareContainerForItemOverride → GetEnumerator → fetch → ...
    /// (Discovered Apr 2026 when deleting a record after a fresh insert
    /// produced a tight loop of Replace events for every page beyond the
    /// viewport.)
    /// </summary>
    private object? PeekAt(int index)
    {
        if ((uint)index >= (uint)_count) return Placeholder;
        var pageIndex   = index / _pageSize;
        var indexInPage = index % _pageSize;
        if (_pages.TryGetValue(pageIndex, out var page)) return page[indexInPage];
        return Placeholder;
    }

    public void CopyTo(object?[] array, int arrayIndex)
    {
        // Used by ICollection<T>; only the cached slice is visible to
        // a synchronous copy. Callers that need the full set should
        // page through via the indexer. Use PeekAt to avoid cascading
        // page fetches — this method is called from base WinUI code
        // and a fetch-triggering copy here re-enters via Replace events.
        for (var i = 0; i < _count && arrayIndex + i < array.Length; i++)
            array[arrayIndex + i] = PeekAt(i);
    }

    public IEnumerator<object?> GetEnumerator()
    {
        // PeekAt instead of this[i]: WinUI's base PrepareContainerForItemOverride
        // walks the enumerator on every container prep, which itself fires
        // for every Replace notification we raise. A fetch-triggering walk
        // would loop indefinitely (see PeekAt remarks).
        for (var i = 0; i < _count; i++) yield return PeekAt(i);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // ── ICollectionView (current item — degenerate) ─────────────────

    public object?           CurrentItem          => null;
    public int               CurrentPosition      => -1;
    public bool              IsCurrentBeforeFirst => true;
    public bool              IsCurrentAfterLast   => true;
    public IObservableVector<object?>? CollectionGroups => null;

    public bool MoveCurrentTo(object? item)        => false;
    public bool MoveCurrentToFirst()               => false;
    public bool MoveCurrentToLast()                => false;
    public bool MoveCurrentToNext()                => false;
    public bool MoveCurrentToPrevious()            => false;
    public bool MoveCurrentToPosition(int index)   => false;

    public event CurrentChangingEventHandler? CurrentChanging
    {
        add    { /* current-item tracking is unused for SQL-backed grids */ }
        remove { }
    }
    public event EventHandler<object>? CurrentChanged
    {
        add    { /* current-item tracking is unused for SQL-backed grids */ }
        remove { }
    }

    // ── ISupportIncrementalLoading ─────────────────────────────────

    /// <summary>
    /// Always false — Count is authoritative, and ListView pages by
    /// random-access index rather than by appending.
    /// </summary>
    public bool HasMoreItems => false;

    public IAsyncOperation<LoadMoreItemsResult>? LoadMoreItemsAsync(uint count) => null;

    // ── Change notifications ───────────────────────────────────────

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    /// <summary>
    /// Generic argument is <c>object</c> (not <c>object?</c>) to match
    /// <see cref="IObservableVector{T}.VectorChanged"/>'s contract on
    /// <see cref="ICollectionView"/>; Uno-side ListView subscribes via
    /// the interface, so a nullable mismatch can quietly drop events.
    /// </summary>
    public event VectorChangedEventHandler<object>?   VectorChanged;
    public event PropertyChangedEventHandler?         PropertyChanged;

    private void RaiseReset()
    {
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        VectorChanged?    .Invoke(this, new VectorChangedEventArgs(CollectionChange.Reset));
    }

    /// <summary>
    /// Notifies subscribers that a previously-placeholder page has
    /// been filled in. Per-row Replace events (rather than a single
    /// Reset) so ListView only re-binds the affected indices instead
    /// of re-walking the whole realized range — the latter triggers
    /// cascading layout passes when user scrolls then sorts, because
    /// each Reset fires more page-fetches that fire more Resets.
    /// Targeted Replace keeps the recycler quiet for unaffected rows.
    /// </summary>
    private void RaisePageLoaded(int firstIndex, object?[] page)
    {
        for (var i = 0; i < page.Length; i++)
        {
            var globalIndex = firstIndex + i;
            var item        = page[i];
            VectorChanged?.Invoke(this, new VectorChangedEventArgs(CollectionChange.ItemChanged, globalIndex, item));
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Replace,
                newItem: item, oldItem: Placeholder, index: globalIndex));
        }
    }

    private void RaisePropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
