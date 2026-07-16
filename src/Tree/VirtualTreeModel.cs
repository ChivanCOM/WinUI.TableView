// FOBO fork addition.
//
// The data core behind <see cref="VirtualTreeItemsSource"/>: a tree-shaped
// counterpart to <see cref="SqlBackedItemsSource"/> for grids whose GROUP
// rows (folders) fit comfortably in memory while their LEAF rows (files,
// tracks, records) number in the hundreds of thousands and must be fetched
// on demand.
//
// The host supplies the group skeleton — a tree of <see cref="ITreeGridRow"/>
// objects — plus a per-group leaf count and an async windowed leaf fetch.
// This class projects the tree into a flat, virtual row space:
//
//   group row
//     child group row
//       ... (its leaves)
//     leaf block of the group (contiguous, AFTER subgroups)
//
// and takes care of index↔row mapping, expand/collapse deltas, a paged
// LRU leaf cache, and placeholder rows while a page is in flight.
//
// Deliberately UI-free (no WinRT / WinUI types) so tests.tree can
// compile-link it and run the index math on any OS, the same split that
// keeps TreeGridFlattener verifiable.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace WinUI.TableView;

/// <summary>
/// Flat virtual projection of a group tree with windowed, on-demand leaf
/// fetching. Groups live in memory (the host's skeleton); leaves are fetched
/// in pages and cached with LRU eviction. All members must be called from a
/// single thread (the UI thread in practice); fetch continuations resume on
/// the caller's synchronization context.
/// </summary>
public sealed class VirtualTreeModel
{
    /// <summary>
    /// Returns a contiguous window of leaf rows for a group
    /// (<see langword="null"/> = leaves at the root level).
    /// </summary>
    public delegate Task<IReadOnlyList<object>> FetchLeavesFn(
        object? group, int offset, int limit, CancellationToken ct);

    /// <summary>
    /// Above this row-count delta a structural change raises a single
    /// <see cref="ResetRaised"/> instead of per-index events — same
    /// threshold rationale as SqlBackedItemsSource: O(delta) per-index
    /// events on the UI thread freeze the app when a folder holds
    /// thousands of rows.
    /// </summary>
    public const int BulkDeltaThreshold = 64;

    /// <summary>
    /// Consecutive failures for one page key before it is put on cooldown —
    /// enough to ride out a couple of transient hiccups but stop a permanently
    /// broken page (a genuinely missing table) from spinning the backend.
    /// </summary>
    private const int FailureCooldownThreshold = 3;

    private readonly Func<object, IReadOnlyList<object>> _childrenOf;
    private readonly Func<object?, int> _leafCountOf;
    private readonly FetchLeavesFn _fetchLeaves;
    private readonly Func<object?, object> _placeholderOf;
    private readonly int _pageSize;
    private readonly int _maxPagesCached;
    private readonly int _maxConcurrentFetches;
    private readonly long _failureCooldownTicks;

    /// <summary>
    /// One contiguous run of the flat projection: either a single group row
    /// (<see cref="IsLeafBlock"/> false, Length 1) or a group's leaf block.
    /// <see cref="Group"/> is null only for the root leaf block.
    /// </summary>
    private readonly record struct Segment(object? Group, bool IsLeafBlock, int Start, int Length);

    private readonly List<Segment> _segments = new();
    private int _count;

    // O(1) group → its leaf-block segment, so IndexOf and post-fetch patching
    // don't linear-scan _segments (which is ~all groups + leaf blocks at 1M
    // rows). Built lazily from _segments on first use after each rebuild.
    private readonly Dictionary<object, Segment> _leafSegmentByGroup = new(ReferenceEqualityComparer.Instance);
    private Segment? _rootLeafSegment;
    private bool _leafSegmentIndexValid;

    private IReadOnlyList<object> _roots = Array.Empty<object>();

    /// <summary>Groups whose IsExpanded we watch. Cleared and re-hooked on SetRoots.</summary>
    private readonly HashSet<INotifyPropertyChanged> _hookedGroups = new();

    // Leaf page cache. Key is (group identity, page-within-group) so the
    // cache survives expand/collapse (group-local indices don't shift).
    private readonly Dictionary<(object? Group, int Page), object?[]> _pages = new();

    // Per-slot placeholder identity: sharing ONE placeholder object across rows breaks every
    // identity-based consumer (ListView container maps, IndexOf, overlap detection) because
    // duplicates resolve to the first occurrence. Buffers live only for touched-but-unfetched
    // pages and are dropped when the real page lands or on Clear.
    private readonly Dictionary<(object? Group, int Page), object?[]> _placeholderPages = new();
    private readonly LinkedList<(object? Group, int Page)> _pageLru = new();
    private readonly Dictionary<(object? Group, int Page), LinkedListNode<(object? Group, int Page)>> _pageLruNodes = new();
    private readonly HashSet<(object? Group, int Page)> _pagesInFlight = new();

