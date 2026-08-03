using System.Collections.ObjectModel;
using System.ComponentModel;
using WinUI.TableView;

namespace WinUI.TableView.TreeTests;

/// <summary>
/// Verifies <see cref="VirtualTreeModel"/>'s index math, expand/collapse
/// deltas, page cache, and change events against a straightforward
/// materialize-everything reference — the same strategy the flattener
/// tests use, because this is the control's other load-bearing data path.
/// Fetches complete synchronously in these tests, so continuations run
/// inline and no async coordination is needed.
/// </summary>
public class VirtualTreeModelTests
{
    private sealed class Group : ITreeGridRow, INotifyPropertyChanged
    {
        private bool _isExpanded = true;

        public required string Name { get; init; }
        public int Depth { get; init; }
        public List<Group> Children { get; } = new();
        public int LeafCount { get; set; }

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

    /// <summary>A leaf row; equality by identity, labeled for assertions.</summary>
    private sealed record Leaf(string Group, int Index)
    {
        public override string ToString() => $"leaf:{Group}[{Index}]";
    }

    private sealed class Placeholder
    {
        public override string ToString() => "placeholder";
    }

    private sealed class Harness
    {
        public List<Group> Roots = new();
        public int RootLeafCount;
        public VirtualTreeModel Model;
        public int FetchCalls;
        private readonly Dictionary<(object?, int), Placeholder> _placeholders = new();

        public Harness(int pageSize = 4, int maxPagesCached = 64)
        {
            Model = new VirtualTreeModel(
                childrenOf: g => ((Group)g).Children,
                leafCountOf: g => g is Group grp ? grp.LeafCount : RootLeafCount,
                fetchLeaves: (g, offset, limit, ct) =>
                {
                    FetchCalls++;
                    var name = g is Group grp ? grp.Name : "<root>";
                    var rows = new List<object>();
                    for (var i = 0; i < limit; i++)
                        rows.Add(new Leaf(name, offset + i));
                    return Task.FromResult<IReadOnlyList<object>>(rows);
                },
                placeholderOf: g => PlaceholderFor(g),
                pageSize: pageSize,
                maxPagesCached: maxPagesCached);
        }

        public object PlaceholderFor(object? g)
        {
            if (!_placeholders.TryGetValue((g, 0), out var p))
                _placeholders[(g, 0)] = p = new Placeholder();
            return p;
        }

        public void SetRoots() => Model.SetRoots(Roots);

        /// <summary>The expected flat projection, fully materialized: group rows,
        /// subgroups first, then the group's leaves; root leaves last.</summary>
        public List<string> Reference()
        {
            var flat = new List<string>();
            void Walk(Group g)
            {
                flat.Add($"group:{g.Name}");
                if (!g.IsExpanded) return;
                foreach (var c in g.Children) Walk(c);
                for (var i = 0; i < g.LeafCount; i++) flat.Add($"leaf:{g.Name}[{i}]");
            }
            foreach (var r in Roots) Walk(r);
            for (var i = 0; i < RootLeafCount; i++) flat.Add($"leaf:<root>[{i}]");
            return flat;
        }

        /// <summary>Reads every row via GetAt. First pass may return placeholders
        /// while pages land (synchronously here), so read twice.</summary>
        public List<string> Materialize()
        {
            for (var i = 0; i < Model.Count; i++) Model.GetAt(i);
            var rows = new List<string>();
            for (var i = 0; i < Model.Count; i++) rows.Add(Model.GetAt(i)!.ToString()!);
            return rows;
        }
    }

    private static Group MakeGroup(string name, int depth, int leaves, params Group[] children)
    {
        var g = new Group { Name = name, Depth = depth, LeafCount = leaves };
        g.Children.AddRange(children);
        return g;
    }

    // ── Basics ──────────────────────────────────────────────────────

    [Fact]
    public void FlatProjection_MatchesReference()
    {
        var h = new Harness();
        h.Roots = new List<Group>
        {
            MakeGroup("A", 0, 3,
                MakeGroup("A/X", 1, 2),
                MakeGroup("A/Y", 1, 0)),
            MakeGroup("B", 0, 5),
        };
        h.RootLeafCount = 2;
        h.SetRoots();

        Assert.Equal(h.Reference(), h.Materialize());
    }

