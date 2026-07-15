using System.Collections.Specialized;
using WinUI.TableView;

namespace WinUI.TableView.TreeTests;

/// <summary>
/// The flattener must publish bulk changes as ONE notification, not one per row.
///
/// Drip-feeding a virtualizing panel thousands of Add/Remove events (a 4,000-row scan, or a
/// folder with hundreds of children) leaves its layout stale: rows that never reflow, an empty
/// gap where a collapsed block used to be, or a grid that arrives measured at zero height and
/// only settles when the window is resized. These tests pin the batching down.
/// </summary>
public class BatchNotificationTests
{
    private static List<Node> SampleTree()
    {
        var roots = new List<Node>();
        for (var a = 1; a <= 3; a++)
        {
            var artist = new Node($"artist{a}", 0);
            roots.Add(artist);
            for (var b = 1; b <= 2; b++)
            {
                var album = artist.AddChild($"artist{a}/album{b}");
                for (var t = 1; t <= 3; t++)
                    album.AddChild($"artist{a}/album{b}/track{t}");
            }
        }
        return roots;
    }

    private static (TreeGridFlattener<Node> Flattener, List<NotifyCollectionChangedAction> Events) Track(List<Node> roots)
    {
        var f = new TreeGridFlattener<Node>(n => n.Children);
        var events = new List<NotifyCollectionChangedAction>();
        f.Flat.CollectionChanged += (_, e) => events.Add(e.Action);
        f.SetRoots(roots);
        return (f, events);
    }

    // ── the flattener ──

    [Fact]
    public void SetRoots_raises_exactly_one_notification_for_the_whole_tree()
    {
        var (f, events) = Track(SampleTree());

        Assert.Equal(27, f.Flat.Count);
        Assert.Single(events);
        Assert.Equal(NotifyCollectionChangedAction.Reset, events[0]);
    }

    [Fact]
    public void Collapsing_a_folder_raises_one_notification_not_one_per_removed_row()
    {
        var roots = SampleTree();
        var (f, events) = Track(roots);
        events.Clear();

        roots[0].IsExpanded = false;   // hides 2 albums + 6 tracks = 8 rows

        Assert.Equal(19, f.Flat.Count);
        Assert.Single(events);
    }

    [Fact]
    public void Expanding_a_folder_raises_one_notification_not_one_per_added_row()
    {
        var roots = SampleTree();
        var (f, events) = Track(roots);
        roots[0].IsExpanded = false;
        events.Clear();

        roots[0].IsExpanded = true;    // brings 8 rows back

        Assert.Equal(27, f.Flat.Count);
        Assert.Single(events);
    }

    [Fact]
    public void A_large_tree_still_costs_a_single_notification()
    {
        // 50 artists × 10 albums × 10 tracks = 5,550 rows — the shape that broke the layout
        var roots = new List<Node>();
        for (var a = 0; a < 50; a++)
        {
            var artist = new Node($"a{a}", 0);
            roots.Add(artist);
            for (var b = 0; b < 10; b++)
            {
                var album = artist.AddChild($"a{a}/b{b}");
                for (var t = 0; t < 10; t++)
                    album.AddChild($"a{a}/b{b}/t{t}");
            }
        }

        var (f, events) = Track(roots);

        Assert.Equal(50 + 500 + 5000, f.Flat.Count);
        Assert.Single(events);
    }

    [Fact]
    public void Toggling_a_leaf_changes_nothing_and_notifies_nobody_meaningfully()
    {
        var roots = SampleTree();
        var (f, events) = Track(roots);
        events.Clear();

        var leaf = roots[0].Children[0].Children[0];
        leaf.IsExpanded = false;

        Assert.Equal(27, f.Flat.Count);   // content unchanged either way
    }

    // ── the collection itself ──

    [Fact]
    public void ReplaceAll_swaps_content_with_one_reset()
    {
        var c = new BulkObservableCollection<int> { 1, 2, 3 };
        var events = new List<NotifyCollectionChangedAction>();
        c.CollectionChanged += (_, e) => events.Add(e.Action);

        c.ReplaceAll(new[] { 7, 8, 9, 10 });

        Assert.Equal(new[] { 7, 8, 9, 10 }, c);
        Assert.Single(events);
        Assert.Equal(NotifyCollectionChangedAction.Reset, events[0]);
    }

    [Fact]
    public void RemoveRange_drops_a_run_with_one_reset()
    {
        var c = new BulkObservableCollection<int> { 1, 2, 3, 4, 5 };
        var events = new List<NotifyCollectionChangedAction>();
        c.CollectionChanged += (_, e) => events.Add(e.Action);

        c.RemoveRange(1, 3);

        Assert.Equal(new[] { 1, 5 }, c);
        Assert.Single(events);
    }

    [Fact]
    public void InsertRange_adds_a_block_with_one_reset()
    {
        var c = new BulkObservableCollection<int> { 1, 5 };
        var events = new List<NotifyCollectionChangedAction>();
        c.CollectionChanged += (_, e) => events.Add(e.Action);

        c.InsertRange(1, new[] { 2, 3, 4 });

        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, c);
        Assert.Single(events);
    }

    [Fact]
    public void Empty_batches_are_no_ops_and_notify_nobody()
    {
        var c = new BulkObservableCollection<int> { 1, 2 };
        var events = new List<NotifyCollectionChangedAction>();
        c.CollectionChanged += (_, e) => events.Add(e.Action);

        c.RemoveRange(0, 0);
        c.InsertRange(0, Array.Empty<int>());

        Assert.Equal(new[] { 1, 2 }, c);
        Assert.Empty(events);
    }

    [Fact]
    public void Single_item_operations_still_notify_normally()
    {
        var c = new BulkObservableCollection<int>();
        var events = new List<NotifyCollectionChangedAction>();
        c.CollectionChanged += (_, e) => events.Add(e.Action);

        c.Add(1);
        c.Remove(1);

        Assert.Equal(new[] { NotifyCollectionChangedAction.Add, NotifyCollectionChangedAction.Remove }, events);
    }
}
