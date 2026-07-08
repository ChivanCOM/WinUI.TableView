using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WinUI.TableView;

namespace TreeGrid.HosterApp;

/// <summary>A demo row: folder levels (artist/album) + track leaves.</summary>
public sealed partial class DemoNode : ITreeGridRow, INotifyPropertyChanged
{
    private bool _isExpanded = true;

    public DemoNode(string name, int depth, string artist = "", string title = "")
    {
        Name = name;
        Depth = depth;
        Artist = artist;
        Title = title;
    }

    public string Name { get; }
    public int Depth { get; }
    public string Artist { get; }
    public string Title { get; }
    public List<DemoNode> Children { get; } = new();
    public bool HasChildren => Children.Count > 0;
    public string Glyph => HasChildren ? "📁" : "🎵";

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
    public override string ToString() => Name;
}

/// <summary>
/// The playground + self-test. Buttons drive every tree-grid function by hand; the
/// self-test runs the same operations programmatically at launch, validating after each
/// step that (a) the flattener matches a reference recursive flatten, (b) the TableView's
/// Items mirror the flat list, and (c) every realized row on screen belongs to the flat
/// list (catches stale recycled containers).
/// </summary>
public sealed partial class HosterView : Grid
{
    private readonly TreeGridFlattener<DemoNode> _flattener = new(n => n.Children);
    private List<DemoNode> _roots = new();
    private readonly TableView _table;
    private readonly TextBlock _status;
    private readonly Random _rng = new(42);