    // Pending page requests not yet dispatched, ordered newest-request-last so
    // the drain pops the top: a thumb-drag lands on the page the user is
    // actually waiting for, ahead of the stale viewports it dragged through.
    // Deduped by _pendingNodes (also gives O(1) move-to-top on re-request).
    private readonly LinkedList<(object? Group, int Page)> _pending = new();
    private readonly Dictionary<(object? Group, int Page), LinkedListNode<(object? Group, int Page)>> _pendingNodes = new();

    // Per-key consecutive-failure count and, once past the threshold, the
    // Stopwatch timestamp before which re-requests are ignored (cooldown).
    private readonly Dictionary<(object? Group, int Page), int> _pageFailures = new();
    private readonly Dictionary<(object? Group, int Page), long> _pageCooldownUntil = new();

    // Reverse map leaf row → its cache slot, so IndexOf is O(1) per row instead
    // of scanning every cached page. Reference identity: two distinct rows that
    // happen to compare equal must not collide.
    private readonly Dictionary<object, (object? Group, int Page, int Slot)> _leafIndex = new(ReferenceEqualityComparer.Instance);

    /// <summary>Leaf rows currently subscribed for property-changed relay.</summary>
    private readonly HashSet<INotifyPropertyChanged> _subscribedLeaves = new();

    private CancellationTokenSource _cts = new();

    public VirtualTreeModel(
        Func<object, IReadOnlyList<object>> childrenOf,
        Func<object?, int> leafCountOf,
        FetchLeavesFn fetchLeaves,
        Func<object?, object> placeholderOf,
        int pageSize = 200,
        int maxPagesCached = 64,
        int maxConcurrentFetches = 2,
        int failureCooldownMs = 1000)
    {
        _childrenOf = childrenOf;
        _leafCountOf = leafCountOf;
        _fetchLeaves = fetchLeaves;
        _placeholderOf = placeholderOf;
        _pageSize = pageSize > 0 ? pageSize : throw new ArgumentOutOfRangeException(nameof(pageSize));
        _maxPagesCached = maxPagesCached > 0 ? maxPagesCached : throw new ArgumentOutOfRangeException(nameof(maxPagesCached));
        _maxConcurrentFetches = maxConcurrentFetches > 0 ? maxConcurrentFetches : throw new ArgumentOutOfRangeException(nameof(maxConcurrentFetches));
        _failureCooldownTicks = (long)(Math.Max(0, failureCooldownMs) / 1000.0 * Stopwatch.Frequency);
    }

    // ── Change surface (translated to INCC/VectorChanged by the wrapper) ──

    /// <summary>Structure changed beyond per-index repair — rebind everything.</summary>
    public event Action? ResetRaised;

    /// <summary>A contiguous run of rows appeared (expand). Args: first index, count.</summary>
    public event Action<int, int>? RangeInserted;

    /// <summary>A contiguous run of rows disappeared (collapse). Args: first index, count.</summary>
    public event Action<int, int>? RangeRemoved;

    /// <summary>A fetched page filled a placeholder. Args: index, new row, old placeholder.</summary>
    public event Action<int, object, object>? ItemReplaced;

    /// <summary>A cached leaf raised INotifyPropertyChanged.</summary>
    public event PropertyChangedEventHandler? LeafPropertyChanged;

    public int Count => _count;

    // ── Structure ───────────────────────────────────────────────────

    /// <summary>
    /// Replaces the group skeleton and resets the projection. Drops every
    /// cached page (the underlying data may have changed) and raises
    /// <see cref="ResetRaised"/>.
    /// </summary>
    public void SetRoots(IReadOnlyList<object> roots)
    {
        _roots = roots;

        foreach (var g in _hookedGroups)
            g.PropertyChanged -= OnGroupPropertyChanged;
        _hookedGroups.Clear();
        HookGroups(roots);

        CancelFetches();
        ClearPages();
        RebuildSegments();
        ResetRaised?.Invoke();
    }

    /// <summary>
    /// Re-queries everything: drops cached pages (underlying data changed)
    /// while keeping the current skeleton and expand states. The host calls
    /// this when leaf data changed but the skeleton is already up to date;
    /// when the skeleton itself changed, call <see cref="SetRoots"/>.
    /// </summary>
    public void Refresh()
    {
        CancelFetches();
        ClearPages();
        RebuildSegments();
        ResetRaised?.Invoke();
    }

