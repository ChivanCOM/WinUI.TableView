using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WinUI.TableView;

namespace TreeGrid.HosterApp;

/// <summary>
/// The virtualized sibling of <see cref="HosterView"/>: hosts a
/// <see cref="VirtualTreeItemsSource"/> over a <see cref="VirtualTreeModel"/> with an
/// async, latency-simulating leaf backend — the exact stack Grooves' library grid runs.
///
/// On top of the flattener view's checks, the self-test asserts LAYOUT: after every
/// operation the realized containers must be CONTIGUOUS (each row exactly RowHeight
/// below the previous) and aligned to the scroll offset (no blank band where a
/// collapsed subtree used to be), and every visible placeholder must resolve to a real
/// row. This is the check that catches "disappearing rows on collapse".
/// </summary>
public sealed partial class VirtualHosterView : Grid
{
    private const int Artists = 30;
    private const int AlbumsPerArtist = 5;
    private const int TracksPerAlbum = 40;
    private const int PageSize = 50;
    private const double RowHeight = 30;

    private readonly TableView _table;
    private readonly TextBlock _status;
    private ScrollViewer? _scrollViewer;

    private List<DemoNode> _roots = new();
    private VirtualTreeModel _model = null!;
    private VirtualTreeItemsSource _source = null!;
    private readonly Dictionary<object, DemoNode> _placeholders = new();

    /// <summary>Simulated store latency per page query.</summary>
    private int _latencyMs = 80;

    /// <summary>How many columns the grid shows — <c>--columns N</c>, default 3.</summary>
    private static readonly int ExtraColumns = ReadColumnCount();