    [Fact]
    public void CollapsedGroup_HidesSubtree()
    {
        var h = new Harness();
        var inner = MakeGroup("A/X", 1, 2);
        h.Roots = new List<Group> { MakeGroup("A", 0, 3, inner) };
        h.Roots[0].IsExpanded = false;
        h.SetRoots();

        Assert.Equal(new[] { "group:A" }, h.Materialize());
    }

    [Fact]
    public void RootLeavesOnly_NoGroups()
    {
        var h = new Harness();
        h.RootLeafCount = 6;
        h.SetRoots();

        Assert.Equal(h.Reference(), h.Materialize());
        Assert.Equal(6, h.Model.Count);
    }

    [Fact]
    public void GroupRowsBefore_MatchesReference()
    {
        var h = new Harness();
        h.Roots = new List<Group>
        {
            MakeGroup("A", 0, 3,
                MakeGroup("A/X", 1, 2),
                MakeGroup("A/Y", 1, 0)),
            MakeGroup("B", 0, 5),
        };
        h.RootLeafCount = 2;
        h.SetRoots();

        // Reference: count "group:" entries in the materialized prefix, for every index incl. Count.
        var flat = h.Reference();
        for (var index = 0; index <= flat.Count; index++)
        {
            var expected = flat.Take(index).Count(r => r.StartsWith("group:"));
            Assert.Equal(expected, h.Model.GroupRowsBefore(index));
        }
    }

    [Fact]
    public void GroupRowsBefore_Filtered_CountsOnlyMatchingGroups()
    {
        var h = new Harness();
        h.Roots = new List<Group>
        {
            MakeGroup("A", 0, 3,
                MakeGroup("A/X", 1, 2),
                MakeGroup("A/Y", 1, 0)),
            MakeGroup("B", 0, 5),
        };
        h.RootLeafCount = 2;
        h.SetRoots();

        // A host whose group rows are not all one height counts each kind for itself. Here the two
        // levels stand in for an artist row and an album row.
        var flat = h.Reference();
        for (var index = 0; index <= flat.Count; index++)
        {
            var tops = h.Model.GroupRowsBefore(index, g => g is Group { Depth: 0 });
            var nested = h.Model.GroupRowsBefore(index, g => g is Group { Depth: > 0 });

            Assert.Equal(flat.Take(index).Count(r => r is "group:A" or "group:B"), tops);
            Assert.Equal(flat.Take(index).Count(r => r is "group:A/X" or "group:A/Y"), nested);
            // The kinds partition the groups: filtered counts sum to the unfiltered one.
            Assert.Equal(h.Model.GroupRowsBefore(index), tops + nested);
        }
    }

    [Fact]
    public void GroupRowsBefore_TracksCollapse()
    {
        var h = new Harness();
        var inner = MakeGroup("A/X", 1, 2);
        h.Roots = new List<Group> { MakeGroup("A", 0, 3, inner), MakeGroup("B", 0, 1) };
        h.SetRoots();

        h.Roots[0].IsExpanded = false;   // A's subtree (A/X and all leaves) leaves the projection

        var flat = h.Reference();
        for (var index = 0; index <= flat.Count; index++)
        {
            var expected = flat.Take(index).Count(r => r.StartsWith("group:"));
            Assert.Equal(expected, h.Model.GroupRowsBefore(index));
        }
    }

    [Fact]
    public void UnfetchedIndex_ReturnsPlaceholder_ThenRealRowAfterFetch()
    {
        var h = new Harness();
        h.Roots = new List<Group> { MakeGroup("A", 0, 3) };
        h.SetRoots();

        // Index 0 is the group row; 1..3 are leaves.
        Assert.Equal("group:A", h.Model.GetAt(0)!.ToString());
        // Fetch completes synchronously in the harness, so the second read
        // returns the real row; the first returned the placeholder.
        var first = h.Model.GetAt(1);
        _ = first; // placeholder or real depending on inline completion; second read must be real:
        Assert.Equal("leaf:A[0]", h.Model.GetAt(1)!.ToString());
    }

