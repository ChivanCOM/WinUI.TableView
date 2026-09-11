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
    private readonly Func<object, object?>? _groupKeyOf;
    private readonly Func<object, (object GroupKey, int LeafOffset)?>? _anchorOf;
    private readonly Func<object?, int> _leafCountOf;
    private readonly FetchLeavesFn _fetchLeaves;
    private readonly Func<object?, object> _placeholderOf;
    private readonly Func<object?, int, object>? _placeholderAtOf;
    private readonly int _pageSize;
    private readonly int _maxPagesCached;

    /// <summary>
    /// How many leaf ROWS the page cache is holding.
    ///
    /// <para>The budget is counted in rows and not in pages, and that is the whole of a fault measured
    /// on 2026-09-10. A page is one block's slice, and a block is one group, so a tree of small groups
    /// makes small pages: an import queue of 26,663 files in 2,744 records averages ten rows a page. At
    /// sixty-four PAGES that cache held about five hundred rows, so a screenful of records and the
    /// blocks prefetched either side of it did not fit, and every read evicted the page the previous
    /// read had just fetched. The queue was running about a thousand SELECTs a second, for ever, and
    /// the rows on screen were built from pages that were no longer resident — so anything that asked
    /// the cache what the reader was looking at got an answer about some other part of the list.</para>
    ///
    /// <para>So the page cap still governs — for big pages it is unchanged — but it never evicts
    /// below <see cref="MinRowsCached"/> rows, and a row ceiling and a generous page ceiling bound the
    /// other end. A page costs a dictionary entry and a list node whatever its length.</para>
    /// </summary>
    private int _cachedRows;

    private int MaxRowsCached => _maxPagesCached * _pageSize;

    /// <summary>Never evict below this many rows however many pages they are spread over.</summary>
    private const int MinRowsCached = 2048;

    /// <summary>A page costs a dictionary entry and a list node whatever its length, so their number
    /// is bounded too, well above the point where the row budget normally binds.</summary>
    private int MaxPagesHeld => _maxPagesCached * 32;
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

    // Group KEY → its row index and the object now carrying it. Only populated when the host supplied
    // a key function. Built in the same lazy pass as the leaf-segment index.
    private readonly Dictionary<object, (int Index, object Group)> _groupsByKey = new();

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

    // Reverse map placeholder → its slot, so IndexOf resolves placeholder rows too.
    // Required since the source exposes non-generic IList: Uno's container index
    // repair calls IndexOf(item) on containers that are still showing placeholders,
    // and a -1 there breaks the post-collapse re-anchor (panel rebuilds from row 0).
    private readonly Dictionary<object, (object? Group, int Page, int Slot)> _placeholderIndex = new(ReferenceEqualityComparer.Instance);
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

    /// <param name="groupKeyOf">
    /// A stable identity for a group, independent of the object carrying it. <b>Returns null for
    /// anything that is not one of the host's group rows</b>, and is handed whatever
    /// <see cref="IndexOf"/> was handed — which the framework calls with containers, placeholders and
    /// the odd foreign object, not only with rows. It must never assume its argument's type. Optional, and worth
    /// supplying for any host that REBUILDS its skeleton rather than mutating it: a rebuild hands over
    /// fresh group objects, so reference identity cannot answer "where did that row go" across one,
    /// and without an answer the only honest thing a host can do after a rebuild is start again at
    /// the top. Enables <see cref="IndexOfGroupKey"/> and <see cref="FlatIndexOf"/>, and makes group
    /// <see cref="IndexOf"/> a lookup instead of a scan of every segment.
    /// </param>
    public VirtualTreeModel(
        Func<object, IReadOnlyList<object>> childrenOf,
        Func<object?, int> leafCountOf,
        FetchLeavesFn fetchLeaves,
        Func<object?, object> placeholderOf,
        Func<object?, int, object>? placeholderAtOf = null,
        int pageSize = 200,
        int maxPagesCached = 64,
        int maxConcurrentFetches = 2,
        int failureCooldownMs = 1000,
        Func<object, object?>? groupKeyOf = null,
        Func<object, (object GroupKey, int LeafOffset)?>? anchorOf = null)
    {
        _childrenOf = childrenOf;
        _groupKeyOf = groupKeyOf;
        _anchorOf = anchorOf;
        _leafCountOf = leafCountOf;
        _fetchLeaves = fetchLeaves;
        _placeholderOf = placeholderOf;
        _placeholderAtOf = placeholderAtOf;
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

    /// <summary>
    /// The rows of one group have been put in a different order, and the store already says so.
    ///
    /// <para>Says exactly that and no more: the same rows, the same count, in new slots. No page is
    /// dropped, nothing is read again and no reset is raised — throwing the whole projection away to
    /// move one row inside one group costs the reader their place in the list, for a gesture they made
    /// with the pointer still on the row.</para>
    ///
    /// <para><paramref name="rows"/> is the new occupant of each slot from <paramref name="offset"/>
    /// onwards, within the group's own leaf block. Slots that are not in a resident page are skipped:
    /// what is not cached is fetched in the store's order the next time it is asked for, which is now
    /// this order.</para>
    /// </summary>
    public void ReorderLeaves(object? group, int offset, IReadOnlyList<object> rows)
    {
        if (rows.Count == 0 || offset < 0)
            return;

        // Written first, indexed second. A row that moves from one slot to another is both a new
        // occupant and a former one, and deindexing as we go would drop the mapping that had just
        // been written for it.
        var moved = new List<((object? Group, int Page) Key, int Slot, object Row, object? Was)>();

        for (var i = 0; i < rows.Count; i++)
        {
            var local = offset + i;
            var key = (group, local / _pageSize);
            if (!_pages.TryGetValue(key, out var buffer))
                continue;

            var inPage = local % _pageSize;
            if (inPage >= buffer.Length)
                continue;

            var was = buffer[inPage];
            if (ReferenceEquals(was, rows[i]))
                continue;

            buffer[inPage] = rows[i];
            moved.Add((key, inPage, rows[i], was));
        }

        if (moved.Count == 0)
            return;

        foreach (var (key, slot, row, _) in moved)
        {
            _leafIndex[row] = (key.Group, key.Page, slot);
            if (row is INotifyPropertyChanged npc && _subscribedLeaves.Add(npc))
                npc.PropertyChanged += OnLeafPropertyChanged;
        }

        // Anything that was in one of these slots and is no longer in any of them has left the cache.
        var resident = new HashSet<object>(moved.Select(m => m.Row), ReferenceEqualityComparer.Instance);
        foreach (var (key, _, _, was) in moved)
        {
            if (was is not null && !resident.Contains(was))
                DeindexLeaf(key, was);
        }

        if (FindLeafSegment(group) is not { } seg)
            return;

        foreach (var (key, slot, row, was) in moved)
        {
            var local = key.Page * _pageSize + slot;
            if (local < seg.Length)
                ItemReplaced?.Invoke(seg.Start + local, row, was ?? row);
        }
    }

    /// <summary>
    /// One leaf changed which group it belongs to — the row moved, and nothing else did.
    ///
    /// <para>The host has already updated whatever <c>leafCountOf</c> reads (its own group counts)
    /// and the store behind <c>fetchLeaves</c>. This works out where the row was and where it now is
    /// and says exactly that: one removal and one insertion. It is the whole of what an edit does to
    /// a tree grouped by its data, and the reason a rebuild is not required to express it.</para>
    ///
    /// <para>Only the two affected groups' pages are dropped, so everything else on screen stays
    /// resident — a reset would have thrown away the entire viewport to move one row.</para>
    /// </summary>
    /// <param name="fromGroup">The group the row was in, or null for the root leaf block.</param>
    /// <param name="fromOffset">Its position within that group before the change.</param>
    /// <param name="toGroup">The group it is in now.</param>
    /// <param name="toOffset">Its position within that group now.</param>
    public void MoveLeaf(object? fromGroup, int fromOffset, object? toGroup, int toOffset)
    {
        // Where it WAS, read off the projection as it still stands.
        var removedAt = FindLeafSegment(fromGroup) is { } fromSeg && fromOffset < fromSeg.Length
            ? fromSeg.Start + fromOffset
            : -1;

        InvalidateGroupPages(fromGroup);
        if (!ReferenceEquals(fromGroup, toGroup))
            InvalidateGroupPages(toGroup);

        // The counts the host just changed are what the segments are built from.
        RebuildSegments();

        var insertedAt = FindLeafSegment(toGroup) is { } toSeg && toOffset < toSeg.Length
            ? toSeg.Start + toOffset
            : -1;

        // A group that appeared or vanished shifts more than the two rows, and a host that got its
        // offsets wrong must not be allowed to corrupt the projection — say so honestly instead.
        if (removedAt < 0 || insertedAt < 0)
        {
            ResetRaised?.Invoke();
            return;
        }

        // Sequential, and in this order: the consumer applies the removal first, so the insertion
        // index is the one in the FINAL projection — which is exactly what the rebuilt segments give.
        RangeRemoved?.Invoke(removedAt, 1);
        RangeInserted?.Invoke(insertedAt, 1);
    }

    /// <summary>
    /// A group's leaf count changed — rows were added to it or taken out of it — without any single
    /// row moving somewhere identifiable. Emits the run that appeared or disappeared, or a reset when
    /// the delta is large enough that per-row events would cost more than a rebind
    /// (<see cref="BulkDeltaThreshold"/>, the same rule expand/collapse answers to).
    /// </summary>
    public void LeafCountChanged(object? group)
    {
        var before = FindLeafSegment(group);
        var beforeLength = before?.Length ?? 0;
        var beforeStart = before?.Start ?? -1;

        InvalidateGroupPages(group);
        RebuildSegments();

        var after = FindLeafSegment(group);
        var afterLength = after?.Length ?? 0;
        var delta = afterLength - beforeLength;

        if (delta == 0)
            return;

        var at = after?.Start ?? beforeStart;
        if (at < 0 || Math.Abs(delta) > BulkDeltaThreshold)
        {
            ResetRaised?.Invoke();
            return;
        }

        if (delta > 0)
            RangeInserted?.Invoke(at + beforeLength, delta);
        else
            RangeRemoved?.Invoke(at + afterLength, -delta);
    }

    /// <summary>Drops one group's cached pages and everything that pointed into them, leaving every
    /// other group resident. The blunt <see cref="Refresh"/> drops all of them.</summary>
    public void InvalidateGroupPages(object? group)
    {
        var doomed = new List<(object? Group, int Page)>();
        foreach (var key in _pages.Keys)
        {
            if (ReferenceEquals(key.Group, group))
                doomed.Add(key);
        }

        foreach (var key in doomed)
        {
            if (_pages.TryGetValue(key, out var buffer))
            {
                foreach (var row in buffer)
                {
                    if (row is null)
                        continue;
                    if (row is INotifyPropertyChanged npc && _subscribedLeaves.Remove(npc))
                        npc.PropertyChanged -= OnLeafPropertyChanged;
                    DeindexLeaf(key, row);
                }
            }
            _pages.Remove(key);
            DropPlaceholderPage(key);
            if (_pageLruNodes.Remove(key, out var node))
                _pageLru.Remove(node);
        }
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
        _cachedRows = 0;
        _placeholderPages.Clear();
        _placeholderIndex.Clear();
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
        var groupIndex = IndexOf(sender);

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

    /// <summary>
    /// How many GROUP rows sit among the flat rows [0, <paramref name="index"/>). Lets a host with
    /// more than one row height compute a row's exact extent offset — group segments are single rows
    /// and leaf blocks are all leaves, so one walk over the segments answers it without touching a
    /// page. O(number of segments), which is O(visible groups).
    /// </summary>
    /// <param name="match">
    /// Optional filter on the group itself, for a host whose groups are not all the same height (an
    /// artist row taller than an album row). Null counts every group row.
    /// </param>
    public int GroupRowsBefore(int index, Func<object?, bool>? match = null)
    {
        var groups = 0;
        foreach (var seg in _segments)
        {
            if (seg.Start >= index)
                break;
            if (!seg.IsLeafBlock && (match is null || match(seg.Group)))
                groups++;
        }
        return groups;
    }

    // ── Row access ──────────────────────────────────────────────────

    /// <summary>
    /// The row at <paramref name="index"/>: the group object itself for
    /// group rows; for leaf rows the cached leaf, or the group's placeholder
    /// with a page fetch kicked off as a side effect.
    /// </summary>
    public object? GetAt(int index) { DiagReads++; return At(index, fetchIfMissing: true); }

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
        var resident = _pages.TryGetValue(key, out var buffer);

        // Only when this read MISSED. Speculating on every read costs nothing when the neighbour is
        // already resident, but the queue's blocks are one record each and a read is therefore always
        // within a margin of its block's edge, so every read queued both neighbours, every page that
        // landed replaced its rows one by one, and each replacement was another read that speculated
        // again. Measured on 2026-09-11 over 26,663 rows: scrolling 6,000 of them cost 11,687 reads of
        // the store where the rows themselves account for 660. Asked only when a read ran out, the
        // same scroll costs 660 and still loads the next block before the reader reaches it.
        if (fetchIfMissing && !resident)
            MaybePrefetchNeighbor(seg, local);

        if (resident)
        {
            TouchLru(key);
            return buffer![inPage] ?? PlaceholderAt(key, inPage);
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

    /// <summary>Sequential scrolling should meet pages that already arrived: when a read
    /// lands within a quarter-page of a page edge, the adjacent page is queued at the BACK
    /// of the fetch queue — it fills spare fetch capacity without ever delaying the page
    /// the viewport is actually on.</summary>
    private void MaybePrefetchNeighbor(Segment seg, int local)
    {
        var margin = Math.Max(1, _pageSize / 4);
        var inPage = local % _pageSize;
        var page = local / _pageSize;
        var lastPage = (seg.Length - 1) / _pageSize;

        if (inPage >= _pageSize - margin && page < lastPage)
            QueuePrefetch((seg.Group, page + 1));
        if (inPage < margin && page > 0)
            QueuePrefetch((seg.Group, page - 1));

        // Real collections are LUMPY: most folders hold fewer rows than a page, so every
        // folder is its own leaf block and page-level prefetch never reaches the next one —
        // each album boundary would flash placeholders. Nearing a block edge prefetches the
        // adjacent block (skipping group rows, which are in-memory).
        if (seg.Length - local <= margin)
            PrefetchAdjacentBlock(seg.Start + seg.Length, forward: true);
        if (local < margin && seg.Start > 0)
            PrefetchAdjacentBlock(seg.Start - 1, forward: false);
    }

    private void PrefetchAdjacentBlock(int index, bool forward)
    {
        // A few hops: group rows (and possibly a childless group chain) sit between blocks.
        for (var hop = 0; hop < 4 && index >= 0 && index < _count; hop++)
        {
            var seg = FindSegment(index);
            if (seg.IsLeafBlock)
            {
                var page = (index - seg.Start) / _pageSize;
                QueuePrefetch((seg.Group, page));
                return;
            }
            index = forward ? seg.Start + seg.Length : seg.Start - 1;
        }
    }

    private void QueuePrefetch((object? Group, int Page) key)
    {
        if (_pages.ContainsKey(key) || _pagesInFlight.Contains(key)
            || _pendingNodes.ContainsKey(key) || InCooldown(key))
            return;
        _pendingNodes[key] = _pending.AddFirst(key);   // back of the LIFO: lowest priority
        DrainPending();
    }

    private object PlaceholderAt((object? Group, int Page) key, int inPage)
    {
        if (!_placeholderPages.TryGetValue(key, out var buffer))
        {
            if (_placeholderPages.Count > _maxPagesCached * 2)
            {
                _placeholderPages.Clear();   // cheap bound; identities re-create on demand
                _placeholderIndex.Clear();
            }
            _placeholderPages[key] = buffer = new object?[_pageSize];
        }
        if (buffer[inPage] is { } cached)
            return cached;
        var created = _placeholderAtOf is { } withIndex
            ? withIndex(key.Group, key.Page * _pageSize + inPage)
            : _placeholderOf(key.Group);
        buffer[inPage] = created;
        _placeholderIndex[created] = (key.Group, key.Page, inPage);
        return created;
    }

    /// <summary>Drops a slot's placeholder page AND its reverse-map entries — always use
    /// this instead of removing from <see cref="_placeholderPages"/> directly, or
    /// <see cref="IndexOf"/> keeps resolving placeholders that no longer occupy a slot.</summary>
    private void DropPlaceholderPage((object? Group, int Page) key)
    {
        if (!_placeholderPages.Remove(key, out var buffer))
            return;
        foreach (var p in buffer)
            if (p is not null)
                _placeholderIndex.Remove(p);
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
    /// <summary>Diagnostics for the hoster's fling probe: how often rows are read and how
    /// often IndexOf misses the O(1) map (falling to the segment scan). Plain increments —
    /// no cost worth gating.</summary>
    public static int DiagReads;
    public static int DiagIndexOfScans;

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

        // Placeholders resolve exactly like cached leaves — they ARE the items
        // realized containers show for unfetched rows, and identity consumers
        // (container index repair, ContainerFromItem) probe them the same way.
        if (_placeholderIndex.TryGetValue(item, out var ploc))
        {
            if (FindLeafSegment(ploc.Group) is not { } pseg)
                return -1;
            var flat = pseg.Start + ploc.Page * _pageSize + ploc.Slot;
            return flat < pseg.Start + pseg.Length ? flat : -1;
        }

        // A keyed host resolves group rows by lookup. Reference-checked afterwards, because two
        // objects can share a key across a rebuild and only the one in the tree has an index.
        // Anything at all can arrive here — ItemsControl.IndexFromContainer asks IndexOf(container),
        // and an automation peer asks it about a row. So the key function is asked, not told, and a
        // null answer means "not one of mine" rather than an error.
        if (_groupKeyOf is not null)
        {
            EnsureSegmentIndex();
            if (_groupKeyOf(item) is { } key
                && _groupsByKey.TryGetValue(key, out var byKey)
                && ReferenceEquals(byKey.Group, item))
                return byKey.Index;
        }

        DiagIndexOfScans++;
        foreach (var seg in _segments)
        {
            if (!seg.IsLeafBlock && ReferenceEquals(seg.Group, item))
                return seg.Start;
        }
        return -1;
    }

    /// <summary>
    /// The flat row index of the group with this key, or -1 when it is not in the tree — collapsed
    /// under an ancestor, filtered out, or gone. O(1).
    /// </summary>
    public int IndexOfGroupKey(object key)
    {
        EnsureSegmentIndex();
        return _groupsByKey.TryGetValue(key, out var found) ? found.Index : -1;
    }

    /// <summary>The group object now carrying this key, or null. A rebuild replaces the objects; the
    /// key is what survives.</summary>
    public object? GroupByKey(object key)
    {
        EnsureSegmentIndex();
        return _groupsByKey.TryGetValue(key, out var found) ? found.Group : null;
    }

    /// <summary>
    /// Where a LEAF sits: the flat index of the row <paramref name="leafOffset"/> places into the
    /// leaf block of the group with this key, or -1 when that group is not showing or the offset is
    /// past its end.
    ///
    /// <para>This is the answer to "where did that row go" after a rebuild moved it — the host knows
    /// which group a row landed in and its position within it (its store can say so cheaply); the
    /// model turns that into the row index the viewport needs, without any page being fetched.</para>
    /// </summary>
    public int FlatIndexOf(object groupKey, int leafOffset)
    {
        EnsureSegmentIndex();
        if (leafOffset < 0 || !_groupsByKey.TryGetValue(groupKey, out var found))
            return -1;
        if (FindLeafSegment(found.Group) is not { } seg || leafOffset >= seg.Length)
            return -1;
        return seg.Start + leafOffset;
    }

    /// <summary>
    /// Where this row is NOW, for re-anchoring a viewport across a rebuild — the row's own index when
    /// it is still in the tree, and otherwise the index of whatever took its place.
    ///
    /// <para>A rebuild replaces every group object and drops every cached leaf, so the items a
    /// viewport was showing a moment ago are all stale references and <see cref="IndexOf"/> answers
    /// -1 for every one of them. A host that can say which group a stale row belonged to, and how far
    /// into it (the <c>anchorOf</c> constructor argument), gets a real index back instead — which is
    /// the difference between a rebuild that keeps the reader's place and one that drops them
    /// wherever the panel happens to land.</para>
    /// </summary>
    public int ResolveAnchor(object? item)
    {
        if (item is null)
            return -1;

        var direct = IndexOf(item);
        if (direct >= 0)
            return direct;

        if (_anchorOf?.Invoke(item) is not { } a)
            return -1;

        // Exactly where that slot is now, or nothing. NOT a fallback to the group's own row: an
        // anchor exists to keep a position, not to chase a row across the list, and answering with
        // somewhere-in-the-region turns a re-anchor into a long scroll to a place the reader never
        // asked for. A caller that genuinely wants to FOLLOW a row asks for it by name.
        return FlatIndexOf(a.GroupKey, a.LeafOffset);
    }

    private void EnsureSegmentIndex()
    {
        if (!_leafSegmentIndexValid)
            FindLeafSegment(null);   // the one place that builds both indexes
    }

    private Segment? FindLeafSegment(object? group)
    {
        if (!_leafSegmentIndexValid)
        {
            _leafSegmentByGroup.Clear();
            _groupsByKey.Clear();
            _rootLeafSegment = null;
            foreach (var seg in _segments)
            {
                if (!seg.IsLeafBlock)
                {
                    if (_groupKeyOf is not null && seg.Group is { } g && _groupKeyOf(g) is { } key)
                        _groupsByKey[key] = (seg.Start, g);
                    continue;
                }
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

            if (_pages.TryGetValue(key, out var replacing))
                _cachedRows -= replacing.Length;
            _pages[key] = buffer;
            _cachedRows += buffer.Length;
            _pagesInFlight.Remove(key);
            _pageFailures.Remove(key);
            _pageCooldownUntil.Remove(key);
            IndexPage(key, buffer);
            TouchLru(key);
            HookLeaves(buffer);

            // The placeholders these rows are replacing, while they are still the objects the list
            // is holding. A replacement names what it replaces, and a name nothing recognizes is not
            // one: a consumer looks the OLD item up to find the container it is showing in, so a
            // freshly built placeholder — which is what this used to hand over — left every row
            // fetched after the first screenful showing the placeholder it was already showing.
            _placeholderPages.TryGetValue(key, out var vacated);

            // Patch only the indices that changed — and only when the block
            // is still visible (the folder may have collapsed mid-fetch; the
            // cache stays warm for the next expand).
            if (FindLeafSegment(group) is { } seg)
            {
                for (var i = 0; i < buffer.Length; i++)
                {
                    if (buffer[i] is not { } row)
                        continue;
                    var flat = seg.Start + offset + i;
                    if (flat < seg.Start + seg.Length)
                        ItemReplaced?.Invoke(flat, row, Vacated(vacated, i) ?? _placeholderOf(group));
                }
            }

            // After the patch, not before it: dropping the page first threw away the identities the
            // events above are named by, and IndexOf stopped answering for placeholders that were
            // still on screen.
            DropPlaceholderPage(key);
            EvictIfNeeded();

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

    /// <summary>The placeholder that stood in a slot, or null when nothing was ever drawn there —
    /// a page fetched ahead of the viewport is replacing rows nobody has asked for yet.</summary>
    private static object? Vacated(object?[]? placeholders, int slot)
        => placeholders is not null && slot < placeholders.Length ? placeholders[slot] : null;

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
        while (((_pages.Count > _maxPagesCached && _cachedRows > MinRowsCached)
                || _cachedRows > MaxRowsCached
                || _pages.Count > MaxPagesHeld)
               && _pageLru.First is { } victim)
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
            if (_pages.Remove(victim.Value, out var dropped))
                _cachedRows -= dropped.Length;
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
