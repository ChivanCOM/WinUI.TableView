using System.ComponentModel;
using WinUI.TableView;

namespace WinUI.TableView.TreeTests;

/// <summary>
/// What a page arrival SAYS about the rows it landed on.
///
/// <para>A replacement names two things: the row that arrived and the row it took the place of. The
/// second one is not decoration — a list looks the old item up to find the container that is showing
/// it — so a host whose placeholders are one object per slot (which is what every real host does, or
/// its list resolves every placeholder row to the first one) must be handed back the very object it
/// was given.</para>
/// </summary>
public class VirtualTreeReplaceTests
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
    }

    private sealed record Lf(int Index);

    /// <summary>A placeholder per slot, built fresh on every call — the identity rule a list holds
    /// its containers by, and the reason the model caches them itself.</summary>
    private sealed class Ph
    {
        public override string ToString() => "placeholder";
    }

    private sealed class Held
    {
        private readonly Dictionary<int, TaskCompletionSource<IReadOnlyList<object>>> _open = new();
        private readonly Dictionary<int, (int Offset, int Limit)> _asked = new();

        public Task<IReadOnlyList<object>> Fetch(object? group, int offset, int limit, CancellationToken ct)
        {
            var page = offset / 4;
            _asked[page] = (offset, limit);
            var tcs = new TaskCompletionSource<IReadOnlyList<object>>();
            _open[page] = tcs;
            return tcs.Task;
        }

        public void Complete(int page)
        {
            var (offset, limit) = _asked[page];
            var tcs = _open[page];
            _open.Remove(page);
            var rows = new List<object>(limit);
            for (var i = 0; i < limit; i++)
                rows.Add(new Lf(offset + i));
            tcs.SetResult(rows);
        }
    }

    [Fact]
    public void A_landed_page_replaces_the_placeholders_that_were_actually_shown()
    {
        var backend = new Held();
        var group = new Grp { Name = "A", LeafCount = 8 };
        var model = new VirtualTreeModel(
            childrenOf: g => ((Grp)g).Children,
            leafCountOf: g => g is Grp grp ? grp.LeafCount : 0,
            fetchLeaves: backend.Fetch,
            placeholderOf: _ => new Ph(),
            pageSize: 4);
        model.SetRoots(new object[] { group });

        // Rows 1..4 are the group's first page: read while the fetch is still open, so what the list
        // is holding is four placeholders, one per row.
        var shown = new object[4];
        for (var i = 0; i < 4; i++)
            shown[i] = model.GetAt(1 + i)!;
        Assert.All(shown, row => Assert.IsType<Ph>(row));
        Assert.Equal(4, shown.Distinct().Count());

        var replaced = new List<(int Index, object New, object Old)>();
        model.ItemReplaced += (index, now, before) => replaced.Add((index, now, before));

        backend.Complete(0);

        Assert.Equal(4, replaced.Count);
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(1 + i, replaced[i].Index);
            Assert.Same(shown[i], replaced[i].Old);
            Assert.Same(model.GetAt(1 + i)!, replaced[i].New);
        }
    }

    [Fact]
    public void A_row_nobody_had_looked_at_still_replaces_something()
    {
        // Only the first row of the page was ever read, so the other three have no placeholder to
        // name. The event still has to be well-formed — a null old item is not one.
        var backend = new Held();
        var group = new Grp { Name = "A", LeafCount = 4 };
        var model = new VirtualTreeModel(
            childrenOf: g => ((Grp)g).Children,
            leafCountOf: g => g is Grp grp ? grp.LeafCount : 0,
            fetchLeaves: backend.Fetch,
            placeholderOf: _ => new Ph(),
            pageSize: 4);
        model.SetRoots(new object[] { group });

        var first = model.GetAt(1);

        var replaced = new List<(int Index, object New, object Old)>();
        model.ItemReplaced += (index, now, before) => replaced.Add((index, now, before));

        backend.Complete(0);

        Assert.Equal(4, replaced.Count);
        Assert.Same(first, replaced[0].Old);
        Assert.All(replaced, r => Assert.NotNull(r.Old));
    }
}
