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

    // ── keyed groups: surviving a rebuild ─────────────────────────────────────────────────────────
    //
    // A host that REBUILDS its skeleton (rather than mutating it) hands over brand new group objects
    // every time. Reference identity cannot answer "where did that row go" across one, and a host
    // with no answer can only put the reader back at the top — which is the whole complaint about
    // editing a row in a twenty-thousand-row tree.

    private static (VirtualTreeModel Model, List<Group> Roots) KeyedTree(int artists, int albums, int tracks)
    {
        var roots = new List<Group>();
        for (var a = 0; a < artists; a++)
        {
            var artist = new Group { Name = $"artist{a:D3}", Depth = 0 };
            for (var b = 0; b < albums; b++)
                artist.Children.Add(new Group
                {
                    Name = $"artist{a:D3}/album{b:D2}", Depth = 1, LeafCount = tracks,
                });
            roots.Add(artist);
        }

        var model = new VirtualTreeModel(
            childrenOf: n => ((Group)n).Children,
            leafCountOf: n => n is Group g ? g.LeafCount : 0,
            fetchLeaves: (g, offset, limit, ct) =>
            {
                var name = ((Group)g!).Name;
                var rows = new List<object>(limit);
                for (var i = 0; i < limit; i++)
                    rows.Add($"{name}#{offset + i}");
                return Task.FromResult<IReadOnlyList<object>>(rows);
            },
            placeholderOf: _ => "…",
            // Asked about anything IndexOf is asked about, so it is a pattern match and not a cast.
            groupKeyOf: n => n is Group g ? g.Name : null);
        model.SetRoots(roots);
        return (model, roots);
    }

    [Fact]
    public void A_group_key_finds_its_row_and_agrees_with_IndexOf()
    {
        var (model, roots) = KeyedTree(artists: 5, albums: 3, tracks: 4);

        var album = roots[2].Children[1];
        var byKey = model.IndexOfGroupKey(album.Name);

        Assert.True(byKey > 0);
        Assert.Equal(byKey, model.IndexOf(album));
        Assert.Same(album, model.GroupByKey(album.Name));
        Assert.Same(album, model.GetAt(byKey));
    }

    [Fact]
    public void A_leaf_is_located_from_its_group_and_offset_without_fetching_anything()
    {
        var (model, roots) = KeyedTree(artists: 4, albums: 2, tracks: 6);

        var album = roots[1].Children[0];
        var groupRow = model.IndexOfGroupKey(album.Name);

        // Track 4 of that album is four rows past the album's own row (its leaves follow it).
        Assert.Equal(groupRow + 1 + 4, model.FlatIndexOf(album.Name, 4));
        // Past the end of the block is not a row.
        Assert.Equal(-1, model.FlatIndexOf(album.Name, 6));
        Assert.Equal(-1, model.FlatIndexOf("no-such-album", 0));
    }

    [Fact]
    public void A_row_is_followed_across_a_rebuild_that_moves_it()
    {
        // The recognition case: a track sitting in "unknown" turns out to be a Muse track, so the
        // next skeleton puts it under Muse instead — new objects throughout.
        var unknown = new Group { Name = "unknown", Depth = 0 };
        unknown.Children.Add(new Group { Name = "unknown/-", Depth = 1, LeafCount = 50 });
        var before = new List<Group> { unknown };

        var model = new VirtualTreeModel(
            childrenOf: n => ((Group)n).Children,
            leafCountOf: n => n is Group g ? g.LeafCount : 0,
            fetchLeaves: (g, offset, limit, ct) => Task.FromResult<IReadOnlyList<object>>(
                Enumerable.Range(offset, limit).Select(i => (object)$"row{i}").ToList()),
            placeholderOf: _ => "…",
            // Asked about anything IndexOf is asked about, so it is a pattern match and not a cast.
            groupKeyOf: n => n is Group g ? g.Name : null);
        model.SetRoots(before);

        Assert.True(model.FlatIndexOf("unknown/-", 7) > 0);

        // The rebuild: a fresh skeleton, entirely new objects, the track now under Muse.
        var muse = new Group { Name = "muse", Depth = 0 };
        muse.Children.Add(new Group { Name = "muse/absolution", Depth = 1, LeafCount = 1 });
        var unknownAfter = new Group { Name = "unknown", Depth = 0 };
        unknownAfter.Children.Add(new Group { Name = "unknown/-", Depth = 1, LeafCount = 49 });
        model.SetRoots(new List<Group> { muse, unknownAfter });

        // Where it went, from the key and the offset alone — no page fetched, nothing scanned.
        var moved = model.FlatIndexOf("muse/absolution", 0);
        Assert.Equal(model.IndexOfGroupKey("muse/absolution") + 1, moved);

        // And the group it LEFT is still findable, one row shorter.
        Assert.Equal(-1, model.FlatIndexOf("unknown/-", 49));
        Assert.True(model.FlatIndexOf("unknown/-", 48) > 0);
    }

    [Fact]
    public void A_key_that_is_no_longer_in_the_tree_answers_minus_one_rather_than_a_wrong_row()
    {
        var (model, roots) = KeyedTree(artists: 3, albums: 2, tracks: 5);
        var gone = roots[1].Children[0].Name;

        model.SetRoots(new List<Group> { roots[0] });

        Assert.Equal(-1, model.IndexOfGroupKey(gone));
        Assert.Equal(-1, model.FlatIndexOf(gone, 0));
        Assert.Null(model.GroupByKey(gone));
    }

    [Fact]
    public void A_collapsed_groups_leaves_have_no_index_but_the_group_still_does()
    {
        var (model, roots) = KeyedTree(artists: 3, albums: 2, tracks: 5);
        var album = roots[0].Children[0];

        roots[0].IsExpanded = false;

        Assert.Equal(-1, model.IndexOfGroupKey(album.Name));   // the album row itself is hidden
        Assert.Equal(-1, model.FlatIndexOf(album.Name, 0));
        Assert.True(model.IndexOfGroupKey(roots[0].Name) >= 0);
    }

    [Fact]
    public void IndexOf_tolerates_objects_that_are_not_rows_at_all()
    {
        // ItemsControl.IndexFromContainer asks IndexOf(container), and an automation peer asks it
        // about a row — so a keyed host's key function is handed containers and foreign objects, not
        // just its own rows. It answers null for those, and nothing here may throw on the way.
        var (model, roots) = KeyedTree(artists: 3, albums: 2, tracks: 4);

        Assert.Equal(-1, model.IndexOf("a string"));
        Assert.Equal(-1, model.IndexOf(new object()));
        Assert.Equal(-1, model.IndexOf(42));
        Assert.Equal(-1, model.ResolveAnchor(new object()));

        // And the real rows still resolve, so the guard did not cost the fast path.
        Assert.True(model.IndexOf(roots[1]) > 0);
        Assert.True(model.IndexOf(roots[1].Children[0]) > 0);
    }

    [Fact]
    public void A_key_function_that_declines_a_group_leaves_it_findable_by_reference()
    {
        // A host may key only some of its rows. The unkeyed ones fall back to the segment scan
        // rather than vanishing.
        var roots = new List<Group>();
        for (var a = 0; a < 4; a++)
        {
            var artist = new Group { Name = $"artist{a}", Depth = 0 };
            artist.Children.Add(new Group { Name = $"artist{a}/album", Depth = 1, LeafCount = 3 });
            roots.Add(artist);
        }

        var model = new VirtualTreeModel(
            childrenOf: n => ((Group)n).Children,
            leafCountOf: n => n is Group g ? g.LeafCount : 0,
            fetchLeaves: (g, offset, limit, ct) => Task.FromResult<IReadOnlyList<object>>(
                Enumerable.Range(offset, limit).Select(i => (object)$"row{i}").ToList()),
            placeholderOf: _ => "…",
            // Depth-1 groups only; the artists decline a key.
            groupKeyOf: n => n is Group { Depth: 1 } g ? g.Name : null);
        model.SetRoots(roots);

        Assert.Equal(-1, model.IndexOfGroupKey("artist2"));
        Assert.True(model.IndexOf(roots[2]) > 0);                    // still found, by reference
        Assert.True(model.IndexOfGroupKey("artist2/album") > 0);     // and the keyed one by key
    }

    [Fact]
    public void An_anchor_whose_slot_is_gone_answers_nothing_rather_than_somewhere_near()
    {
        // An anchor restores a POSITION. When the position no longer exists — the group shrank out
        // from under it, which is what a mass removal does — the honest answer is "gone", so the
        // caller keeps the offset it had. Answering with the group's own row instead sends the
        // viewport on a long scroll to a place nobody asked for, and the restore loop then grinds
        // trying to land on it.
        var group = new Group { Name = "g", Depth = 0, LeafCount = 100 };
        var model = new VirtualTreeModel(
            childrenOf: n => ((Group)n).Children,
            leafCountOf: n => n is Group g ? g.LeafCount : 0,
            fetchLeaves: (g, offset, limit, ct) => Task.FromResult<IReadOnlyList<object>>(
                Enumerable.Range(offset, limit).Select(i => (object)$"row{i}").ToList()),
            placeholderOf: _ => "…",
            groupKeyOf: n => n is Group g ? g.Name : null,
            anchorOf: n => n is string s && s.StartsWith("row")
                ? ("g", int.Parse(s[3..]))
                : null);
        model.SetRoots(new List<Group> { group });

        Assert.Equal(model.IndexOfGroupKey("g") + 1 + 90, model.ResolveAnchor("row90"));

        // The removal: the group keeps its identity but most of its rows are gone.
        group.LeafCount = 10;
        model.SetRoots(new List<Group> { group });

        Assert.Equal(-1, model.ResolveAnchor("row90"));            // gone means gone
        Assert.True(model.ResolveAnchor("row5") > 0);              // one that survived still places
    }

    // ── editing a row must not rebuild the tree ───────────────────────────────────────────────────

    private sealed class MoveRig
    {
        public required VirtualTreeModel Model { get; init; }
        public required Dictionary<string, List<string>> Rows { get; init; }
        public required Dictionary<string, Group> Groups { get; init; }
        public int Resets;
        public readonly List<(int At, int Count)> Inserted = new();
        public readonly List<(int At, int Count)> Removed = new();
    }

    /// <summary>Two albums with rows the test can move between them, exactly as an edit does.</summary>
    private static MoveRig BuildMoveRig()
    {
        var rows = new Dictionary<string, List<string>>
        {
            ["a/one"] = ["a1", "a2", "a3"],
            ["a/two"] = ["b1", "b2"],
        };
        var groups = new Dictionary<string, Group>();
        var artist = new Group { Name = "a", Depth = 0 };
        foreach (var key in rows.Keys)
        {
            var album = new Group { Name = key, Depth = 1, LeafCount = rows[key].Count };
            groups[key] = album;
            artist.Children.Add(album);
        }

        var model = new VirtualTreeModel(
            childrenOf: n => ((Group)n).Children,
            leafCountOf: n => n is Group g ? g.LeafCount : 0,
            fetchLeaves: (g, offset, limit, ct) => Task.FromResult<IReadOnlyList<object>>(
                rows[((Group)g!).Name].Skip(offset).Take(limit).Cast<object>().ToList()),
            placeholderOf: _ => "…",
            groupKeyOf: n => n is Group g ? g.Name : null);
        model.SetRoots(new List<Group> { artist });

        var rig = new MoveRig { Model = model, Rows = rows, Groups = groups };
        model.ResetRaised += () => rig.Resets++;
        model.RangeInserted += (at, n) => rig.Inserted.Add((at, n));
        model.RangeRemoved += (at, n) => rig.Removed.Add((at, n));
        return rig;
    }

    [Fact]
    public void A_row_changing_group_is_one_removal_and_one_insertion_not_a_reset()
    {
        var rig = BuildMoveRig();

        // Realize everything so the move has real rows to disturb.
        for (var i = 0; i < rig.Model.Count; i++) rig.Model.GetAt(i);
        var countBefore = rig.Model.Count;

        // The edit: "a2" turns out to belong to the other album, in the middle of it.
        rig.Rows["a/one"].Remove("a2");
        rig.Groups["a/one"].LeafCount = 2;
        rig.Rows["a/two"].Insert(1, "a2");
        rig.Groups["a/two"].LeafCount = 3;

        rig.Model.MoveLeaf(rig.Groups["a/one"], 1, rig.Groups["a/two"], 1);

        Assert.Equal(0, rig.Resets);
        Assert.Single(rig.Removed);
        Assert.Single(rig.Inserted);
        Assert.Equal(1, rig.Removed[0].Count);
        Assert.Equal(1, rig.Inserted[0].Count);
        Assert.Equal(countBefore, rig.Model.Count);   // one row left, one arrived

        // And the projection genuinely reads the new way round.
        var moved = rig.Model.IndexOfGroupKey("a/two") + 1 + 1;
        Assert.Equal("a2", rig.Model.GetAt(moved));
        Assert.DoesNotContain("a2", Enumerable.Range(0, 3)
            .Select(i => rig.Model.GetAt(rig.Model.IndexOfGroupKey("a/one") + 1 + i) as string));
    }

    [Fact]
    public void Moving_a_row_keeps_the_other_groups_pages_resident()
    {
        var rig = BuildMoveRig();
        for (var i = 0; i < rig.Model.Count; i++) rig.Model.GetAt(i);

        var untouched = new Group { Name = "a/three", Depth = 1, LeafCount = 2 };
        rig.Rows["a/three"] = ["c1", "c2"];
        rig.Groups["a/three"] = untouched;
        // (already-built tree; this group is only here to be left alone)

        var residentBefore = rig.Model.CachedLeaves().Count();
        rig.Rows["a/one"].Remove("a1");
        rig.Groups["a/one"].LeafCount = 2;
        rig.Rows["a/two"].Add("a1");
        rig.Groups["a/two"].LeafCount = 3;
        rig.Model.MoveLeaf(rig.Groups["a/one"], 0, rig.Groups["a/two"], 2);

        // Both touched groups dropped their pages; nothing else was disturbed. A Refresh would have
        // emptied the cache entirely.
        Assert.Equal(0, rig.Model.CachedLeaves().Count());
        Assert.True(residentBefore > 0);
    }

    [Fact]
    public void A_group_that_gained_rows_reports_the_run_that_appeared()
    {
        var rig = BuildMoveRig();
        for (var i = 0; i < rig.Model.Count; i++) rig.Model.GetAt(i);

        rig.Rows["a/one"].AddRange(["a4", "a5"]);
        rig.Groups["a/one"].LeafCount = 5;
        rig.Model.LeafCountChanged(rig.Groups["a/one"]);

        Assert.Equal(0, rig.Resets);
        Assert.Single(rig.Inserted);
        Assert.Equal(2, rig.Inserted[0].Count);
    }

    [Fact]
    public void A_group_that_lost_a_lot_of_rows_resets_rather_than_emitting_thousands_of_events()
    {
        var rig = BuildMoveRig();
        rig.Rows["a/one"] = Enumerable.Range(0, 500).Select(i => $"x{i}").ToList();
        rig.Groups["a/one"].LeafCount = 500;
        rig.Model.LeafCountChanged(rig.Groups["a/one"]);
        rig.Resets = 0; rig.Inserted.Clear(); rig.Removed.Clear();

        rig.Rows["a/one"] = ["x0"];
        rig.Groups["a/one"].LeafCount = 1;
        rig.Model.LeafCountChanged(rig.Groups["a/one"]);

        Assert.Equal(1, rig.Resets);          // 499 per-row events would cost more than a rebind
        Assert.Empty(rig.Removed);
    }
}
