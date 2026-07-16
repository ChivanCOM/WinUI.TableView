using System.ComponentModel;
using WinUI.TableView;

namespace WinUI.TableView.TreeTests;

/// <summary>
/// Regression tests for the streaming-scroll additions: adjacent-page prefetch
/// (a read near a page edge queues the neighbor at LOW priority) and per-slot
/// context placeholders (the optional placeholderAtOf factory receives the
/// group-local leaf index).
/// </summary>
public class VirtualTreePrefetchTests
{
    private sealed class Grp : ITreeGridRow, INotifyPropertyChanged
    {
        public int Depth => 0;
        public bool HasChildren => true;
        public int LeafCount { get; init; }
        public bool IsExpanded { get; set; } = true;
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Unused() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
    }

    private static (VirtualTreeModel Model, List<int> DispatchedOffsets) Make(
        Grp group, int pageSize = 10, int cap = 2, Func<object?, int, object>? placeholderAtOf = null)
    {
        var dispatched = new List<int>();
        var model = new VirtualTreeModel(
            childrenOf: _ => Array.Empty<object>(),
            leafCountOf: g => g is Grp grp ? grp.LeafCount : 0,
            fetchLeaves: (g, offset, limit, ct) =>
            {
                dispatched.Add(offset);
                var rows = new List<object>(limit);
                for (var i = 0; i < limit; i++)
                    rows.Add($"leaf{offset + i}");
                return Task.FromResult<IReadOnlyList<object>>(rows);
            },
            placeholderOf: _ => "…",
            placeholderAtOf: placeholderAtOf,
            pageSize: pageSize,
            maxConcurrentFetches: cap);
        model.SetRoots(new object[] { group });
        return (model, dispatched);
    }

    [Fact]
    public void Read_near_page_end_prefetches_the_next_page()
    {
        var group = new Grp { LeafCount = 40 };
        var (model, dispatched) = Make(group);

        // Leaf index 8 within page 0 (page size 10, margin 2): current page + next page.
        model.GetAt(1 + 8);

        Assert.Contains(0, dispatched);
        Assert.Contains(10, dispatched);
        Assert.DoesNotContain(20, dispatched);
    }

    [Fact]
    public void Read_near_page_start_prefetches_the_previous_page()
    {
        var group = new Grp { LeafCount = 40 };
        var (model, dispatched) = Make(group);

        model.GetAt(1 + 21);   // leaf 21 = page 2, slot 1 → prev page 1 prefetched

        Assert.Contains(20, dispatched);
        Assert.Contains(10, dispatched);
        Assert.DoesNotContain(0, dispatched);
    }

    [Fact]
    public void Mid_page_reads_prefetch_nothing()
    {
        var group = new Grp { LeafCount = 40 };
        var (model, dispatched) = Make(group);

        model.GetAt(1 + 5);   // slot 5 of page 0: not within margin of either edge

        Assert.Equal(new[] { 0 }, dispatched);
    }

    [Fact]
    public void Prefetch_never_runs_past_the_last_page()
    {
        var group = new Grp { LeafCount = 15 };   // pages: 0 (10 rows), 1 (5 rows)
        var (model, dispatched) = Make(group);

        model.GetAt(1 + 14);   // last leaf, page 1

        Assert.Contains(10, dispatched);
        Assert.DoesNotContain(20, dispatched);
    }

    [Fact]
    public void Context_placeholder_receives_the_group_local_index()
    {
        var group = new Grp { LeafCount = 40 };
        var seen = new List<int>();
        var (model, _) = Make(group, placeholderAtOf: (g, i) => { seen.Add(i); return $"… {i + 1}"; });

        // A held backend would be cleaner, but a synchronous one caches the page before the
        // placeholder is ever needed — so read through PeekAt, which never fetches.
        var p0 = model.PeekAt(1 + 3);
        var p1 = model.PeekAt(1 + 27);

        Assert.Equal("… 4", p0);
        Assert.Equal("… 28", p1);
        Assert.Equal(new[] { 3, 27 }, seen);
    }

    [Fact]
    public void Per_slot_placeholders_are_stable_and_distinct()
    {
        var group = new Grp { LeafCount = 40 };
        var (model, _) = Make(group, placeholderAtOf: (g, i) => new object());

        var a1 = model.PeekAt(1 + 2);
        var a2 = model.PeekAt(1 + 2);
        var b = model.PeekAt(1 + 3);

        Assert.Same(a1, a2);
        Assert.NotSame(a1, b);
    }
}