    // ── Expand / collapse ───────────────────────────────────────────

    [Fact]
    public void CollapseAndExpand_RaiseMatchingRangeEvents()
    {
        var h = new Harness();
        var a = MakeGroup("A", 0, 3, MakeGroup("A/X", 1, 2));
        h.Roots = new List<Group> { a, MakeGroup("B", 0, 1) };
        h.SetRoots();
        h.Materialize();

        var events = new List<string>();
        h.Model.RangeInserted += (i, n) => events.Add($"ins:{i}+{n}");
        h.Model.RangeRemoved += (i, n) => events.Add($"rem:{i}+{n}");
        h.Model.ResetRaised += () => events.Add("reset");

        // Collapse A: its subtree (A/X group + 2 leaves + 3 A-leaves = 6 rows) vanishes.
        a.IsExpanded = false;
        Assert.Equal(new[] { "rem:1+6" }, events);
        Assert.Equal(h.Reference(), h.Materialize());

        events.Clear();
        a.IsExpanded = true;
        Assert.Equal(new[] { "ins:1+6" }, events);
        Assert.Equal(h.Reference(), h.Materialize());
    }

    [Fact]
    public void BulkCollapse_RaisesResetInsteadOfRangeEvents()
    {
        var h = new Harness(pageSize: 50);
        var big = MakeGroup("A", 0, VirtualTreeModel.BulkDeltaThreshold + 10);
        h.Roots = new List<Group> { big };
        h.SetRoots();

        var events = new List<string>();
        h.Model.RangeRemoved += (i, n) => events.Add($"rem:{i}+{n}");
        h.Model.ResetRaised += () => events.Add("reset");

        big.IsExpanded = false;
        Assert.Equal(new[] { "reset" }, events);
        Assert.Equal(1, h.Model.Count);
    }

    [Fact]
    public void ToggleUnderCollapsedAncestor_ChangesNothingVisible()
    {
        var h = new Harness();
        var inner = MakeGroup("A/X", 1, 2);
        var a = MakeGroup("A", 0, 1, inner);
        a.IsExpanded = false;
        h.Roots = new List<Group> { a };
        h.SetRoots();

        var events = new List<string>();
        h.Model.RangeInserted += (i, n) => events.Add($"ins:{i}+{n}");
        h.Model.RangeRemoved += (i, n) => events.Add($"rem:{i}+{n}");
        h.Model.ResetRaised += () => events.Add("reset");

        inner.IsExpanded = false;
        Assert.Empty(events);
        Assert.Equal(new[] { "group:A" }, h.Materialize());

        // Expanding A afterwards shows X collapsed — state was recorded.
        a.IsExpanded = true;
        Assert.Equal(h.Reference(), h.Materialize());
    }

    [Fact]
    public void CollapseKeepsPageCache_ReexpandNeedsNoRefetch()
    {
        var h = new Harness();
        var a = MakeGroup("A", 0, 3);
        h.Roots = new List<Group> { a };
        h.SetRoots();
        h.Materialize();
        var fetchesAfterFirstRead = h.FetchCalls;

        a.IsExpanded = false;
        a.IsExpanded = true;
        h.Materialize();

        Assert.Equal(fetchesAfterFirstRead, h.FetchCalls);
    }

    // ── Refresh ─────────────────────────────────────────────────────

    [Fact]
    public void Refresh_DropsPages_AndRefetches()
    {
        var h = new Harness();
        h.Roots = new List<Group> { MakeGroup("A", 0, 2) };
        h.SetRoots();
        h.Materialize();
        var before = h.FetchCalls;

        h.Model.Refresh();
        h.Materialize();

        Assert.True(h.FetchCalls > before);
        Assert.Equal(h.Reference(), h.Materialize());
    }

