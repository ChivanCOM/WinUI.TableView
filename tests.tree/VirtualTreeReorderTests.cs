using System.ComponentModel;

namespace WinUI.TableView.TreeTests;

/// <summary>
/// <see cref="VirtualTreeModel.ReorderLeaves"/>: a group's rows put in a different order without the
/// projection being thrown away.
///
/// <para>The point of it is what does NOT happen. No reset, no page dropped, nothing read again — the
/// rows are the objects the grid is already holding, and all the tree is told is which slot each of
/// them sits in now.</para>
/// </summary>
public class VirtualTreeReorderTests
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

    private sealed record Leaf(string Group, int Index)
    {
        public override string ToString() => $"leaf:{Group}[{Index}]";
    }

    private sealed class Fixture
    {
        public readonly List<Group> Roots = new();
        public readonly VirtualTreeModel Model;
        public int FetchCalls;
        public int Resets;
        public readonly List<int> Replaced = new();

        /// <summary>The rows handed out, so a test can reorder the very objects the model holds.</summary>
        public readonly Dictionary<(string Group, int Index), Leaf> Handed = new();

        public Fixture(int pageSize = 8)
        {
            Model = new VirtualTreeModel(
                childrenOf: g => ((Group)g).Children,
                leafCountOf: g => g is Group grp ? grp.LeafCount : 0,
                fetchLeaves: (g, offset, limit, ct) =>
                {
                    FetchCalls++;
                    var name = ((Group)g!).Name;
                    var rows = new List<object>();
                    for (var i = 0; i < limit; i++)
                    {
                        var leaf = new Leaf(name, offset + i);
                        Handed[(name, offset + i)] = leaf;
                        rows.Add(leaf);
                    }
                    return Task.FromResult<IReadOnlyList<object>>(rows);
                },
                placeholderOf: _ => new object(),
                pageSize: pageSize);

            Model.ResetRaised += () => Resets++;
            Model.ItemReplaced += (index, _, _) => Replaced.Add(index);
        }

        public Group Add(string name, int leaves)
        {
            var g = new Group { Name = name, LeafCount = leaves };
            Roots.Add(g);
            return g;
        }

        /// <summary>Reads every row in, twice, so the pages have landed.</summary>
        public void Fill()
        {
            for (var i = 0; i < Model.Count; i++) Model.GetAt(i);
            for (var i = 0; i < Model.Count; i++) Model.GetAt(i);
        }

        public List<string> Rows()
        {
            var rows = new List<string>();
            for (var i = 0; i < Model.Count; i++) rows.Add(Model.PeekAt(i)!.ToString()!);
            return rows;
        }

        public List<Leaf> Leaves(string group, params int[] indices) =>
            indices.Select(i => Handed[(group, i)]).ToList();
    }

    private static Fixture Loaded(int pageSize = 8)
    {
        var f = new Fixture(pageSize);
        f.Add("A", 4);
        f.Add("B", 3);
        f.Model.SetRoots(f.Roots);
        f.Fill();
        f.FetchCalls = 0;
        f.Resets = 0;          // SetRoots raises one; the tests are about what the reorder does
        f.Replaced.Clear();
        return f;
    }

    [Fact]
    public void A_row_moved_to_the_front_reads_there()
    {
        var f = Loaded();

        f.Model.ReorderLeaves(f.Roots[0], 0, f.Leaves("A", 3, 0, 1, 2));

        Assert.Equal(
            ["group:A", "leaf:A[3]", "leaf:A[0]", "leaf:A[1]", "leaf:A[2]",
             "group:B", "leaf:B[0]", "leaf:B[1]", "leaf:B[2]"],
            f.Rows());
    }

    [Fact]
    public void Nothing_is_read_again_and_nothing_is_reset()
    {
        var f = Loaded();

        f.Model.ReorderLeaves(f.Roots[0], 0, f.Leaves("A", 3, 0, 1, 2));

        Assert.Equal(0, f.FetchCalls);
        Assert.Equal(0, f.Resets);
    }

    /// <summary>Only the rows that actually changed slots are announced — a row put back where it
    /// already was is not news.</summary>
    [Fact]
    public void Only_the_slots_that_changed_are_announced()
    {
        var f = Loaded();

        f.Model.ReorderLeaves(f.Roots[0], 1, f.Leaves("A", 2, 1, 3));

        Assert.Equal([2, 3], f.Replaced);
    }

    [Fact]
    public void A_reorder_that_changes_nothing_says_nothing()
    {
        var f = Loaded();

        f.Model.ReorderLeaves(f.Roots[0], 0, f.Leaves("A", 0, 1, 2, 3));

        Assert.Empty(f.Replaced);
        Assert.Equal(0, f.Resets);
    }

    /// <summary>The reverse map has to move with the rows, or the grid asks where a row is and is told
    /// where it used to be — which is how a selection ends up painted on the wrong line.</summary>
    [Fact]
    public void The_rows_can_still_say_where_they_are()
    {
        var f = Loaded();

        f.Model.ReorderLeaves(f.Roots[0], 0, f.Leaves("A", 3, 0, 1, 2));

        Assert.Equal(1, f.Model.IndexOf(f.Handed[("A", 3)]));
        Assert.Equal(2, f.Model.IndexOf(f.Handed[("A", 0)]));
        Assert.Equal(4, f.Model.IndexOf(f.Handed[("A", 2)]));
    }

    /// <summary>A record longer than a page is arranged across the page boundary like any other.</summary>
    [Fact]
    public void It_reaches_across_a_page_boundary()
    {
        var f = new Fixture(pageSize: 2);
        f.Add("A", 4);
        f.Model.SetRoots(f.Roots);
        f.Fill();

        f.Model.ReorderLeaves(f.Roots[0], 0, f.Leaves("A", 1, 2, 3, 0));

        Assert.Equal(["group:A", "leaf:A[1]", "leaf:A[2]", "leaf:A[3]", "leaf:A[0]"], f.Rows());
        Assert.Equal(4, f.Model.IndexOf(f.Handed[("A", 0)]));
    }

    [Fact]
    public void A_group_that_was_never_read_is_left_alone()
    {
        var f = Loaded();
        var untouched = f.Rows();

        f.Model.ReorderLeaves(f.Roots[1], 40, f.Leaves("B", 0, 1));

        Assert.Equal(untouched, f.Rows());
        Assert.Equal(0, f.Resets);
    }
}