    private void CancelFetches()
    {
        var old = _cts;
        _cts = new CancellationTokenSource();
        old.Cancel();
        old.Dispose();
        _pagesInFlight.Clear();
        _pending.Clear();
        _pendingNodes.Clear();
        _pageFailures.Clear();
        _pageCooldownUntil.Clear();
    }

    private void ClearPages()
    {
        foreach (var leaf in _subscribedLeaves)
            leaf.PropertyChanged -= OnLeafPropertyChanged;
        _subscribedLeaves.Clear();
        _pages.Clear();
        _placeholderPages.Clear();
        _pageLru.Clear();
        _pageLruNodes.Clear();
        _leafIndex.Clear();
    }

    private void HookGroups(IReadOnlyList<object> groups)
    {
        foreach (var g in groups)
        {
            if (g is INotifyPropertyChanged npc && _hookedGroups.Add(npc))
                npc.PropertyChanged += OnGroupPropertyChanged;
            HookGroups(_childrenOf(g));
        }
    }

    private void OnGroupPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ITreeGridRow.IsExpanded) || sender is null)
            return;

        // The group's row index, from the CURRENT (pre-toggle) segments.
        // Not found = the group sits under a collapsed ancestor; nothing
        // visible changes, and the rebuilt-on-next-toggle segments will
        // pick the new state up.
        var groupIndex = -1;
        foreach (var seg in _segments)
        {
            if (!seg.IsLeafBlock && ReferenceEquals(seg.Group, sender))
            {
                groupIndex = seg.Start;
                break;
            }
        }

        var oldCount = _count;
        RebuildSegments();

        if (groupIndex < 0)
            return;

        // The subtree is contiguous right after the group row, so the whole
        // change is one inserted or removed block.
        var delta = _count - oldCount;
        if (delta == 0)
            return;

        if (Math.Abs(delta) > BulkDeltaThreshold)
            ResetRaised?.Invoke();
        else if (delta > 0)
            RangeInserted?.Invoke(groupIndex + 1, delta);
        else
            RangeRemoved?.Invoke(groupIndex + 1, -delta);
    }

    private void RebuildSegments()
    {
        _segments.Clear();
        _leafSegmentIndexValid = false;
        var cursor = 0;

        void Walk(IReadOnlyList<object> groups)
        {
            foreach (var g in groups)
            {
                _segments.Add(new Segment(g, IsLeafBlock: false, cursor, 1));
                cursor++;
                if (g is ITreeGridRow { IsExpanded: true })
                {
                    Walk(_childrenOf(g));
                    var leaves = _leafCountOf(g);
                    if (leaves > 0)
                    {
                        _segments.Add(new Segment(g, IsLeafBlock: true, cursor, leaves));
                        cursor += leaves;
                    }
                }
            }
        }

        Walk(_roots);

        var rootLeaves = _leafCountOf(null);
        if (rootLeaves > 0)
        {
            _segments.Add(new Segment(null, IsLeafBlock: true, cursor, rootLeaves));
            cursor += rootLeaves;
        }

        _count = cursor;
    }

    // ── Row access ──────────────────────────────────────────────────

    /// <summary>
    /// The row at <paramref name="index"/>: the group object itself for
    /// group rows; for leaf rows the cached leaf, or the group's placeholder
    /// with a page fetch kicked off as a side effect.
    /// </summary>
    public object? GetAt(int index) => At(index, fetchIfMissing: true);

    /// <summary>
    /// Like <see cref="GetAt"/> but never triggers a fetch — for enumerator
    /// and CopyTo paths that WinUI walks during container prep (a
    /// fetch-triggering walk recurses: fetch → Replace → container prep →
    /// walk → fetch; see SqlBackedItemsSource.PeekAt).
    /// </summary>
    public object? PeekAt(int index) => At(index, fetchIfMissing: false);

    private object? At(int index, bool fetchIfMissing)
    {
        if ((uint)index >= (uint)_count)
            return null;

        var seg = FindSegment(index);
        if (!seg.IsLeafBlock)
            return seg.Group;

        var local = index - seg.Start;
        var page = local / _pageSize;
        var inPage = local % _pageSize;

        var key = (seg.Group, page);
        if (_pages.TryGetValue(key, out var buffer))
        {
            TouchLru(key);
            return buffer[inPage] ?? PlaceholderAt(key, inPage);
        }

        if (fetchIfMissing && !_pagesInFlight.Contains(key) && !InCooldown(key))
        {
            // Newest request wins: push to the top of the LIFO, then drain —
            // which starts this page immediately when a fetch slot is free
            // (preserving the synchronous fast path) or leaves it queued ahead
            // of older requests until one frees.
            PushPending(key);
            DrainPending();

            // A fast local store can complete the fetch synchronously (the
            // awaited task was already finished) — return the real row now
            // instead of a placeholder the next read would swap anyway.
            if (_pages.TryGetValue(key, out var landed))
                return landed[inPage] ?? PlaceholderAt(key, inPage);
        }

        return PlaceholderAt(key, inPage);
    }

    private object PlaceholderAt((object? Group, int Page) key, int inPage)
    {
        if (!_placeholderPages.TryGetValue(key, out var buffer))
        {
            if (_placeholderPages.Count > _maxPagesCached * 2)
                _placeholderPages.Clear();   // cheap bound; identities re-create on demand
            _placeholderPages[key] = buffer = new object?[_pageSize];
        }
        return buffer[inPage] ??= _placeholderOf(key.Group);
    }

    private Segment FindSegment(int index)
    {
        // Binary search over segment starts.
        int lo = 0, hi = _segments.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (_segments[mid].Start <= index) lo = mid;
            else hi = mid - 1;
        }
        return _segments[lo];
    }

    /// <summary>
    /// The flat index of a row, or -1. Group rows are found via the
    /// segments; leaves only when their page is cached (deliberately no
    /// full-range walk — that would defeat virtualization).
    /// </summary>
    public int IndexOf(object? item)
    {
        if (item is null)
            return -1;

        // Cached leaves resolve in O(1): the reverse map gives the group-local
        // slot, the group→leaf-block index the containing segment. This is the
        // per-realized-row selection hot path, so it goes first — group rows are
        // never in the map and fall through to the segment scan below.
        if (_leafIndex.TryGetValue(item, out var loc))
        {
            if (FindLeafSegment(loc.Group) is not { } seg)
                return -1;
            var flat = seg.Start + loc.Page * _pageSize + loc.Slot;
            return flat < seg.Start + seg.Length ? flat : -1;
        }

        foreach (var seg in _segments)
        {
            if (!seg.IsLeafBlock && ReferenceEquals(seg.Group, item))
                return seg.Start;
        }
        return -1;
    }

    private Segment? FindLeafSegment(object? group)
    {
        if (!_leafSegmentIndexValid)
        {
            _leafSegmentByGroup.Clear();
            _rootLeafSegment = null;
            foreach (var seg in _segments)
            {
                if (!seg.IsLeafBlock)
                    continue;
                if (seg.Group is null)
                    _rootLeafSegment = seg;
                else
                    _leafSegmentByGroup[seg.Group] = seg;
            }
            _leafSegmentIndexValid = true;
        }

        if (group is null)
            return _rootLeafSegment;
        return _leafSegmentByGroup.TryGetValue(group, out var found) ? found : null;
    }

    /// <summary>Every leaf row currently resident in the page cache.</summary>
    public IEnumerable<object> CachedLeaves()
    {
        foreach (var buffer in _pages.Values)
        {
            foreach (var row in buffer)
            {
                if (row is not null)
                    yield return row;
            }
        }
    }

    // ── Page fetch ──────────────────────────────────────────────────

    private async Task LoadPageAsync(object? group, int page, CancellationToken ct)
    {
        var key = (group, page);
        try
        {
            var total = _leafCountOf(group);
            var offset = page * _pageSize;
            var limit = Math.Min(_pageSize, Math.Max(0, total - offset));
            if (limit == 0)
            {
                _pagesInFlight.Remove(key);
                DrainPending();
                return;
            }

            var rows = await _fetchLeaves(group, offset, limit, ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested)
                return; // CancelFetches already reset in-flight + pending

            var buffer = new object?[limit];
            for (var i = 0; i < limit && i < rows.Count; i++)
                buffer[i] = rows[i];

            _pages[key] = buffer;
            _placeholderPages.Remove(key);
            _pagesInFlight.Remove(key);
            _pageFailures.Remove(key);
            _pageCooldownUntil.Remove(key);
            IndexPage(key, buffer);
            TouchLru(key);
            HookLeaves(buffer);
            EvictIfNeeded();

            // Patch only the indices that changed — and only when the block
            // is still visible (the folder may have collapsed mid-fetch; the
            // cache stays warm for the next expand).
            if (FindLeafSegment(group) is { } seg)
            {
                var placeholder = _placeholderOf(group);
                for (var i = 0; i < buffer.Length; i++)
                {
                    if (buffer[i] is not { } row)
                        continue;
                    var flat = seg.Start + offset + i;
                    if (flat < seg.Start + seg.Length)
                        ItemReplaced?.Invoke(flat, row, placeholder);
                }
            }

            DrainPending();
        }
        catch (OperationCanceledException)
        {
            _pagesInFlight.Remove(key);
            // A cancel only comes from CancelFetches, which already reset the
            // queue; draining here would fight that reset.
        }
        catch
        {
            // Any other failure (DB locked, transient IO) must free the slot so
            // the next GetAt refetches instead of the key sitting in-flight
            // forever. Count the consecutive failure for the cooldown guard,
            // then let another queued page take the freed slot.
            _pagesInFlight.Remove(key);
            RecordFailure(key);
            DrainPending();
        }
    }

    private void HookLeaves(object?[] buffer)
    {
        foreach (var row in buffer)
        {
            if (row is INotifyPropertyChanged npc && _subscribedLeaves.Add(npc))
                npc.PropertyChanged += OnLeafPropertyChanged;
        }
    }

    private void OnLeafPropertyChanged(object? sender, PropertyChangedEventArgs e)
        => LeafPropertyChanged?.Invoke(sender, e);

    private void TouchLru((object? Group, int Page) key)
    {
        if (_pageLruNodes.TryGetValue(key, out var existing))
        {
            _pageLru.Remove(existing);
            _pageLru.AddLast(existing);
        }
        else
        {
            _pageLruNodes[key] = _pageLru.AddLast(key);
        }
    }

    private void EvictIfNeeded()
    {
        while (_pages.Count > _maxPagesCached && _pageLru.First is { } victim)
        {
            _pageLru.RemoveFirst();
            _pageLruNodes.Remove(victim.Value);
            if (_pages.TryGetValue(victim.Value, out var buffer))
            {
                foreach (var row in buffer)
                {
                    if (row is null)
                        continue;
                    if (row is INotifyPropertyChanged npc && _subscribedLeaves.Remove(npc))
                        npc.PropertyChanged -= OnLeafPropertyChanged;
                    DeindexLeaf(victim.Value, row);
                }
            }
            _pages.Remove(victim.Value);
            // No notification: the indices still exist; re-access returns
            // the placeholder and re-triggers the fetch.
        }
    }

    // ── Fetch scheduler + reverse index ─────────────────────────────

    /// <summary>Push a page key to the top of the LIFO, or move it there if
    /// already queued (most-recently-requested wins).</summary>
    private void PushPending((object? Group, int Page) key)
    {
        if (_pendingNodes.TryGetValue(key, out var node))
        {
            _pending.Remove(node);
            _pending.AddLast(node);
        }
        else
        {
            _pendingNodes[key] = _pending.AddLast(key);
        }
    }

    /// <summary>Start queued fetches newest-first up to the concurrency cap,
    /// dropping keys that no longer need serving.</summary>
    private void DrainPending()
    {
        while (_pagesInFlight.Count < _maxConcurrentFetches && _pending.Last is { } node)
        {
            var key = node.Value;
            _pending.RemoveLast();
            _pendingNodes.Remove(key);

            // Drop what does not need fetching (any more): already cached,
            // already in flight, cooling down, or a block that stopped being
            // visible (the folder collapsed while this request waited).
            if (_pages.ContainsKey(key) || _pagesInFlight.Contains(key) || InCooldown(key))
                continue;
            if (FindLeafSegment(key.Group) is null)
                continue;

            _pagesInFlight.Add(key);
            _ = LoadPageAsync(key.Group, key.Page, _cts.Token);
        }
    }

    private bool InCooldown((object? Group, int Page) key)
        => _pageCooldownUntil.TryGetValue(key, out var until) && Stopwatch.GetTimestamp() < until;

    private void RecordFailure((object? Group, int Page) key)
    {
        var n = _pageFailures.TryGetValue(key, out var c) ? c + 1 : 1;
        _pageFailures[key] = n;
        if (n >= FailureCooldownThreshold && _failureCooldownTicks > 0)
            _pageCooldownUntil[key] = Stopwatch.GetTimestamp() + _failureCooldownTicks;
    }

    private void IndexPage((object? Group, int Page) key, object?[] buffer)
    {
        for (var i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] is { } row)
                _leafIndex[row] = (key.Group, key.Page, i);
        }
    }

    private void DeindexLeaf((object? Group, int Page) key, object row)
    {
        // Only drop the mapping if it still points at the evicted page — a
        // re-fetch may already own this row under a resident page.
        if (_leafIndex.TryGetValue(row, out var loc)
            && ReferenceEquals(loc.Group, key.Group) && loc.Page == key.Page)
            _leafIndex.Remove(row);
    }
}
