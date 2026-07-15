using System.ComponentModel;
using System.Reflection;
using WinUI.TableView;

namespace WinUI.TableView.TreeTests;

/// <summary>
/// Regression tests for the fetch scheduler added to <see cref="VirtualTreeModel"/>:
/// failed-fetch retry + cooldown (FIX 1), newest-first LIFO dispatch under a
/// concurrency cap (FIX 2), and the O(1) IndexOf reverse map (FIX 3). Uses a
/// controllable backend so a fetch can be held open, completed on demand, or
/// made to fail — none of which the value-based reference harness can express.
///
/// No SynchronizationContext is installed: a held fetch completes on the test
/// thread, so the model's ConfigureAwait(true) continuation runs inline there,
/// exactly like the synchronous-fetch harness in VirtualTreeModelTests.
/// </summary>
public class VirtualTreeSchedulerTests
{
    private sealed class Grp : ITreeGridRow, INotifyPropertyChanged
    {
        private bool _isExpanded = true;
        public required string Name { get; init; }
        public int Depth { get; init; }
        public List<Grp> Children { get; } = new();
        public int LeafCount { get; init; }
        public bool HasChildren => Children.Count > 0 || LeafCount > 0;

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value) return;
                _isExpanded = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public override string ToString() => $"group:{Name}";
    }

    private sealed record Lf(string Group, int Index)
    {
        public override string ToString() => $"leaf:{Group}[{Index}]";
    }

    private sealed class Ph
    {
        public override string ToString() => "placeholder";
    }

    /// <summary>Backend that records every dispatch and can complete a page
    /// synchronously (default), hold it open (Deferred), or fail it (FailWhen).</summary>
    private sealed class Backend
    {
        private readonly int _pageSize;
        private readonly Dictionary<(object? Group, int Page), (int Offset, int Limit, string Name)> _held = new();
        private readonly Dictionary<(object? Group, int Page), TaskCompletionSource<IReadOnlyList<object>>> _tcs = new();

        public Backend(int pageSize) => _pageSize = pageSize;

        public bool Deferred;
        public Func<(object? Group, int Page), bool>? FailWhen;
        public readonly List<(object? Group, int Page)> Dispatched = new();
        public List<int> DispatchedPages => Dispatched.ConvertAll(k => k.Page);

        public Task<IReadOnlyList<object>> Fetch(object? group, int offset, int limit, CancellationToken ct)
        {
            var page = offset / _pageSize;
            var key = (group, page);
            Dispatched.Add(key);

            if (FailWhen?.Invoke(key) == true)
                return Task.FromException<IReadOnlyList<object>>(new InvalidOperationException("simulated store failure"));

            var name = group is Grp g ? g.Name : "<root>";
            if (!Deferred)
                return Task.FromResult(Build(name, offset, limit));

            var tcs = new TaskCompletionSource<IReadOnlyList<object>>();
            _held[key] = (offset, limit, name);
            _tcs[key] = tcs;
            return tcs.Task;
        }

        /// <summary>Completes a previously-held fetch, running the model's
        /// continuation inline on the calling thread.</summary>
        public void Complete(object? group, int page)
        {
            var key = (group, page);
            var (offset, limit, name) = _held[key];
            _held.Remove(key);
            var tcs = _tcs[key];
            _tcs.Remove(key);
            tcs.SetResult(Build(name, offset, limit));
        }

        private static IReadOnlyList<object> Build(string name, int offset, int limit)
        {
            var rows = new List<object>(limit);
            for (var i = 0; i < limit; i++)
                rows.Add(new Lf(name, offset + i));
            return rows;
        }
    }

    private sealed class Harness
    {
        public readonly List<Grp> Roots = new();
        public int RootLeafCount;
        public readonly Backend Backend;
        public readonly VirtualTreeModel Model;
        private readonly Dictionary<object, Ph> _placeholders = new();
        private readonly Ph _rootPlaceholder = new();

        public Harness(int pageSize = 4, int maxPagesCached = 64,
            int maxConcurrentFetches = 2, int failureCooldownMs = 1000)
        {
            Backend = new Backend(pageSize);
            Model = new VirtualTreeModel(
                childrenOf: g => ((Grp)g).Children,
                leafCountOf: g => g is Grp grp ? grp.LeafCount : RootLeafCount,
                fetchLeaves: Backend.Fetch,
                placeholderOf: PlaceholderFor,
                pageSize: pageSize,
                maxPagesCached: maxPagesCached,
                maxConcurrentFetches: maxConcurrentFetches,
                failureCooldownMs: failureCooldownMs);
        }

        public object PlaceholderFor(object? g)
        {
            if (g is null)
                return _rootPlaceholder;
            if (!_placeholders.TryGetValue(g, out var p))
                _placeholders[g] = p = new Ph();
            return p;
        }

        public void SetRoots() => Model.SetRoots(Roots);
        public bool IsPlaceholder(object? row) => row is Ph;

        // Reflection over the model's private scheduler state (the bench does the
        // same in CheckInvariants; there is no public surface for these counts).
        private int Count(string field)
        {
            var obj = typeof(VirtualTreeModel).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Model)!;
            return (int)obj.GetType().GetProperty("Count")!.GetValue(obj)!;
        }
        public int InFlightCount => Count("_pagesInFlight");
        public int PendingCount => Count("_pending");
        public int CooldownCount => Count("_pageCooldownUntil");
    }

    private static Grp Group(string name, int leaves, int depth = 0)
        => new() { Name = name, Depth = depth, LeafCount = leaves };

    // ── FIX 1: failed fetch does not strand placeholders ────────────

    [Fact]
    public void FailedFetch_ReturnsPlaceholder_ThenLaterGetAtRetriesAndSucceeds()
    {
        var h = new Harness(pageSize: 4);
        h.Roots.Add(Group("A", leaves: 3));
        h.SetRoots();

        h.Backend.FailWhen = _ => true;         // the store is momentarily broken
        var first = h.Model.GetAt(1);           // leaf index 1 → fetch throws → placeholder
        Assert.True(h.IsPlaceholder(first));
        Assert.Equal(0, h.InFlightCount);       // the slot was freed, not stranded

        h.Backend.FailWhen = null;              // the store recovers
        var second = h.Model.GetAt(1);          // next read retries and lands the real row
        Assert.Equal("leaf:A[0]", second!.ToString());
        Assert.Equal(0, h.InFlightCount);
    }

    [Fact]
    public void ThreeFailures_ThenCooldown_SuppressesRetry_UntilCooldownElapses()
    {
        var h = new Harness(pageSize: 4, failureCooldownMs: 120);
        h.Roots.Add(Group("A", leaves: 3));
        h.SetRoots();

        h.Backend.FailWhen = _ => true;
        h.Model.GetAt(1);                       // failure 1
        h.Model.GetAt(1);                       // failure 2
        h.Model.GetAt(1);                       // failure 3 → cooldown armed
        Assert.Equal(3, h.Backend.Dispatched.Count);
        Assert.Equal(1, h.CooldownCount);

        h.Model.GetAt(1);                       // within cooldown → no new dispatch
        h.Model.GetAt(1);
        Assert.Equal(3, h.Backend.Dispatched.Count);

        Thread.Sleep(260);                      // let the cooldown lapse
        h.Backend.FailWhen = null;
        var row = h.Model.GetAt(1);             // now it retries again and succeeds
        Assert.Equal(4, h.Backend.Dispatched.Count);
        Assert.Equal("leaf:A[0]", row!.ToString());
    }

    // ── FIX 2: newest-first LIFO under a concurrency cap ────────────

    [Fact]
    public void PendingFetches_DispatchNewestFirst_UnderConcurrencyCapOfOne()
    {
        var h = new Harness(pageSize: 4, maxConcurrentFetches: 1);
        h.Backend.Deferred = true;
        var a = Group("A", leaves: 12);         // pages 0,1,2 (leaf block starts at flat index 1)
        h.Roots.Add(a);
        h.SetRoots();

        h.Model.GetAt(1);                       // request page 0 → starts (fills the one slot)
        h.Model.GetAt(5);                       // request page 1 → queued
        h.Model.GetAt(9);                       // request page 2 → queued on top (newest)
        Assert.Equal(new[] { 0 }, h.Backend.DispatchedPages);

        h.Backend.Complete(a, 0);               // slot frees → drain starts the NEWEST queued (page 2)
        Assert.Equal(new[] { 0, 2 }, h.Backend.DispatchedPages);

        h.Backend.Complete(a, 2);               // then the older one (page 1)
        Assert.Equal(new[] { 0, 2, 1 }, h.Backend.DispatchedPages);

        h.Backend.Complete(a, 1);
        Assert.Equal(0, h.PendingCount);
        Assert.Equal(0, h.InFlightCount);
    }

    [Fact]
    public void PendingFetch_ForGroupCollapsedWhileQueued_IsDropped()
    {
        var h = new Harness(pageSize: 4, maxConcurrentFetches: 1);
        h.Backend.Deferred = true;
        var g1 = Group("G1", leaves: 4);
        var g2 = Group("G2", leaves: 4);
        h.Roots.Add(g1);
        h.Roots.Add(g2);
        h.SetRoots();                           // 0:G1 1..4:G1-leaves 5:G2 6..9:G2-leaves

        h.Model.GetAt(1);                       // page 0 of G1 → starts
        h.Model.GetAt(6);                       // page 0 of G2 → queued
        Assert.Equal(1, h.PendingCount);

        g2.IsExpanded = false;                  // G2's leaf block vanishes while its page waits
        h.Backend.Complete(g1, 0);              // drain pops the G2 page and drops it (not visible)

        Assert.DoesNotContain(h.Backend.Dispatched, k => ReferenceEquals(k.Group, g2));
        Assert.Equal(0, h.PendingCount);
        Assert.Equal(0, h.InFlightCount);
    }

    [Fact]
    public void SetRoots_ClearsPendingQueue_NoStaleFetchDispatchedAfterwards()
    {
        var h = new Harness(pageSize: 4, maxConcurrentFetches: 1);
        h.Backend.Deferred = true;
        var g1 = Group("G1", leaves: 4);
        var g2 = Group("G2", leaves: 4);
        h.Roots.Add(g1);
        h.Roots.Add(g2);
        h.SetRoots();

        h.Model.GetAt(1);                       // page 0 of G1 → starts (held open)
        h.Model.GetAt(6);                       // page 0 of G2 → queued
        Assert.Equal(1, h.PendingCount);

        h.SetRoots();                           // search/refresh mid-queue → CancelFetches
        Assert.Equal(0, h.PendingCount);

        // Completing the now-cancelled G1 fetch must not drain the stale G2 page.
        h.Backend.Complete(g1, 0);
        Assert.DoesNotContain(h.Backend.Dispatched, k => ReferenceEquals(k.Group, g2));
    }

    // ── FIX 3: IndexOf through the reverse map after eviction ───────

    [Fact]
    public void IndexOf_AfterEviction_EvictedLeafIsMinusOne_ResidentLeafIsCorrect()
    {
        var h = new Harness(pageSize: 2, maxPagesCached: 2);
        h.RootLeafCount = 10;                   // 5 pages; only 2 stay resident
        h.SetRoots();

        var early = h.Model.GetAt(0);           // page 0 loaded; captured before it is evicted
        for (var i = 0; i < 10; i++)            // sweep evicts the early pages
            h.Model.GetAt(i);
        var resident = h.Model.GetAt(9);        // last page is most-recently-used → resident

        Assert.Equal(-1, h.Model.IndexOf(early));      // its page was evicted + de-indexed
        Assert.Equal(9, h.Model.IndexOf(resident));    // resolved through the reverse map
    }

    [Fact]
    public void IndexOf_TranslatesCollapsedLeafToMinusOne_ThenBackWhenVisible()
    {
        var h = new Harness(pageSize: 4);
        var a = Group("A", leaves: 3);
        h.Roots.Add(a);
        h.SetRoots();

        var leaf = h.Model.GetAt(2);            // real leaf at flat index 2 (page cached)
        Assert.Equal(2, h.Model.IndexOf(leaf));

        a.IsExpanded = false;                   // leaf block gone but the page stays cached
        Assert.Equal(-1, h.Model.IndexOf(leaf));

        a.IsExpanded = true;                    // block visible again → same flat index
        Assert.Equal(2, h.Model.IndexOf(leaf));
    }
}