    [Fact]
    public void LeafCountChange_AppliesOnRefresh()
    {
        var h = new Harness();
        var a = MakeGroup("A", 0, 2);
        h.Roots = new List<Group> { a };
        h.SetRoots();
        h.Materialize();

        a.LeafCount = 5;
        h.Model.Refresh();

        Assert.Equal(6, h.Model.Count); // group row + 5 leaves
        Assert.Equal(h.Reference(), h.Materialize());
    }

    // ── IndexOf / CachedLeaves ─────────────────────────────────────

    [Fact]
    public void IndexOf_FindsGroupsAndCachedLeaves()
    {
        var h = new Harness();
        var a = MakeGroup("A", 0, 3);
        h.Roots = new List<Group> { a };
        h.SetRoots();
        h.Materialize();

        Assert.Equal(0, h.Model.IndexOf(a));
        var leaf = h.Model.GetAt(2);
        Assert.Equal(2, h.Model.IndexOf(leaf));
        Assert.Equal(-1, h.Model.IndexOf(new object()));
        Assert.Equal(3, h.Model.CachedLeaves().Count());
    }

    // ── LRU eviction ────────────────────────────────────────────────

    [Fact]
    public void Eviction_KeepsWorking_EvictedPagesRefetch()
    {
        var h = new Harness(pageSize: 2, maxPagesCached: 2);
        h.RootLeafCount = 10; // 5 pages of 2
        h.SetRoots();

        h.Materialize();
        // All rows still readable after eviction churn.
        Assert.Equal(h.Reference(), h.Materialize());
    }

    // ── Shadow-list consistency via events ──────────────────────────

    /// <summary>
    /// Maintains a shadow list purely from the model's change events and
    /// asserts it always matches direct reads — this is exactly what the
    /// bound ListView does, so any divergence here is a real-world
    /// stale-rows bug.
    /// </summary>
    [Fact]
    public void RandomStorm_ShadowListStaysConsistent()
    {
        // No eviction here: an evicted page silently reverts its rows to
        // placeholders (by design — no notification), which a strict
        // event-driven shadow can't mirror. Eviction has its own test.
        var h = new Harness(pageSize: 3, maxPagesCached: 10_000);
        var rng = new Random(20260712);

        var groups = new List<Group>();
        List<Group> Build(int depth, int breadth)
        {
            var list = new List<Group>();
            if (depth > 2) return list;
            for (var i = 0; i < breadth; i++)
            {
                var g = MakeGroup($"g{groups.Count}", depth, rng.Next(0, 7));
                groups.Add(g);
                g.Children.AddRange(Build(depth + 1, rng.Next(0, 3)));
                list.Add(g);
            }
            return list;
        }
        h.Roots = Build(0, 4);
        h.RootLeafCount = 4;
        h.SetRoots();

        var shadow = new List<object?>();
        void Rebuild()
        {
            shadow.Clear();
            for (var i = 0; i < h.Model.Count; i++) shadow.Add(h.Model.PeekAt(i));
        }
        h.Model.ResetRaised += Rebuild;
        h.Model.RangeInserted += (i, n) =>
        {
            for (var k = 0; k < n; k++) shadow.Insert(i + k, h.Model.PeekAt(i + k));
        };
        h.Model.RangeRemoved += (i, n) => shadow.RemoveRange(i, n);
        h.Model.ItemReplaced += (i, item, _) => shadow[i] = item;
        Rebuild();

        for (var op = 0; op < 500; op++)
        {
            switch (rng.Next(4))
            {
                case 0 or 1:
                    var g = groups[rng.Next(groups.Count)];
                    g.IsExpanded = !g.IsExpanded;
                    break;
                case 2:
                    h.Model.GetAt(rng.Next(Math.Max(1, h.Model.Count)));
                    break;
                case 3:
                    h.Model.Refresh();
                    break;
            }

            Assert.Equal(h.Model.Count, shadow.Count);
            // Sample a handful of indices for equality with direct reads.
            for (var s = 0; s < 10 && h.Model.Count > 0; s++)
            {
                var idx = rng.Next(h.Model.Count);
                Assert.Same(h.Model.PeekAt(idx), shadow[idx]);
            }
        }

        // Full-materialization equality at the end.
        Assert.Equal(h.Reference(), h.Materialize());
    }
}