    private static int ReadColumnCount()
    {
        var args = Environment.GetCommandLineArgs();
        var at = Array.IndexOf(args, "--columns");
        return at >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], out var n) ? n : 3;
    }

    public VirtualHosterView()
    {
        Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x0E, 0x15, 0x20));
        Padding = new Thickness(16);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowSpacing = 10;

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        toolbar.Children.Add(MakeButton("Collapse artist 1", () => _roots[0].IsExpanded = false));
        toolbar.Children.Add(MakeButton("Expand artist 1", () => _roots[0].IsExpanded = true));
        toolbar.Children.Add(MakeButton("Collapse all", () => _roots.ForEach(r => r.IsExpanded = false)));
        toolbar.Children.Add(MakeButton("Expand all", ExpandAll));
        toolbar.Children.Add(MakeButton("Scroll middle", () => ScrollToOffset(TotalRows() * RowHeight / 2)));
        toolbar.Children.Add(MakeButton("Scroll top", () => ScrollToOffset(0)));
        toolbar.Children.Add(MakeButton("Refresh", () => _source.Refresh()));
        toolbar.Children.Add(MakeButton("Verify now", async () => await VerifyAsync("manual")));
        toolbar.Children.Add(MakeButton("Run self-test", async () => await RunSelfTestAsync()));
        SetRow(toolbar, 0);
        Children.Add(toolbar);

        _table = new TableView
        {
            AutoGenerateColumns = false,
            SelectionMode = ListViewSelectionMode.Extended,
            RowHeight = RowHeight,
            RowMaxHeight = RowHeight,
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
        // --columns N: pad out to N columns. Three columns hide everything that costs PER CELL,
        // and the grid this stack exists for (the import queue) shows nineteen. A row realizing
        // nineteen cells is where per-cell work stops being a rounding error.
        for (var i = _table.Columns.Count; i < ExtraColumns; i++)
        {
            // Every fifth one is a TEMPLATE column, because the grid this stack exists for marks
            // its rows with them (the check, the state glyph, the duplicate badge) and they take a
            // different path through cell realization than a bound column does.
            if (i % 5 == 0)
            {
                _table.Columns.Add(new TableViewTemplateColumn
                {
                    Header = $"Mark {i}",
                    Width = new GridLength(30),
                    CellTemplate = MarkTemplate(),
                });
                continue;
            }

            _table.Columns.Add(new TableViewTextColumn
            {
                Header = $"Col {i}",
                Width = new GridLength(90),
                IsReadOnly = true,
                Binding = new Microsoft.UI.Xaml.Data.Binding
                {
                    Path = new PropertyPath(i % 2 == 0 ? nameof(DemoNode.Artist) : nameof(DemoNode.Title))
                },
            });
        }

        SetRow(_table, 1);
        Children.Add(_table);

        _status = new TextBlock
        {
            Text = "virtual self-test pending…",
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            FontSize = 14,
        };
        SetRow(_status, 2);
        Children.Add(_status);

        BuildSkeletonAndModel();

        Loaded += async (_, _) =>
        {
            await Task.Delay(600);
            await RunSelfTestAsync();
        };
    }

    /// <summary>A mark cell of about the weight the import queue's are: a bordered glyph.</summary>
    private static DataTemplate MarkTemplate() => (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
        """
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
          <Border Padding="2" HorizontalAlignment="Center" VerticalAlignment="Center">
            <TextBlock Text="{Binding Glyph}" FontSize="11" />
          </Border>
        </DataTemplate>
        """);

    private static Button MakeButton(string label, Action action)
    {
        var b = new Button { Content = label };
        b.Click += (_, _) => action();
        return b;
    }

    // ── skeleton + model ──

    private void BuildSkeletonAndModel()
    {
        _roots = new List<DemoNode>();
        for (var a = 1; a <= Artists; a++)
        {
            var artist = new DemoNode($"Artist {a:00}", 0);
            _roots.Add(artist);
            for (var b = 1; b <= AlbumsPerArtist; b++)
                artist.Children.Add(new DemoNode($"Album {a:00}.{b}", 1) { LeafCount = TracksPerAlbum });
        }

        _placeholders.Clear();
        _model = new VirtualTreeModel(
            childrenOf: n => ((DemoNode)n).Children,
            leafCountOf: n => n is DemoNode d ? d.LeafCount : 0,
            fetchLeaves: FetchLeavesAsync,
            placeholderOf: PlaceholderFor,
            pageSize: PageSize);
        _model.SetRoots(_roots);
        _source = new VirtualTreeItemsSource(_model);
        _table.ItemsSource = _source;
    }

    private async Task<IReadOnlyList<object>> FetchLeavesAsync(
        object? group, int offset, int limit, CancellationToken ct)
    {
        if (_latencyMs > 0)
            await Task.Delay(_latencyMs, ct);
        var album = (DemoNode)group!;
        var rows = new List<object>(limit);
        for (var i = 0; i < limit; i++)
            rows.Add(new DemoNode($"{offset + i + 1:00} - {album.Name} Track.mp3", album.Depth + 1,
                artist: album.Name.Split('.')[0].Replace("Album", "Artist"),
                title: $"{album.Name} Track {offset + i + 1}"));
        return rows;
    }

    /// <summary>Diagnostic switch: PLACEHOLDER_MODE=unique returns a FRESH placeholder object
    /// per call instead of the shared per-group instance Grooves uses. Distinguishes "ListView
    /// chokes on many identical item objects" from other causes of stuck placeholders.</summary>
    private static readonly bool UniquePlaceholders =
        Environment.GetEnvironmentVariable("PLACEHOLDER_MODE") == "unique";

    private object PlaceholderFor(object? group)
        => new DemoNode("…", group is DemoNode g ? g.Depth + 1 : 0);   // model caches per slot

    private void ExpandAll()
    {
        void Walk(DemoNode n) { n.IsExpanded = true; n.Children.ForEach(Walk); }
        _roots.ForEach(Walk);
    }

    private int TotalRows()
    {
        var count = 0;
        void Walk(DemoNode n)
        {
            count++;
            if (!n.IsExpanded) return;
            n.Children.ForEach(Walk);
            count += n.LeafCount;
        }
        _roots.ForEach(Walk);
        return count;
    }

    private int FirstVisibleGroupIndex()
    {
        _scrollViewer ??= FindScrollViewer(_table);
        var first = _scrollViewer is { } sv ? (int)(sv.VerticalOffset / 41) : 0;
        for (var i = Math.Max(0, first); i < _model.Count; i++)
            if (_model.PeekAt(i) is DemoNode { HasChildren: true })
                return i;
        return 0;
    }

    private void ScrollToOffset(double offset)
    {
        _scrollViewer ??= FindScrollViewer(_table);
        _scrollViewer?.ChangeView(null, offset, null, true);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject d)
    {
        var n = VisualTreeHelper.GetChildrenCount(d);
        for (var i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is ScrollViewer sv) return sv;
            if (FindScrollViewer(c) is { } found) return found;
        }
        return null;
    }

    // ── verification ──

    /// <summary>Waits until no visible placeholder remains AND at least one realized row
    /// actually intersects the viewport (a broken re-anchor leaves every container above the
    /// viewport showing real content — placeholder checks alone can't see that), or times out.</summary>
    private async Task SettleAsync(int timeoutMs = 3500)
    {
        var start = Environment.TickCount;
        while (Environment.TickCount - start < timeoutMs)
        {
            await Task.Delay(120);
            if (RealizedRows().All(r => r.DataContext is DemoNode { Name: not "…" })
                && ViewportIntersected())
            {
                await Task.Delay(120);   // one extra beat for layout after the last fill
                return;
            }
        }
    }

    private bool ViewportIntersected()
    {
        _scrollViewer ??= FindScrollViewer(_table);
        if (_scrollViewer is not { } sv || _model.Count == 0)
            return true;   // nothing to show — vacuously fine

        foreach (var r in RealizedRows())
        {
            var y = r.TransformToVisual(sv).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            if (y > -r.ActualHeight && y < sv.ViewportHeight)
                return true;
        }
        return false;
    }

    private List<TableViewRow> RealizedRows()
    {
        var rows = new List<TableViewRow>();
        void Walk(DependencyObject d)
        {
            var n = VisualTreeHelper.GetChildrenCount(d);
            for (var i = 0; i < n; i++)
            {
                var c = VisualTreeHelper.GetChild(d, i);
                if (c is TableViewRow r && r.ActualHeight > 0) rows.Add(r);
                Walk(c);
            }
        }
        Walk(_table);
        return rows;
    }

    private async Task<bool> VerifyAsync(string step)
    {
        await SettleAsync();

        // Diagnostic: if placeholders survived the settle, try a 1px scroll nudge (forces the
        // panel to re-realize) and settle again. If that cures them the containers were STALE
        // (realization trigger missing); if not, the content mapping itself is broken.
        var nudgeCured = false;
        if (RealizedRows().Any(r => r.DataContext is DemoNode { Name: "…" }))
        {
            _scrollViewer ??= FindScrollViewer(_table);

            // Dump the stuck state BEFORE the nudge disturbs it: which indices the panel
            // realized, what they show, and what the model says they should show.
            if (_scrollViewer is { } svDump)
            {
                Console.WriteLine($"[hoster:virtual] STUCK DUMP {step}: offset={svDump.VerticalOffset:F0} modelCount={_model.Count}");
                foreach (var r in RealizedRows().OrderBy(r => r.Index).Take(25))
                {
                    var y = r.TransformToVisual(svDump).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
                    var shown = (r.DataContext as DemoNode)?.Name ?? r.DataContext?.ToString() ?? "<null>";
                    var expect = (_model.PeekAt(r.Index) as DemoNode)?.Name ?? "<?>";
                    Console.WriteLine($"[hoster:virtual]   idx={r.Index} y={y:F0} shown='{shown}' model='{expect}'");
                }
            }

            if (_scrollViewer is { } sv)
            {
                var y = sv.VerticalOffset;
                sv.ChangeView(null, y > 0 ? y - 1 : 1, null, true);
                await Task.Delay(60);
                sv.ChangeView(null, y, null, true);
                await SettleAsync(1200);
                nudgeCured = RealizedRows().All(r => r.DataContext is not DemoNode { Name: "…" });
            }
        }

        var problems = new List<string>();
        if (nudgeCured)
            problems.Add("stale realization: placeholders persisted until a 1px scroll nudge re-realized the panel");

        // (a) model count == reference walk of the skeleton
        var expectedCount = TotalRows();
        if (_model.Count != expectedCount)
            problems.Add($"model Count {_model.Count} != reference {expectedCount}");
        if (_table.Items.Count != expectedCount)
            problems.Add($"TableView.Items {_table.Items.Count} != reference {expectedCount}");

        _scrollViewer ??= FindScrollViewer(_table);

        // (b) realized rows: correct content per index, no leftover placeholders
        var realized = RealizedRows()
            .Where(r => r.Index >= 0)
            .OrderBy(r => r.Index)
            .ToList();
        if (expectedCount > 0 && realized.Count == 0)
            problems.Add("no rows realized");

        foreach (var row in realized)
        {
            var expected = _model.PeekAt(row.Index);
            if (row.DataContext is DemoNode { Name: "…" })
                problems.Add($"row {row.Index} still a placeholder after settle");
            else if (!ReferenceEquals(row.DataContext, expected))
            {
                problems.Add($"row {row.Index} shows '{row.DataContext}' but model has '{expected}'");
                break;
            }
        }

        // (c) LAYOUT: realized rows must be contiguous — each container exactly one row
        // pitch below the previous, no blank band where a collapsed subtree used to sit.
        // Pitch is MEASURED (row min-height + gridline make it differ from the RowHeight
        // property): the smallest positive per-index gap between consecutive realized rows.
        if (_scrollViewer is not null && realized.Count > 2)
        {
            double YOf(TableViewRow r) =>
                r.TransformToVisual(_scrollViewer).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;

            var pitch = double.MaxValue;
            for (var i = 1; i < realized.Count; i++)
            {
                var indexDelta = realized[i].Index - realized[i - 1].Index;
                var gap = YOf(realized[i]) - YOf(realized[i - 1]);
                if (indexDelta > 0 && gap > 1)
                    pitch = Math.Min(pitch, gap / indexDelta);
            }

            if (pitch is > 1 and < double.MaxValue)
            {
                for (var i = 1; i < realized.Count; i++)
                {
                    var gap = YOf(realized[i]) - YOf(realized[i - 1]);
                    var indexDelta = realized[i].Index - realized[i - 1].Index;
                    var expectedGap = indexDelta * pitch;
                    if (Math.Abs(gap - expectedGap) > 2)
                    {
                        problems.Add($"layout gap: rows {realized[i - 1].Index}→{realized[i].Index} are {gap:F0}px apart, expected {expectedGap:F0}px (pitch {pitch:F1})");
                        break;
                    }
                }

                // (d) top alignment: the first row visible at this offset sits where the offset says.
                var offset = _scrollViewer.VerticalOffset;
                var first = realized[0];
                var expectedY = first.Index * pitch - offset;
                var actualY = YOf(first);
                if (Math.Abs(actualY - expectedY) > pitch)
                    problems.Add($"blank band: first realized row {first.Index} sits at y={actualY:F0}, expected {expectedY:F0} (offset {offset:F0}, pitch {pitch:F1})");
            }
        }

        var ok = problems.Count == 0;
        var msg = ok
            ? $"[hoster:virtual] PASS {step}: rows={expectedCount} realized={realized.Count}"
            : $"[hoster:virtual] FAIL {step}: {string.Join("; ", problems)}";
        Console.WriteLine(msg);
        _status.Text = msg;
        _status.Foreground = new SolidColorBrush(ok ? Color.FromArgb(0xFF, 0x00, 0xF1, 0x5E) : Color.FromArgb(0xFF, 0xFF, 0x6B, 0x6B));
        return ok;
    }

    // ── the scripted self-test ──

    private async Task RunSelfTestAsync()
    {
        var ok = true;

        // --fling: the fast-scroll blank probe (viewport shows nothing during a fling).
        if (Environment.GetCommandLineArgs().Contains("--fling"))
        {
            await FlingProbeAsync();
            return;
        }

        // --fling-loop: hop between two far-apart high offsets forever — a stable hot loop
        // for attaching a CPU profiler to the large-scroll path.
        if (Environment.GetCommandLineArgs().Contains("--fling-loop"))
        {
            BuildSkeletonAndModel();
            await SettleAsync();
            _scrollViewer ??= FindScrollViewer(_table);
            if (_scrollViewer is { } svLoop)
            {
                var extentLoop = Math.Max(0, svLoop.ExtentHeight - svLoop.ViewportHeight);
                var flip = false;
                Console.WriteLine("[hoster:fling] loop mode — profiler attach window");
                while (true)
                {
                    svLoop.ChangeView(null, flip ? extentLoop * 0.95 : extentLoop * 0.60, null, true);
                    flip = !flip;
                    await Task.Delay(30);
                }
            }
            return;
        }

        // --repro: only the currently-failing scenario, for fast fix iteration.
        if (Environment.GetCommandLineArgs().Contains("--repro"))
        {
            // The user's exact gesture first: scrolled a little, collapse the folder at the
            // top of the viewport.
            // Offset ZERO first — the user's real position: no scroll at all, collapse an
            // expanded folder a few rows down.
            BuildSkeletonAndModel();
            ScrollToOffset(0);
            ok = await VerifyAsync("at top");
            _roots[1].IsExpanded = false;
            _table.RefreshAfterTreeToggle();
            ok &= await VerifyAsync("collapse at offset zero");

            BuildSkeletonAndModel();
            ScrollToOffset(500);
            ok &= await VerifyAsync("scroll slightly");
            var top = _model.PeekAt(FirstVisibleGroupIndex()) as DemoNode;
            if (top is { HasChildren: true })
            {
                top.IsExpanded = false;
                _table.RefreshAfterTreeToggle();
                ok &= await VerifyAsync("top collapse at small offset");
            }

            BuildSkeletonAndModel();
            ScrollToOffset(TotalRows() * RowHeight / 2);
            ok &= await VerifyAsync("scroll to middle");
            _roots[2].IsExpanded = false;
            _table.RefreshAfterTreeToggle();
            ok &= await VerifyAsync("collapse above viewport");

            // The user's exact gesture: collapse the folder that IS the top visible row.
            var topRow = (_model.PeekAt(FirstVisibleGroupIndex()) as DemoNode);
            if (topRow is { HasChildren: true })
            {
                topRow.IsExpanded = false;
                _table.RefreshAfterTreeToggle();
                ok &= await VerifyAsync("collapse top-of-viewport folder");
            }
            Finish(ok);
            return;
        }

        BuildSkeletonAndModel();
        ScrollToOffset(0);
        ok &= await VerifyAsync("initial load (all expanded)");

        // THE regression: collapse a subtree above the viewport content — the removed
        // block's space must close up, not linger as a blank band.
        _roots[0].IsExpanded = false;
        ok &= await VerifyAsync("collapse artist 1");

        _roots[0].IsExpanded = true;
        ok &= await VerifyAsync("re-expand artist 1");

        // Collapse an album mid-fetch (its page still in flight).
        var album = _roots[1].Children[0];
        album.IsExpanded = false;
        await Task.Delay(10);
        album.IsExpanded = true;
        await Task.Delay(_latencyMs / 3);
        album.IsExpanded = false;
        ok &= await VerifyAsync("album collapse mid-fetch");
        album.IsExpanded = true;
        ok &= await VerifyAsync("album re-expand (cache warm)");

        // Deep scroll, then collapse something far above the viewport.
        ScrollToOffset(TotalRows() * RowHeight / 2);
        ok &= await VerifyAsync("scroll to middle");
        _roots[2].IsExpanded = false;
        _table.RefreshAfterTreeToggle();
        ok &= await VerifyAsync("collapse above viewport");
        _roots[2].IsExpanded = true;
        _table.RefreshAfterTreeToggle();

        ScrollToOffset(0);
        _roots.ForEach(r => r.IsExpanded = false);
        ok &= await VerifyAsync("collapse all roots");

        ExpandAll();
        ok &= await VerifyAsync("expand all");

        _source.Refresh();
        ok &= await VerifyAsync("refresh (drop pages)");

        Finish(ok);
    }

    /// <summary>Probes the fast-scroll blank symptom: hops across the full extent in
    /// viewport-sized jumps (each one trips the layouter's large-scroll path, like a
    /// trackpad fling) while sampling how many realized rows intersect the viewport.
    /// Reports the longest zero-visible window and the worst UI-thread stall (a gap
    /// between samples means layout/prep blocked the thread that long).</summary>
    private async Task FlingProbeAsync()
    {
        // --fling-nolatency: zero store latency isolates the fill-burst backlog from
        // container/layout cost (growth that persists at 0ms is NOT fetch-related).
        if (Environment.GetCommandLineArgs().Contains("--fling-nolatency"))
            _latencyMs = 0;
        BuildSkeletonAndModel();
        ScrollToOffset(0);
        await SettleAsync();
        _scrollViewer ??= FindScrollViewer(_table);
        if (_scrollViewer is not { } sv)
        {
            Console.WriteLine("[hoster:fling] no ScrollViewer");
            Finish(false);
            return;
        }

        var extent = Math.Max(0, sv.ExtentHeight - sv.ViewportHeight);
        var samples = new List<(long T, int Visible, int Holes, double Offset)>();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        void Sample()
        {
            var visible = 0;
            var holes = 0;
            foreach (var r in RealizedRows())
            {
                var y = r.TransformToVisual(sv).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
                if (y > -r.ActualHeight && y < sv.ViewportHeight)
                {
                    visible++;
                    if ((r.DataContext as DemoNode)?.Name is null or "…" || (r.DataContext as DemoNode)!.Name.EndsWith(" …"))
                        holes++;
                }
            }
            samples.Add((sw.ElapsedMilliseconds, visible, holes, sv.VerticalOffset));
        }

        // Enough hops that the median means something: single hops swing two-to-one on a laptop,
        // and a fix worth keeping has to show up over the swing rather than inside it.
        var hops = Environment.GetCommandLineArgs().Contains("--long") ? 60 : 16;
        var walls = new List<long>();
        var measures = new List<long>();
        for (var h = 1; h <= hops; h++)
        {
            var t0 = sw.ElapsedMilliseconds;
            var prep0 = TableView.DiagPrepares;
            var prepT0 = TableView.DiagPrepareTicks;
            var meas0 = TableView.DiagRowMeasureTicks;
            var reads0 = VirtualTreeModel.DiagReads;
            var scans0 = VirtualTreeModel.DiagIndexOfScans;
            var cellLists0 = TableView.DiagCellListBuilds;
            var rowIdx0 = TableView.DiagRowIndexLookups;
            var alt0 = TableView.DiagAlternateSweeps;
            var ins0 = TableView.DiagInsertCellScans;
            var post0 = TableView.DiagPostPrepareTicks;
            var states0 = TableView.DiagGoToStates;
            var cm0 = TableView.DiagCellMeasureTicks;
            var cpm0 = TableView.DiagCellPreMeasureTicks;
            var cmc0 = TableView.DiagCellMeasures;
            var ra0 = TableView.DiagRowArrangeTicks;
            var rac0 = TableView.DiagRowArranges;
            sv.ChangeView(null, extent * h / (double)hops, null, true);
            Sample();
            await Task.Delay(16);
            Sample();
            var prepMs = (TableView.DiagPrepareTicks - prepT0) * 1000 / System.Diagnostics.Stopwatch.Frequency;
            var measMs = (TableView.DiagRowMeasureTicks - meas0) * 1000 / System.Diagnostics.Stopwatch.Frequency;
            // The first few hops are the panel still filling; they say nothing about scrolling.
            if (h > 4)
            {
                walls.Add(sw.ElapsedMilliseconds - t0);
                measures.Add(measMs);
            }
            var panelKids = _table.ItemsPanelRoot is { } panel ? panel.Children.Count : -1;
            Console.WriteLine($"[hoster:fling] hop {h:00} wall={sw.ElapsedMilliseconds - t0}ms "
                + $"prepares={TableView.DiagPrepares - prep0} prepMs={prepMs} rowMeasureMs={measMs} "
                + $"reads={VirtualTreeModel.DiagReads - reads0} idxScans={VirtualTreeModel.DiagIndexOfScans - scans0} "
                + $"rows={RealizedRows().Count} panelKids={panelKids} "
                + $"cellLists={TableView.DiagCellListBuilds - cellLists0} rowIdx={TableView.DiagRowIndexLookups - rowIdx0} "
                + $"altSweeps={TableView.DiagAlternateSweeps - alt0} insScans={TableView.DiagInsertCellScans - ins0} "
                + $"postPrepMs={(TableView.DiagPostPrepareTicks - post0) * 1000 / System.Diagnostics.Stopwatch.Frequency} "
                + $"goToStates={TableView.DiagGoToStates - states0} "
                + $"cellMeasures={TableView.DiagCellMeasures - cmc0} "
                + $"cellMs={(TableView.DiagCellMeasureTicks - cm0) * 1000 / System.Diagnostics.Stopwatch.Frequency} "
                + $"cellPreMs={(TableView.DiagCellPreMeasureTicks - cpm0) * 1000 / System.Diagnostics.Stopwatch.Frequency} "
                + $"rowArranges={TableView.DiagRowArranges - rac0} "
                + $"arrangeMs={(TableView.DiagRowArrangeTicks - ra0) * 1000 / System.Diagnostics.Stopwatch.Frequency}");
        }
        while (sw.ElapsedMilliseconds < 2500)
        {
            Sample();
            await Task.Delay(30);
        }

        long blankStart = -1, worstBlank = 0, blankAt = 0, lastT = 0, worstGap = 0, gapAt = 0;
        foreach (var s in samples)
        {
            if (s.T - lastT > worstGap) { worstGap = s.T - lastT; gapAt = lastT; }
            lastT = s.T;
            if (s.Visible == 0) { blankStart = blankStart < 0 ? s.T : blankStart; }
            else if (blankStart >= 0)
            {
                if (s.T - blankStart > worstBlank) { worstBlank = s.T - blankStart; blankAt = blankStart; }
                blankStart = -1;
            }
        }
        if (blankStart >= 0 && lastT - blankStart > worstBlank) { worstBlank = lastT - blankStart; blankAt = blankStart; }

        foreach (var s in samples)
            if (s.Visible == 0 || s.Holes > 0)
                Console.WriteLine($"[hoster:fling] t={s.T}ms visible={s.Visible} holes={s.Holes} offset={s.Offset:F0}");
        static long Median(List<long> xs)
        {
            if (xs.Count == 0) return 0;
            var sorted = new List<long>(xs);
            sorted.Sort();
            return sorted[sorted.Count / 2];
        }

        static long P90(List<long> xs)
        {
            if (xs.Count == 0) return 0;
            var sorted = new List<long>(xs);
            sorted.Sort();
            return sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * 0.9))];
        }

        Console.WriteLine($"[hoster:fling] SUMMARY hops={walls.Count} "
            + $"wallMedian={Median(walls)}ms wallP90={P90(walls)}ms "
            + $"rowMeasureMedian={Median(measures)}ms rowMeasureP90={P90(measures)}ms");
        Console.WriteLine($"[hoster:fling] extent={extent:F0} samples={samples.Count} worstBlank={worstBlank}ms@{blankAt}ms worstUiStall={worstGap}ms@{gapAt}ms");

        var ok = worstBlank < 200;
        Console.WriteLine(ok
            ? "[hoster:fling] PASS — viewport never blank >200ms during fling"
            : $"[hoster:fling] FAIL — viewport blank {worstBlank}ms during fling");
        Finish(ok);
    }

    private void Finish(bool ok)
    {
        var summary = ok
            ? "[hoster:virtual] SELF-TEST PASS — all steps green"
            : "[hoster:virtual] SELF-TEST FAIL — see steps above";
        Console.WriteLine(summary);
        _status.Text = summary;
        _status.Foreground = new SolidColorBrush(ok ? Color.FromArgb(0xFF, 0x00, 0xF1, 0x5E) : Color.FromArgb(0xFF, 0xFF, 0x6B, 0x6B));

        // Headless runs must never need a manual kill: exit with the verdict.
        if (Environment.GetCommandLineArgs().Contains("--autoclose"))
        {
            Console.Out.Flush();
            Environment.Exit(ok ? 0 : 1);
        }
    }
}
