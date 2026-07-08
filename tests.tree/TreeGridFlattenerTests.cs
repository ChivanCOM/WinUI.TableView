using System.Collections.ObjectModel;
using System.ComponentModel;
using WinUI.TableView;

namespace WinUI.TableView.TreeTests;

/// <summary>Minimal ITreeGridRow for the tests.</summary>
public sealed class Node : ITreeGridRow, INotifyPropertyChanged
{
    private bool _isExpanded = true;

    public Node(string name, int depth) { Name = name; Depth = depth; }

    public string Name { get; }
    public int Depth { get; }
    public List<Node> Children { get; } = new();
    public bool HasChildren => Children.Count > 0;

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

    public Node AddChild(string name)
    {
        var c = new Node(name, Depth + 1);
        Children.Add(c);
        return c;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public override string ToString() => Name;
}

public class TreeGridFlattenerTests
{
    /// <summary>3 artists × 2 albums × 3 tracks, everything expanded. 3+6+18 = 27 rows.</summary>
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

    private static TreeGridFlattener<Node> Flatten(List<Node> roots)
    {
        var f = new TreeGridFlattener<Node>(n => n.Children);
        f.SetRoots(roots);
        return f;
    }

    /// <summary>Reference implementation: full recursive flatten honoring IsExpanded.</summary>
    private static List<Node> Expected(IEnumerable<Node> roots)
    {
        var list = new List<Node>();
        void Walk(Node n)
        {
            list.Add(n);
            if (n.IsExpanded)
                foreach (var c in n.Children) Walk(c);
        }
        foreach (var r in roots) Walk(r);
        return list;
    }

    private static void AssertMatchesReference(TreeGridFlattener<Node> f, List<Node> roots)
    {
        var expected = Expected(roots);
        Assert.Equal(expected.Select(n => n.Name), f.Flat.Select(n => n.Name));
        Assert.Equal(expected.Count, f.Flat.Distinct().Count()); // no duplicates ever
    }

    [Fact]
    public void Initial_flatten_shows_all_expanded()
    {
        var roots = SampleTree();
        var f = Flatten(roots);
        Assert.Equal(27, f.Flat.Count);
        AssertMatchesReference(f, roots);
    }

    [Fact]
    public void Initial_flatten_respects_collapsed_state()
    {
        var roots = SampleTree();
        roots[1].IsExpanded = false; // collapsed before SetRoots
        var f = Flatten(roots);
        Assert.Equal(27 - 8, f.Flat.Count);
        AssertMatchesReference(f, roots);
    }

    [Fact]
    public void Collapse_removes_all_visible_descendants()
    {
        var roots = SampleTree();
        var f = Flatten(roots);

        roots[0].IsExpanded = false; // artist1: 2 albums + 6 tracks = 8 hidden

        Assert.Equal(19, f.Flat.Count);
        AssertMatchesReference(f, roots);
    }

    [Fact]
    public void Expand_after_collapse_restores_without_duplicates()
    {
        var roots = SampleTree();
        var f = Flatten(roots);

        roots[0].IsExpanded = false;
        roots[0].IsExpanded = true;

        Assert.Equal(27, f.Flat.Count);
        AssertMatchesReference(f, roots);
    }

    [Fact]
    public void Repeated_toggle_never_grows_the_list()
    {
        var roots = SampleTree();
        var f = Flatten(roots);

        for (var i = 0; i < 5; i++)
        {
            roots[1].IsExpanded = false;
            roots[1].IsExpanded = true;
        }

        Assert.Equal(27, f.Flat.Count);
        AssertMatchesReference(f, roots);
    }

    [Fact]
    public void Collapsed_child_stays_collapsed_when_parent_reexpands()
    {
        var roots = SampleTree();
        var f = Flatten(roots);

        var album = roots[2].Children[0];
        album.IsExpanded = false;         // hide its 3 tracks
        roots[2].IsExpanded = false;      // hide artist3's subtree
        roots[2].IsExpanded = true;       // bring it back — album stays collapsed

        Assert.Equal(27 - 3, f.Flat.Count);
        AssertMatchesReference(f, roots);
    }

    [Fact]
    public void Toggling_a_hidden_row_is_a_noop_until_visible_again()
    {
        var roots = SampleTree();
        var f = Flatten(roots);

        roots[0].IsExpanded = false;                  // hides album rows
        roots[0].Children[0].IsExpanded = false;      // toggled while hidden
        AssertMatchesReference(f, roots);

        roots[0].IsExpanded = true;                   // album1 reappears collapsed
        Assert.Equal(27 - 3, f.Flat.Count);
        AssertMatchesReference(f, roots);
    }

    [Fact]
    public void Deep_nesting_collapse_at_every_level()
    {
        var root = new Node("r", 0);
        var l1 = root.AddChild("r/1");
        var l2 = l1.AddChild("r/1/2");
        var l3 = l2.AddChild("r/1/2/3");
        l3.AddChild("r/1/2/3/4");
        var roots = new List<Node> { root };
        var f = Flatten(roots);
        Assert.Equal(5, f.Flat.Count);

        l2.IsExpanded = false;
        Assert.Equal(3, f.Flat.Count);
        AssertMatchesReference(f, roots);

        root.IsExpanded = false;
        Assert.Equal(1, f.Flat.Count);

        root.IsExpanded = true;
        l2.IsExpanded = true;
        Assert.Equal(5, f.Flat.Count);
        AssertMatchesReference(f, roots);
    }

    [Fact]
    public void SetRoots_twice_replaces_content_and_unhooks_old_rows()
    {
        var first = SampleTree();
        var f = Flatten(first);

        var second = SampleTree();
        f.SetRoots(second);
        Assert.Equal(27, f.Flat.Count);

        // Toggling a row from the OLD tree must not touch the new flat view.
        first[0].IsExpanded = false;
        Assert.Equal(27, f.Flat.Count);
        AssertMatchesReference(f, second);
    }

    [Fact]
    public void Leaf_toggle_changes_nothing()
    {
        var roots = SampleTree();
        var f = Flatten(roots);
        var leaf = roots[0].Children[0].Children[0];

        leaf.IsExpanded = false;
        leaf.IsExpanded = true;

        Assert.Equal(27, f.Flat.Count);
        AssertMatchesReference(f, roots);
    }

    [Fact]
    public void Empty_roots_yield_empty_flat()
    {
        var f = Flatten(new List<Node>());
        Assert.Empty(f.Flat);
    }

    [Fact]
    public void Random_toggle_storm_always_matches_reference_model()
    {
        var roots = SampleTree();
        var f = Flatten(roots);

        var all = Expected(roots).ToList(); // every node (everything starts expanded)
        var rng = new Random(1234);

        for (var i = 0; i < 500; i++)
        {
            var node = all[rng.Next(all.Count)];
            node.IsExpanded = !node.IsExpanded;
            AssertMatchesReference(f, roots);
        }
    }
}