    public HosterView()
    {
        Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x0E, 0x15, 0x20));
        Padding = new Thickness(16);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowSpacing = 10;

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        toolbar.Children.Add(MakeButton("Expand all", () => ForEachNode(n => n.IsExpanded = true)));
        toolbar.Children.Add(MakeButton("Collapse all", () => ForEachNode(n => n.IsExpanded = false)));
        toolbar.Children.Add(MakeButton("Collapse roots", () => _roots.ForEach(r => r.IsExpanded = false)));
        toolbar.Children.Add(MakeButton("Toggle random", () => ToggleRandom(1)));
        toolbar.Children.Add(MakeButton("Toggle random ×25", () => ToggleRandom(25)));
        toolbar.Children.Add(MakeButton("Rebuild roots", () => { BuildTree(); _flattener.SetRoots(_roots); }));
        toolbar.Children.Add(MakeButton("Verify now", async () => await VerifyAsync("manual")));
        toolbar.Children.Add(MakeButton("Run self-test", async () => await RunSelfTestAsync()));
        SetRow(toolbar, 0);
        Children.Add(toolbar);

        _table = new TableView
        {
            AutoGenerateColumns = false,
            SelectionMode = ListViewSelectionMode.Extended,
            RowHeight = 30,
            RowMaxHeight = 30,
            CanSortColumns = false,
            CanFilterColumns = false,
            ShowExportOptions = false,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        _table.Columns.Add(new TableViewTreeColumn
        {
            Header = "Name",
            Width = new GridLength(320),
            Binding = new Microsoft.UI.Xaml.Data.Binding { Path = new PropertyPath(nameof(DemoNode.Name)) },
            GlyphBinding = new Microsoft.UI.Xaml.Data.Binding { Path = new PropertyPath(nameof(DemoNode.Glyph)) },
        });
        _table.Columns.Add(new TableViewTextColumn
        {
            Header = "Artist",
            Width = new GridLength(160),
            IsReadOnly = true,
            Binding = new Microsoft.UI.Xaml.Data.Binding { Path = new PropertyPath(nameof(DemoNode.Artist)) },
        });
        _table.Columns.Add(new TableViewTextColumn
        {
            Header = "Title",
            Width = new GridLength(200),
            IsReadOnly = true,
            Binding = new Microsoft.UI.Xaml.Data.Binding { Path = new PropertyPath(nameof(DemoNode.Title)) },
        });
        SetRow(_table, 1);
        Children.Add(_table);

        _status = new TextBlock
        {
            Text = "self-test pending…",
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            FontSize = 14,
        };
        SetRow(_status, 2);
        Children.Add(_status);

        BuildTree();
        _flattener.SetRoots(_roots);
        _table.ItemsSource = _flattener.Flat;

        Loaded += async (_, _) =>
        {
            await Task.Delay(600); // let first layout settle
            await RunSelfTestAsync();
        };
    }

    private static Button MakeButton(string label, Action action)
    {
        var b = new Button { Content = label };
        b.Click += (_, _) => action();
        return b;
    }

    // ── tree building ──

    private void BuildTree(int artists = 4, int albums = 3, int tracks = 4)
    {
        _roots = new List<DemoNode>();
        for (var a = 1; a <= artists; a++)
        {
            var artist = new DemoNode($"Artist {a}", 0);
            _roots.Add(artist);
            for (var b = 1; b <= albums; b++)
            {
                var album = new DemoNode($"Album {a}.{b}", 1);
                artist.Children.Add(album);
                for (var t = 1; t <= tracks; t++)
                    album.Children.Add(new DemoNode($"{t:00} - Track {a}.{b}.{t}.mp3", 2, $"Artist {a}", $"Track {a}.{b}.{t}"));
            }
        }
    }

    private void ForEachNode(Action<DemoNode> action)
    {
        void Walk(DemoNode n) { action(n); n.Children.ForEach(Walk); }
        _roots.ForEach(Walk);
    }

    private List<DemoNode> AllNodes()
    {
        var list = new List<DemoNode>();
        ForEachNode(list.Add);
        return list;
    }

    private void ToggleRandom(int count)
    {
        var all = AllNodes().Where(n => n.HasChildren).ToList();
        for (var i = 0; i < count; i++)
        {
            var n = all[_rng.Next(all.Count)];
            n.IsExpanded = !n.IsExpanded;
        }
    }

    // ── verification ──

    private List<DemoNode> ReferenceFlatten()
    {
        var list = new List<DemoNode>();
        void Walk(DemoNode n)
        {
            list.Add(n);
            if (n.IsExpanded) n.Children.ForEach(Walk);
        }
        _roots.ForEach(Walk);
        return list;
    }

    private async Task<bool> VerifyAsync(string step)
    {
        await Task.Delay(150); // let the coalesced VectorChanged rebind + layout run

        var problems = new List<string>();
        var expected = ReferenceFlatten();

        // (a) flattener output == reference recursive flatten, no duplicates
        if (!expected.SequenceEqual(_flattener.Flat))
            problems.Add($"flat mismatch (expected {expected.Count}, got {_flattener.Flat.Count})");
        if (_flattener.Flat.Distinct().Count() != _flattener.Flat.Count)
            problems.Add("duplicates in flat list");

        // (b) the TableView's item collection mirrors the flat list
        if (_table.Items.Count != _flattener.Flat.Count)
            problems.Add($"TableView.Items {_table.Items.Count} != flat {_flattener.Flat.Count}");

        // (c) realized rows: at least one when data exists, and none stale
        var realized = new List<TableViewRow>();
        void Walk(DependencyObject d)
        {
            var n = VisualTreeHelper.GetChildrenCount(d);
            for (var i = 0; i < n; i++)
            {
                var c = VisualTreeHelper.GetChild(d, i);
                if (c is TableViewRow r) realized.Add(r);
                Walk(c);
            }
        }
        Walk(_table);
        if (_flattener.Flat.Count > 0 && realized.Count == 0)
            problems.Add("no rows realized");
        var flatSet = _flattener.Flat.ToHashSet();
        foreach (var r in realized)
        {
            if (r.DataContext is DemoNode node && !flatSet.Contains(node))
            {
                problems.Add($"stale realized row: {node.Name}");
                break;
            }
        }

        var ok = problems.Count == 0;
        var msg = ok
            ? $"[hoster] PASS {step}: flat={_flattener.Flat.Count} realized={realized.Count}"
            : $"[hoster] FAIL {step}: {string.Join("; ", problems)}";
        Console.WriteLine(msg);
        _status.Text = msg;
        _status.Foreground = new SolidColorBrush(ok ? Color.FromArgb(0xFF, 0x00, 0xF1, 0x5E) : Color.FromArgb(0xFF, 0xFF, 0x6B, 0x6B));
        return ok;
    }

    // ── the scripted self-test: exercises every function ──

    private async Task RunSelfTestAsync()
    {
        var ok = true;

        BuildTree();
        _flattener.SetRoots(_roots);
        ok &= await VerifyAsync("initial (all expanded)");

        _roots[0].IsExpanded = false;
        ok &= await VerifyAsync("collapse artist1");

        _roots[0].IsExpanded = true;
        ok &= await VerifyAsync("re-expand artist1 (no duplicates)");

        for (var i = 0; i < 5; i++)
        {
            _roots[1].IsExpanded = false;
            _roots[1].IsExpanded = true;
        }
        ok &= await VerifyAsync("toggle artist2 ×5");

        var album = _roots[2].Children[0];
        album.IsExpanded = false;
        _roots[2].IsExpanded = false;
        _roots[2].IsExpanded = true;
        ok &= await VerifyAsync("nested collapse survives parent toggle");

        _roots.ForEach(r => r.IsExpanded = false);
        ok &= await VerifyAsync("collapse all roots");

        ForEachNode(n => n.IsExpanded = true);
        ok &= await VerifyAsync("expand all");

        for (var round = 0; round < 5; round++)
        {
            ToggleRandom(20);
            ok &= await VerifyAsync($"random storm {round + 1}/5");
        }

        BuildTree();
        _flattener.SetRoots(_roots);
        ok &= await VerifyAsync("rebuild roots");

        var summary = ok ? "[hoster] SELF-TEST PASS — all steps green" : "[hoster] SELF-TEST FAIL — see steps above";
        Console.WriteLine(summary);
        _status.Text = summary;
        _status.Foreground = new SolidColorBrush(ok ? Color.FromArgb(0xFF, 0x00, 0xF1, 0x5E) : Color.FromArgb(0xFF, 0xFF, 0x6B, 0x6B));
    }
}
