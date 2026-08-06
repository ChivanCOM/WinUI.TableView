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

    /// <summary>What a realized row actually occupies, borders included — the pitch the offset
    /// maths goes by (see <see cref="FirstVisibleGroupIndex"/>).</summary>
    private const double RowPitch = 41;

    /// <summary>The import queue's row height, which is what <c>--music</c> mode measures against.</summary>
    private const double QueueRowHeight = 22;

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

            // --vcols: build a row's cells only for the columns on screen. Every probe here runs
            // both ways, because the point of the switch is that nothing else about the grid
            // changes.
            VirtualizeColumns = Environment.GetCommandLineArgs().Contains("--vcols"),
        };
        if (MusicPath is not null)
        {
            // The queue's own row height. It is not decoration: at 22px a viewport holds half
            // again as many rows as at 30, and every one of them is twenty-two cells to realize.
            _table.RowHeight = QueueRowHeight;
            _table.RowMinHeight = QueueRowHeight;
            _table.RowMaxHeight = QueueRowHeight;
            _table.FontSize = 12;
            AddQueueColumns();
        }
        else
        {
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
        }
        // --columns N: pad out to N columns. Three columns hide everything that costs PER CELL,
        // and the grid this stack exists for (the import queue) shows nineteen. A row realizing
        // nineteen cells is where per-cell work stops being a rounding error.
        for (var i = _table.Columns.Count; i < ExtraColumns && MusicPath is null; i++)
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

    /// <summary>The dump to read a real collection from — <c>--music &lt;path.tsv&gt;</c>.</summary>
    private static readonly string? MusicPath = ReadArg("--music");

    /// <summary>Cap on how much of it to load — <c>--tracks N</c>, for scaling runs.</summary>
    private static readonly int MusicLimit =
        int.TryParse(ReadArg("--tracks"), out var n) ? n : int.MaxValue;

    private static string? ReadArg(string name)
    {
        var args = Environment.GetCommandLineArgs();
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    private MusicLibrary? _music;

    /// <summary>
    /// The import queue's own columns, at its own widths: three template marks, the tree, and
    /// eighteen text columns — 2452px of them, which is why that grid is nearly always scrolled
    /// sideways and why horizontal scrolling is worth measuring at all.
    /// </summary>
    private void AddQueueColumns()
    {
        // The tick. A Viewbox around a CheckBox, as the queue has it — the heaviest cell in the row.
        _table.Columns.Add(new TableViewTemplateColumn
        {
            Header = "",
            Width = new GridLength(30),
            CellTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                """
                <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                  <Viewbox Width="14" Height="14" HorizontalAlignment="Center" VerticalAlignment="Center">
                    <CheckBox IsChecked="{Binding IsChecked, Mode=TwoWay}"
                              MinWidth="0" MinHeight="0" Width="20" Height="20" Padding="0" Margin="0"/>
                  </Viewbox>
                </DataTemplate>
                """),
        });

        // The details button, as the queue has it: a Button per row, which carries a whole control
        // template and its visual states, not a glyph in a Border.
        _table.Columns.Add(new TableViewTemplateColumn
        {
            Header = "",
            Width = new GridLength(26),
            CellTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                """
                <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                  <Button Background="Transparent" BorderThickness="0" Padding="2" MinWidth="0" MinHeight="0"
                          HorizontalAlignment="Center" VerticalAlignment="Center"
                          ToolTipService.ToolTip="Details">
                    <TextBlock Text="&#xf044;" FontSize="10" Foreground="#CCFFFFFF"
                               FontFamily="ms-appx:///Assets/Fonts/fa-solid-900.ttf#Font Awesome 5 Pro Solid"/>
                  </Button>
                </DataTemplate>
                """),
        });

        // The duplicate badge: its own glyph, its own colour, its own tooltip — the mark that says
        // the library already holds this track, which a re-imported folder wears on many rows.
        _table.Columns.Add(new TableViewTemplateColumn
        {
            Header = "",
            Width = new GridLength(26),
            CellTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                """
                <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                  <TextBlock Text="{Binding DuplicateGlyph}" FontSize="10.5"
                             Foreground="{Binding DuplicateBrush}"
                             HorizontalAlignment="Center" VerticalAlignment="Center"
                             ToolTipService.ToolTip="{Binding DuplicateTip}"
                             FontFamily="ms-appx:///Assets/Fonts/fa-solid-900.ttf#Font Awesome 5 Pro Solid"/>
                </DataTemplate>
                """),
        });

        _table.Columns.Add(new TableViewTreeColumn
        {
            Header = "Name",
            Width = new GridLength(260),
            Binding = new Microsoft.UI.Xaml.Data.Binding { Path = new PropertyPath(nameof(DemoNode.Name)) },
            // The state mark rides in the tree column's glyph slot, as the queue has it: the glyph,
            // its colour and its tooltip are three bindings read on every row realized.
            GlyphBinding = new Microsoft.UI.Xaml.Data.Binding { Path = new PropertyPath(nameof(DemoNode.StateGlyph)) },
            GlyphForegroundBinding = new Microsoft.UI.Xaml.Data.Binding { Path = new PropertyPath(nameof(DemoNode.StateBrush)) },
            GlyphToolTipBinding = new Microsoft.UI.Xaml.Data.Binding { Path = new PropertyPath(nameof(DemoNode.StateTip)) },
            GlyphFontFamily = new FontFamily("ms-appx:///Assets/Fonts/fa-solid-900.ttf#Font Awesome 5 Pro Solid"),
            // The playing row swaps its glyph for a template, and the binding that decides it is
            // read on every realized row whether one is playing or not.
            GlyphOverrideBinding = new Microsoft.UI.Xaml.Data.Binding { Path = new PropertyPath(nameof(DemoNode.IsChecked)) },
            GlyphOverrideTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                """
                <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                  <TextBlock Text="&#x25B6;" FontSize="9" VerticalAlignment="Center"/>
                </DataTemplate>
                """),
        });

        // --maxcolumns N: keep only the first N of the queue's columns. A pane shows six of the
        // twenty-two, and this is how the cost of realizing the sixteen nobody can see is measured.
        var maxColumns = int.TryParse(ReadArg("--maxcolumns"), out var mc) ? mc : int.MaxValue;

        foreach (var (header, width, path) in new (string, int, string)[]
        {
            ("Folder", 180, nameof(DemoNode.ColFolder)),
            ("Title", 190, nameof(DemoNode.ColTitle)),
            ("Artist", 150, nameof(DemoNode.ColArtist)),
            ("Album", 150, nameof(DemoNode.ColAlbum)),
            ("Album artist", 150, nameof(DemoNode.ColAlbumArtist)),
            ("#", 56, nameof(DemoNode.ColTrack)),
            ("Disc", 48, nameof(DemoNode.ColDisc)),
            ("Disc title", 130, nameof(DemoNode.ColDiscTitle)),
            ("Year", 64, nameof(DemoNode.ColYear)),
            ("Genre", 120, nameof(DemoNode.ColGenre)),
            ("Length", 70, nameof(DemoNode.ColLength)),
            ("Kind", 70, nameof(DemoNode.ColKind)),
            ("Codec", 120, nameof(DemoNode.ColCodec)),
            ("Bitrate", 86, nameof(DemoNode.ColBitrate)),
            ("Sample rate", 90, nameof(DemoNode.ColSampleRate)),
            ("Bit depth", 76, nameof(DemoNode.ColBitDepth)),
            ("Channels", 80, nameof(DemoNode.ColChannels)),
            ("Size", 80, nameof(DemoNode.ColSize)),
            ("File", 200, nameof(DemoNode.ColFile)),
        })
        {
            if (_table.Columns.Count >= maxColumns)
            {
                break;
            }

            _table.Columns.Add(new TableViewTextColumn
            {
                Header = header,
                Width = new GridLength(width),
                IsReadOnly = true,
                Binding = new Microsoft.UI.Xaml.Data.Binding { Path = new PropertyPath(path) },
            });
        }
    }

    /// <summary>
    /// Are the cells actually SHOWING anything?
    ///
    /// <para>Every other check in this harness asks whether the right rows are realized in the
    /// right places. None of them looks inside a cell, so a grid whose cells are all present,
    /// correctly sized and completely empty passes the lot. This walks the realized rows at each
    /// of several horizontal offsets — starting at nought, which is where a grid opens and where
    /// the cells were found blank — and counts the ones whose content is missing or has no size.
    /// </para>
    /// </summary>
    private async Task PaintProbeAsync()
    {
        await SettleAsync();
        _scrollViewer ??= FindScrollViewer(_table);

        var failures = new List<string>();
        var inkByOffset = new Dictionary<double, int>();

        foreach (var offset in new double[] { 0, 4, 40, 400, 0 })
        {
            // Sideways scrolling in this grid is not a ScrollViewer moving: the horizontal scroll
            // bar is bound two-way to TableView.HorizontalOffset, and every row's arrange reads
            // that to place its cells. Setting it is what a drag of that bar does.
            _table.SetValue(TableView.HorizontalOffsetProperty, offset);
            _table.UpdateLayout();
            await Task.Delay(150);

            // What actually reached the screen. Asking the elements whether they are visible and
            // sized is asking the wrong witness — a cell can answer yes to all of it and still
            // paint nothing. Rendering the grid and counting the pixels that are not the
            // background is the only answer that cannot be argued with.
            var ink = await InkPerRowAsync();
            if (ink is not null)
            {
                var median = ink.OrderBy(v => v).ElementAt(ink.Count / 2);
                inkByOffset[offset] = median;
                Console.WriteLine($"[hoster:paint] ink offset={offset:F0}: bands={ink.Count} "
                    + $"empty={ink.Count(v => v == 0)} min={ink.Min()} median={median}");
            }

            var rows = RealizedRows();
            var blankCells = 0;
            var blankRows = 0;
            var checkedCells = 0;

            foreach (var row in rows)
            {
                if (row.DataContext is not DemoNode node || node.Name is "…")
                {
                    continue;   // a placeholder is meant to be empty
                }

                var blankHere = 0;
                foreach (var cell in row.Cells)
                {
                    // A cell paints nothing when its content is gone, hidden, or has no size.
                    var presenter = FindContentPresenter(cell);
                    var hidden = presenter is null || presenter.Visibility != Visibility.Visible;
                    var empty = cell.Content is not FrameworkElement { ActualWidth: > 0, ActualHeight: > 0 };

                    checkedCells++;
                    if (hidden || empty)
                    {
                        blankHere++;
                    }
                }

                blankCells += blankHere;
                if (blankHere == row.Cells.Count && row.Cells.Count > 0)
                {
                    blankRows++;
                }
            }

            var line = $"[hoster:paint] asked={offset,4:F0} actual={_table.HorizontalOffset:F0} "
                + $"scrollable={HorizontalScrollViewer()?.ScrollableWidth ?? -1:F0} "
                + $"rows={rows.Count} cells={checkedCells} "
                + $"blankCells={blankCells} fullyBlankRows={blankRows}";
            Console.WriteLine(line);

            if (blankRows > 0)
            {
                failures.Add($"offset {offset:F0}: {blankRows} rows painted nothing at all");
            }
        }

        // The check that matters, and the one the first version of this probe was too weak to make:
        // scrolling sideways moves the columns, it does not change how much there is to draw. So
        // every offset should paint about as much as the best of them. Half is not a rounding
        // error — half is the grid at rest showing empty cells and filling them in the moment you
        // nudge it, which is exactly what a stale clip did.
        if (inkByOffset.Count > 1)
        {
            var best = inkByOffset.Values.Max();
            foreach (var (offset, painted) in inkByOffset)
            {
                if (painted < best * 0.6)
                {
                    failures.Add($"offset {offset:F0} painted {painted} where the best offset painted {best}");
                }
            }
        }

        var ok = failures.Count == 0;
        Console.WriteLine(ok
            ? "[hoster:paint] PASS — every realized row painted its cells at every offset"
            : $"[hoster:paint] FAIL — {string.Join("; ", failures)}");
        Finish(ok);
    }

    /// <summary>
    /// Types a search term one character at a time, rebuilding the skeleton from the matching
    /// subset each time — what the import grid's search box does — and times how long the UI thread
    /// is blocked by each keystroke.
    ///
    /// <para>The store side of this is measured elsewhere and is not the question. The question is
    /// what handing the model a new set of roots costs, because that is a Reset: every page dropped,
    /// every segment rebuilt, every container recycled and re-realized, and whatever the panel does
    /// afterwards to find its place again.</para>
    /// </summary>
    private async Task FilterProbeAsync()
    {
        if (_music is null)
        {
            Console.WriteLine("[hoster:filter] needs --music");
            Finish(false);
            return;
        }

        await SettleAsync();

        // Filtering from the top is the easy case, and not the one anybody does: the search box is
        // reached for after scrolling around looking for something. Scrolled down, the rebind has a
        // scroll position it feels obliged to restore — into a list that no longer contains what was
        // being looked at.
        _scrollViewer ??= FindScrollViewer(_table);
        if (_scrollViewer is { } sv)
        {
            sv.ChangeView(null, Math.Max(0, sv.ExtentHeight - sv.ViewportHeight) / 2, null, true);
            await Task.Delay(200);
            await SettleAsync();
            Console.WriteLine($"[hoster:filter] typing from offset {sv.VerticalOffset:F0}");
        }

        var worst = 0L;
        var term = "";

        foreach (var ch in "beatles")
        {
            term += ch;

            // The filtered skeleton, built the way the queue builds it: only groups with a match.
            var t0 = System.Diagnostics.Stopwatch.StartNew();
            var roots = FilteredRoots(term);
            var buildMs = t0.ElapsedMilliseconds;

            // The UI-thread half — the only half the user feels.
            var prepares0 = TableView.DiagPrepares;
            var t1 = System.Diagnostics.Stopwatch.StartNew();
            _model.SetRoots(roots);
            var setRootsMs = t1.ElapsedMilliseconds;
            _table.UpdateLayout();
            var blockMs = t1.ElapsedMilliseconds;

            await Task.Delay(60);

            var settle = System.Diagnostics.Stopwatch.StartNew();
            await SettleAsync();
            worst = Math.Max(worst, blockMs);

            Console.WriteLine($"[hoster:filter] \"{term}\" groups={roots.Count} rows={_source.Count} "
                + $"buildMs={buildMs} setRootsMs={setRootsMs} layoutMs={blockMs - setRootsMs} "
                + $"uiBlockMs={blockMs} settleMs={settle.ElapsedMilliseconds} "
                + $"prepares={TableView.DiagPrepares - prepares0}");
        }

        // And clearing it again, which is the widest reset of all — back to everything.
        var t2 = System.Diagnostics.Stopwatch.StartNew();
        _model.SetRoots(_rootsAll);
        _table.UpdateLayout();
        Console.WriteLine($"[hoster:filter] cleared rows={_source.Count} uiBlockMs={t2.ElapsedMilliseconds}");
        worst = Math.Max(worst, t2.ElapsedMilliseconds);

        var ok = worst < 150;
        Console.WriteLine(ok
            ? $"[hoster:filter] PASS — worst keystroke blocked the UI for {worst}ms"
            : $"[hoster:filter] FAIL — a keystroke blocked the UI for {worst}ms");
        Finish(ok);
    }

    /// <summary>
    /// A rebuild that changes nothing about what the list holds — the shape of a metadata edit,
    /// which rebuilds the skeleton from the store and so hands back all-new objects for the same
    /// rows — must leave the reader where they were.
    ///
    /// <para>The Reset sends the offset to the top, because a layout pass against an offset
    /// belonging to the list that was just replaced is what freezes the interface on a keystroke.
    /// The place is remembered across that, and restored from the rows the viewport was showing.
    /// This is the check that it comes back.</para>
    /// </summary>
    private async Task ReanchorProbeAsync()
    {
        if (_music is null)
        {
            Console.WriteLine("[hoster:reanchor] needs --music");
            Finish(false);
            return;
        }

        await SettleAsync();
        _scrollViewer ??= FindScrollViewer(_table);
        if (_scrollViewer is not { } sv)
        {
            Console.WriteLine("[hoster:reanchor] no ScrollViewer");
            Finish(false);
            return;
        }

        // A hundred rows down, not half a collection down: the reader scrolled to something and
        // is looking at it. (A deep jump lands on a viewport that has not been realized yet —
        // there is nothing on screen to anchor to, and what that costs is what --fling measures.)
        sv.ChangeView(null, Math.Min(RowPitch * 100, Math.Max(0, sv.ExtentHeight - sv.ViewportHeight)), null, true);
        await Task.Delay(200);
        await SettleAsync();

        var before = sv.VerticalOffset;
        var topBefore = TopVisibleName();
        Console.WriteLine($"[hoster:reanchor] parked at {before:F0} with {RealizedRows().Count} rows realized");

        _model.SetRoots(FilteredRoots(""));   // the same rows, as new objects

        // The restore is driven by container prepares after the re-point, so it lands over the
        // next few frames rather than in this one.
        for (var i = 0; i < 25 && Math.Abs(sv.VerticalOffset - before) > RowPitch * 2; i++)
        {
            await Task.Delay(100);
        }
        await SettleAsync();

        var after = sv.VerticalOffset;
        var drift = Math.Abs(after - before);
        var ok = drift <= RowPitch * 2;

        Console.WriteLine($"[hoster:reanchor] before={before:F0} after={after:F0} drift={drift:F0} "
            + $"top=\"{topBefore}\" -> \"{TopVisibleName()}\"");
        Console.WriteLine(ok
            ? "[hoster:reanchor] PASS — the rebuild kept the reader's place"
            : $"[hoster:reanchor] FAIL — the rebuild moved the reader {drift:F0}px");
        Finish(ok);
    }

    /// <summary>The name of the row at the top of the viewport, or null where a page has not
    /// landed yet.</summary>
    private string? TopVisibleName()
    {
        _scrollViewer ??= FindScrollViewer(_table);
        var index = _scrollViewer is { } sv ? (int)(sv.VerticalOffset / RowPitch) : 0;
        return index >= 0 && index < _model.Count && _model.PeekAt(index) is DemoNode node
            ? node.Name
            : null;
    }

    private List<DemoNode> _rootsAll = new();

    /// <summary>The skeleton for a search term: artists and albums that still hold a matching track,
    /// with the leaf counts narrowed to the matches — the shape QueueGroups(search) returns.</summary>
    private List<DemoNode> FilteredRoots(string term)
    {
        var roots = new List<DemoNode>();
        _musicLeaves.Clear();

        foreach (var (artist, albums) in _music!.Artists)
        {
            DemoNode? artistNode = null;

            foreach (var (album, tracks) in albums)
            {
                var hits = tracks.Where(t => Matches(t, term)).ToArray();
                if (hits.Length == 0)
                {
                    continue;
                }

                artistNode ??= new DemoNode(artist, 0) { GroupArtist = artist };
                var albumNode = new DemoNode(album, 1)
                {
                    LeafCount = hits.Length,
                    GroupArtist = artist,
                    GroupAlbum = album,
                    Parent = artistNode,
                };
                artistNode.Children.Add(albumNode);
                _musicLeaves[albumNode] = hits;
            }

            if (artistNode is not null)
            {
                roots.Add(artistNode);
            }
        }

        return roots;
    }

    private static bool Matches(MusicTrack t, string term) =>
        t.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase)
        || t.Artist.Contains(term, StringComparison.OrdinalIgnoreCase)
        || t.Album.Contains(term, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Renders the grid and counts, for each row-height band, how many pixels differ from the most
    /// common colour in that band. A band that draws text and glyphs has thousands; a band that
    /// draws only its own background has none.
    /// </summary>
    private async Task<List<int>?> InkPerRowAsync()
    {
        var bitmap = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
        try
        {
            await bitmap.RenderAsync(_table);
        }
        catch (Exception e)
        {
            Console.WriteLine($"[hoster:paint] could not render: {e.GetType().Name} {e.Message}");
            return null;
        }

        var buffer = await bitmap.GetPixelsAsync();
        var pixels = new byte[buffer.Length];
        using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer))
        {
            reader.ReadBytes(pixels);
        }

        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        if (width <= 0 || height <= 0 || pixels.Length < width * height * 4)
        {
            Console.WriteLine($"[hoster:paint] empty render {width}x{height} ({pixels.Length} bytes)");
            return null;
        }

        SavePng(pixels, width, height);

        // The bitmap is in physical pixels; the rows are in logical ones.
        var scale = height / Math.Max(1.0, _table.ActualHeight);
        var band = Math.Max(1, (int)Math.Round(RowHeight * scale));
        var headerRows = 2;   // skip the header strip, which always paints

        var ink = new List<int>();
        for (var top = band * headerRows; top + band <= height; top += band)
        {
            var counts = new Dictionary<uint, int>();
            for (var y = top; y < top + band; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var i = (y * width + x) * 4;
                    var argb = (uint)(pixels[i] | (pixels[i + 1] << 8) | (pixels[i + 2] << 16) | (pixels[i + 3] << 24));
                    counts[argb] = counts.TryGetValue(argb, out var n) ? n + 1 : 1;
                }
            }

            var total = band * width;
            var background = counts.Values.Max();
            ink.Add(total - background);
        }

        return ink;
    }

    private void SavePng(byte[] bgra, int width, int height)
    {
        if (ReadArg("--png") is not { } stem)
        {
            return;
        }

        var path = stem.Replace(".png", $"-{_pngSequence++}.png");

        // Minimal PNG writer: a single IDAT of stored-deflate scanlines. Enough to look at.
        using var stream = System.IO.File.Create(path);
        using var writer = new System.IO.BinaryWriter(stream);

        void BE(int v) => writer.Write(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });
        void Chunk(string type, byte[] data)
        {
            BE(data.Length);
            var body = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            writer.Write(body);
            BE(unchecked((int)Crc32(body)));
        }

        writer.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        Chunk("IHDR", [.. BeBytes(width), .. BeBytes(height), 8, 6, 0, 0, 0]);

        var raw = new List<byte>((width * 4 + 1) * height);
        for (var y = 0; y < height; y++)
        {
            raw.Add(0);
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                raw.AddRange([bgra[i + 2], bgra[i + 1], bgra[i], bgra[i + 3]]);   // BGRA → RGBA
            }
        }

        using var deflated = new System.IO.MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(deflated, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(raw.ToArray());
        }

        Chunk("IDAT", deflated.ToArray());
        Chunk("IEND", []);
        Console.WriteLine($"[hoster:paint] wrote {path} ({width}x{height})");

        static byte[] BeBytes(int v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
    }

    private int _pngSequence;

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }
        return crc ^ 0xFFFFFFFFu;
    }

    /// <summary>The cell's own "Content" presenter — the one its measure collapses when it decides
    /// there is no room, which is how a cell ends up present and empty.</summary>
    private static ContentPresenter? FindContentPresenter(DependencyObject root)
    {
        var n = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ContentPresenter { Name: "Content" } found)
            {
                return found;
            }

            if (FindContentPresenter(child) is { } deeper)
            {
                return deeper;
            }
        }
        return null;
    }

    /// <summary>The scroll viewer that carries the columns sideways.</summary>
    private ScrollViewer? HorizontalScrollViewer()
    {
        ScrollViewer? found = null;
        void Walk(DependencyObject d)
        {
            var n = VisualTreeHelper.GetChildrenCount(d);
            for (var i = 0; i < n && found is null; i++)
            {
                var c = VisualTreeHelper.GetChild(d, i);
                if (c is ScrollViewer sv && sv.ScrollableWidth > 0)
                {
                    found = sv;
                    return;
                }
                Walk(c);
            }
        }
        Walk(_table);
        return found;
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

    /// <summary>
    /// The real collection's skeleton: one node per artist, one per album under it, and the tracks
    /// left as virtual leaves the model pages in — the same shape QueueTreeSource builds, so the
    /// group sizes the model has to cope with are the real ones (one artist owning a thousand
    /// tracks, eight hundred owning one, a quarter of everything in a single Unknown-album bucket).
    /// </summary>
    private void BuildMusicSkeleton()
    {
        var t0 = System.Diagnostics.Stopwatch.StartNew();
        _music = MusicLibrary.Load(MusicPath!, MusicLimit);
        _musicLeaves.Clear();
        _roots = new List<DemoNode>();

        foreach (var (artist, albums) in _music.Artists)
        {
            var artistNode = new DemoNode(artist, 0) { GroupArtist = artist };
            _roots.Add(artistNode);

            foreach (var (album, tracks) in albums)
            {
                var albumNode = new DemoNode(album, 1)
                {
                    LeafCount = tracks.Length,
                    GroupArtist = artist,
                    GroupAlbum = album,
                    Parent = artistNode,
                };
                artistNode.Children.Add(albumNode);
                _musicLeaves[albumNode] = tracks;
            }
        }

        // groupKeyOf / anchorOf are what let the grid answer "where is the row I was looking at"
        // after the skeleton is rebuilt — the queue source wires them the same way, and without
        // them a rebuild has nothing to re-anchor to and starts again at the top.
        _model = new VirtualTreeModel(
            childrenOf: n => ((DemoNode)n).Children,
            leafCountOf: n => n is DemoNode d ? d.LeafCount : 0,
            fetchLeaves: FetchMusicLeavesAsync,
            placeholderOf: PlaceholderFor,
            groupKeyOf: n => n is DemoNode { Track: null, GroupArtist: not null } g
                ? GroupKeyOf(g)
                : null,
            anchorOf: n => n is DemoNode { Track: not null, LeafOffset: var offset,
                                           Parent: { GroupAlbum: not null } album }
                ? (GroupKeyOf(album), offset)
                : null,
            pageSize: PageSize);
        _model.SetRoots(_roots);
        _rootsAll = _roots;
        _source = new VirtualTreeItemsSource(_model);
        _table.ItemsSource = _source;

        Console.WriteLine($"[hoster:music] {_music.TrackCount} tracks, {_roots.Count} artists, "
            + $"{_music.AlbumCount} albums, loaded in {t0.ElapsedMilliseconds}ms");
    }

    // A group's identity across rebuilds, keyed the way the store groups: the LEVEL in front, so
    // an artist and its own unknown-album child cannot key the same, and a unit separator between
    // the parts, so an artist called "X/Y" cannot collide with an album.
    private static string GroupKeyOf(DemoNode group)
        => group.GroupAlbum is { } album
            ? "b\u001f" + group.GroupArtist + "\u001f" + album
            : "a\u001f" + group.GroupArtist;

    private readonly Dictionary<DemoNode, MusicTrack[]> _musicLeaves = new();

    private async Task<IReadOnlyList<object>> FetchMusicLeavesAsync(
        object? group, int offset, int limit, CancellationToken ct)
    {
        if (_latencyMs > 0)
            await Task.Delay(_latencyMs, ct);

        if (group is not DemoNode album || !_musicLeaves.TryGetValue(album, out var tracks))
            return [];

        var take = Math.Max(0, Math.Min(limit, tracks.Length - offset));
        var rows = new List<object>(take);
        for (var i = 0; i < take; i++)
        {
            var track = tracks[offset + i];
            rows.Add(new DemoNode(track.DisplayName, album.Depth + 1)
            {
                Track = track,
                Parent = album,
                LeafOffset = offset + i,
            });
        }
        return rows;
    }

    private void BuildSkeletonAndModel()
    {
        if (MusicPath is not null)
        {
            BuildMusicSkeleton();
            return;
        }

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

        // --filter: typing in the grid's search box. Each keystroke narrows the collection and hands
        // the model a new skeleton, which is a Reset — the single most expensive thing that can
        // happen to a virtualized list, and it happens once per character.
        if (Environment.GetCommandLineArgs().Contains("--filter"))
        {
            await FilterProbeAsync();
            return;
        }

        // --hscroll: the right value under the right header, at every horizontal position.
        if (Environment.GetCommandLineArgs().Contains("--hscroll"))
        {
            await HScrollProbeAsync();
            return;
        }

        // --flick: the trackpad gesture itself — a decaying burst of small scrolls — through the
        // grid's own wheel path, with --cap N rows/frame to measure the scroll cap.
        if (Environment.GetCommandLineArgs().Contains("--flick"))
        {
            await FlickProbeAsync();
            return;
        }

        // --wheel: scrolling at the speed a hand scrolls, which is the speed that hits the wall.
        if (Environment.GetCommandLineArgs().Contains("--wheel"))
        {
            await WheelProbeAsync();
            return;
        }

        // --reanchor: a rebuild that changes nothing about what the list holds — a metadata edit —
        // must leave the reader where they were, across the offset clamp the Reset does.
        if (Environment.GetCommandLineArgs().Contains("--reanchor"))
        {
            await ReanchorProbeAsync();
            return;
        }

        // --paint: are the cells actually SHOWING anything? Every other check here asks whether
        // the right rows are realized in the right places; none of them looks inside a cell. A
        // grid whose cells are all present, correctly sized and empty passes all of them.
        if (Environment.GetCommandLineArgs().Contains("--paint"))
        {
            await PaintProbeAsync();
            return;
        }

        // --hdrag: the horizontal scrollbar under a thumb. Sideways scrolling in this grid is not a
        // ScrollViewer moving — it is HorizontalOffset changing and every realized row re-arranging
        // its cells against it — so it has its own cost, and its own probe.
        if (Environment.GetCommandLineArgs().Contains("--hdrag"))
        {
            await HDragProbeAsync();
            return;
        }

        // --resize: dragging a column's edge. The reported symptom is that the header strip comes
        // out scrambled and only sorts itself out on a SECOND resize, which is the signature of a
        // strip laid out against widths one pass out of date.
        if (Environment.GetCommandLineArgs().Contains("--resize"))
        {
            await ResizeProbeAsync();
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
    /// <summary>
    /// Scrolling at the speed a hand actually scrolls: a wheel notch or a trackpad glide, forty
    /// pixels at a time, sixty times a second — not the viewport-sized hops <c>--fling</c> makes.
    ///
    /// <para>Those hops trip the layouter's large-scroll path, which throws everything away and
    /// re-seeds from the average row height; it is cheap and it is not what a hand does. A hand
    /// walks the list, and every row it walks past has to be realized: a container, its cells, its
    /// bindings, its measure. This is the probe for the wall a reader hits twenty rows in.</para>
    /// </summary>
    /// <summary>
    /// A trackpad flick: not one scroll but a burst of small ones, arriving every frame and
    /// decaying as the momentum runs out — which is the gesture that makes the layouter walk the
    /// list a row at a time instead of jumping. Drives the grid's own wheel path so the scroll cap
    /// (<see cref="TableView.MaxScrollRowsPerFrame"/>) is measured exactly as a hand would meet it.
    /// </summary>
    /// <summary>
    /// The check column virtualization has to survive: at every horizontal position, is the value
    /// under a header the value that BELONGS under it?
    ///
    /// <para>Realizing only the columns on screen means a row's cells no longer run 0..N in step
    /// with the columns, so every way of finding "the cell for this column" that quietly assumed
    /// they did is now a wrong-data bug — the worst kind here, because the grid still looks right.
    /// This walks right across the strip and back, twice, and reads every realized cell's text
    /// against what its own column says that row should show.</para>
    /// </summary>
    private async Task HScrollProbeAsync()
    {
        if (_music is null)
        {
            Console.WriteLine("[hoster:hscroll] needs --music");
            Finish(false);
            return;
        }

        await SettleAsync();
        _scrollViewer ??= FindScrollViewer(_table);
        if (_scrollViewer is not { } sv)
        {
            Console.WriteLine("[hoster:hscroll] no ScrollViewer");
            Finish(false);
            return;
        }

        var failures = new List<string>();
        var checkedCells = 0;
        var offsets = new double[] { 0, 200, 700, 1500, 2400, 900, 0, 1500, 0 };

        // Down the list as well as across it: a cell realized at one vertical position and recycled
        // to another is where a stale column mapping shows up.
        foreach (var top in new double[] { 0, 3000 })
        {
            sv.ChangeView(null, top, null, disableAnimation: true);
            await SettleAsync();

            foreach (var x in offsets)
            {
                sv.ChangeView(x, null, null, disableAnimation: true);
                _table.UpdateLayout();
                await Task.Delay(120);
                await SettleAsync();

                foreach (var row in RealizedRows())
                {
                    if (row.DataContext is not DemoNode node || node.Name is "…")
                    {
                        continue;   // a placeholder owes nothing
                    }

                    foreach (var cell in row.Cells)
                    {
                        if (cell.Column is not TableViewTextColumn column
                            || cell.Content is not TextBlock text)
                        {
                            continue;   // template and tree columns are checked by --paint
                        }

                        var expected = ExpectedFor(node, column.Header?.ToString() ?? "");
                        if (expected is null)
                        {
                            continue;
                        }

                        checkedCells++;
                        if (text.Text != expected)
                        {
                            failures.Add($"top={top:F0} x={x:F0} \"{column.Header}\" showed "
                                + $"\"{text.Text}\" for a row whose value is \"{expected}\"");
                        }
                    }
                }
            }
        }

        Console.WriteLine($"[hoster:hscroll] checked {checkedCells} cells across "
            + $"{offsets.Length * 2} positions, {failures.Count} wrong");
        foreach (var f in failures.Take(8))
        {
            Console.WriteLine($"[hoster:hscroll] WRONG {f}");
        }

        var ok = failures.Count == 0 && checkedCells > 0;
        Console.WriteLine(ok
            ? $"[hoster:hscroll] PASS — every realized cell showed its own column's value"
            : $"[hoster:hscroll] FAIL — {failures.Count} cells showed another column's value "
                + $"(or nothing was checked: {checkedCells})");
        Finish(ok);
    }

    /// <summary>What the row should be showing under a given header, or null for a header this
    /// check does not know about.</summary>
    private static string? ExpectedFor(DemoNode node, string header) => header switch
    {
        "Folder" => node.ColFolder,
        "Title" => node.ColTitle,
        "Artist" => node.ColArtist,
        "Album" => node.ColAlbum,
        "Album artist" => node.ColAlbumArtist,
        "#" => node.ColTrack,
        "Disc" => node.ColDisc,
        "Disc title" => node.ColDiscTitle,
        "Year" => node.ColYear,
        "Genre" => node.ColGenre,
        "Length" => node.ColLength,
        "Kind" => node.ColKind,
        "Codec" => node.ColCodec,
        "Bitrate" => node.ColBitrate,
        "Sample rate" => node.ColSampleRate,
        "Bit depth" => node.ColBitDepth,
        "Channels" => node.ColChannels,
        "Size" => node.ColSize,
        "File" => node.ColFile,
        _ => null,
    };

    /// <summary>
    /// A column being resized, and the header strip that has to follow it.
    ///
    /// <para>A drag is not one width change, it is one per pointer move, so this sets the width the
    /// way the gripper does — repeatedly, then once more to a final value — and then asks the only
    /// question that matters: does every header sit at the sum of the widths before it, and does
    /// every cell sit under its own header. The "resize again and it fixes itself" part of the
    /// report is why the check runs after the FIRST drag, before anything else touches layout.</para>
    /// </summary>
    /// <summary>
    /// Dragging the horizontal scrollbar from one end of the columns to the other and back.
    ///
    /// <para>A thumb drag delivers a stream of small offset changes, the same way a trackpad
    /// delivers a fling — and each one re-arranges every realized row. Under column virtualization
    /// some of them also cross a column boundary, which is the step that costs: that is where rows
    /// have to build cells they did not have. This measures both, and says how many of the steps
    /// were boundary crossings, so a slow drag can be blamed on the right one.</para>
    /// </summary>
    private async Task HDragProbeAsync()
    {
        BuildSkeletonAndModel();
        ScrollToOffset(0);
        await SettleAsync();
        _scrollViewer ??= FindScrollViewer(_table);

        var notch = double.TryParse(ReadArg("--notch"), out var n) ? n : 24;
        var span = _scrollViewer?.ScrollableWidth ?? 0;
        if (span <= 0)
        {
            // The extent is the row width; if the grid is wider than its columns there is nothing
            // to drag.
            span = Math.Max(0, _table.Columns.VisibleColumns.Sum(c => c.ActualWidth) - _table.ActualWidth);
        }

        if (span <= 0)
        {
            Console.WriteLine("[hoster:hdrag] nothing to scroll sideways");
            Finish(false);
            return;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var walls = new List<long>();
        var gaps = new List<long>();
        var last = sw.ElapsedMilliseconds;
        var stalls = 0;
        var crossings = 0;
        long crossedWall = 0, plainWall = 0;
        var lastRange = _table.ColumnRangeForDiagnostics;

        long Ms(long ticks) => ticks * 1000 / System.Diagnostics.Stopwatch.Frequency;
        long cellsAtStart = 0, arrangeAtStart = 0, listsAtStart = 0, syncAtStart = 0, measureAtStart = 0;
        long syncTotal = 0, measureTotal = 0, arrangeTotal = 0, cellTotal = 0, cellPreTotal = 0;
        long cellTicksAtStart = 0, cellPreAtStart = 0;
        var worstStep = "";
        long worstWall = -1;

        // Out to the last column and back, at a thumb's rate. --span limits the travel, because
        // nudging the grid a few columns is the gesture people actually make; sweeping it end to end
        // is the worst case, not the common one.
        if (double.TryParse(ReadArg("--span"), out var limit) && limit > 0)
        {
            span = Math.Min(span, limit);
        }

        var offsets = new List<double>();
        var sweeps = int.TryParse(ReadArg("--sweeps"), out var sw2) ? sw2 : 1;
        for (var s2 = 0; s2 < sweeps; s2++)
        {
            for (var x = 0d; x < span; x += notch) offsets.Add(x);
            for (var x = span; x > 0; x -= notch) offsets.Add(x);
        }

        foreach (var x in offsets)
        {
            var t0 = sw.ElapsedMilliseconds;
            var gap = t0 - last;
            last = t0;

            cellsAtStart = TableView.DiagCellMeasures;
            arrangeAtStart = TableView.DiagRowArrangeTicks;
            listsAtStart = TableView.DiagCellListBuilds;
            syncAtStart = TableView.DiagColumnSyncTicks;
            measureAtStart = TableView.DiagRowMeasureTicks;
            cellTicksAtStart = TableView.DiagCellMeasureTicks;
            cellPreAtStart = TableView.DiagCellPreMeasureTicks;

            _table.SetValue(TableView.HorizontalOffsetProperty, x);
            _table.UpdateLayout();

            var wall = sw.ElapsedMilliseconds - t0;
            syncTotal += TableView.DiagColumnSyncTicks - syncAtStart;
            measureTotal += TableView.DiagRowMeasureTicks - measureAtStart;
            arrangeTotal += TableView.DiagRowArrangeTicks - arrangeAtStart;
            cellTotal += TableView.DiagCellMeasureTicks - cellTicksAtStart;
            cellPreTotal += TableView.DiagCellPreMeasureTicks - cellPreAtStart;

            var range = _table.ColumnRangeForDiagnostics;
            var crossed = range != lastRange;
            lastRange = range;

            if (crossed)
            {
                crossings++;
                crossedWall += wall;
            }
            else
            {
                plainWall += wall;
            }

            if (walls.Count > 2)
            {
                walls.Add(wall);
                gaps.Add(gap);

                if (gap >= 50)
                {
                    stalls++;
                }

                if (wall > worstWall)
                {
                    worstWall = wall;
                    worstStep = $"x={x:F0} wall={wall}ms crossed={crossed} range={range} "
                        + $"cellMeasures={TableView.DiagCellMeasures - cellsAtStart} "
                        + $"syncMs={Ms(TableView.DiagColumnSyncTicks - syncAtStart)} "
                        + $"rowMeasureMs={Ms(TableView.DiagRowMeasureTicks - measureAtStart)} "
                        + $"arrangeMs={Ms(TableView.DiagRowArrangeTicks - arrangeAtStart)} "
                        + $"cellLists={TableView.DiagCellListBuilds - listsAtStart}";
                }
            }
            else
            {
                walls.Add(wall);
                gaps.Add(gap);
            }

            await Task.Delay(16);
        }

        var sortedWalls = walls.OrderBy(v => v).ToList();
        var sortedGaps = gaps.OrderBy(v => v).ToList();

        Console.WriteLine($"[hoster:hdrag] steps={walls.Count} notch={notch:F0}px span={span:F0}px "
            + $"crossings={crossings} crossedWallTotal={crossedWall}ms plainWallTotal={plainWall}ms "
            + $"wallMedian={sortedWalls[sortedWalls.Count / 2]}ms "
            + $"wallP99={sortedWalls[(int)(sortedWalls.Count * 0.99)]}ms wallWorst={sortedWalls[^1]}ms "
            + $"wallTotal={walls.Sum()}ms "
            + $"gapMedian={sortedGaps[sortedGaps.Count / 2]}ms gapWorst={sortedGaps[^1]}ms stalls>=50ms={stalls}");
        Console.WriteLine($"[hoster:hdrag] where the time went: syncTotal={Ms(syncTotal)}ms "
            + $"rowMeasureTotal={Ms(measureTotal)}ms rowArrangeTotal={Ms(arrangeTotal)}ms "
            + $"cellMeasureTotal={Ms(cellTotal)}ms cellPreMeasureTotal={Ms(cellPreTotal)}ms");
        Console.WriteLine($"[hoster:hdrag] worst step: {worstStep}");

        var ok = sortedWalls[(int)(sortedWalls.Count * 0.99)] <= 16 && sortedWalls[^1] <= 50;
        Console.WriteLine(ok
            ? $"[hoster:hdrag] PASS — worst step {sortedWalls[^1]}ms"
            : $"[hoster:hdrag] FAIL — worst step {sortedWalls[^1]}ms, p99 {sortedWalls[(int)(sortedWalls.Count * 0.99)]}ms");
        Finish(ok);
    }

    private async Task ResizeProbeAsync()
    {
        await SettleAsync();

        var failures = new List<string>();

        // Scrolled sideways as well as at the start, and on a column near the front as well as one
        // out in the middle: the report is of a strip that comes apart, and which of those it takes
        // is the difference between "the widths were stale" and "the offsets were".
        foreach (var offset in new double[] { 0, 600 })
        {
            foreach (var columnIndex in new[] { 1, 8 })
            {
                _table.SetValue(TableView.HorizontalOffsetProperty, offset);
                _table.UpdateLayout();
                await Task.Delay(150);

                // The control: scrolled sideways, nothing resized. Anything wrong here is not the
                // resize's doing.
                foreach (var p in HeaderStripProblems($"x={offset:F0} col={columnIndex} (before any drag)"))
                {
                    Console.WriteLine($"[hoster:resize] BEFORE {p}");
                }

                // Two drags, because the symptom is that the first comes out wrong and the second
                // hides it. Each is a burst of width changes, as a real gripper produces — and with
                // no forced layout in between, because a gripper does not force one either.
                for (var drag = 1; drag <= 2; drag++)
                {
                    var target = _table.Columns.VisibleColumns.ElementAtOrDefault(columnIndex);
                    if (target is null)
                    {
                        continue;
                    }

                    var from = target.ActualWidth;
                    var to = drag == 1 ? from + 120 : from - 60;

                    for (var step = 1; step <= 8; step++)
                    {
                        target.Width = new GridLength(from + ((to - from) * step / 8.0), GridUnitType.Pixel);
                        await Task.Delay(16);
                    }

                    // What the eye sees the moment the button comes up, before the debounced
                    // desired-width pass has had its 250ms.
                    await Task.Delay(80);
                    var immediate = HeaderStripProblems($"x={offset:F0} col={columnIndex} drag {drag} (on release)");

                    // And what is left once everything the drag kicked off has landed. A problem
                    // that survives this is the one being reported.
                    _table.UpdateLayout();
                    await Task.Delay(500);
                    var settled = HeaderStripProblems($"x={offset:F0} col={columnIndex} drag {drag} (settled)");

                    foreach (var p in immediate.Where(p => !settled.Contains(p)))
                    {
                        Console.WriteLine($"[hoster:resize] TRANSIENT {p}");
                    }

                    failures.AddRange(settled);
                }
            }
        }

        Console.WriteLine($"[hoster:resize] {failures.Count} problems after two drags");
        foreach (var f in failures.Take(12))
        {
            Console.WriteLine($"[hoster:resize] WRONG {f}");
        }

        var ok = failures.Count == 0;
        Console.WriteLine(ok
            ? "[hoster:resize] PASS — the header strip followed the resize"
            : $"[hoster:resize] FAIL — {failures.Count} headers or cells landed off their column");
        Finish(ok);
    }

    /// <summary>
    /// Where every header actually sits against where the column widths say it should, and where
    /// every realized cell sits against its own header.
    /// </summary>
    private List<string> HeaderStripProblems(string step)
    {
        var problems = new List<string>();
        var visible = _table.Columns.VisibleColumns;

        // Each header's left edge, in the strip's own coordinates: the widths of everything before
        // it. A header that reports a different offset is a header the strip laid out stale.
        var expected = new Dictionary<TableViewColumnHeader, double>();
        var x = 0d;

        foreach (var column in visible)
        {
            if (column.HeaderControl is { } header)
            {
                expected[header] = x;

                if (Math.Abs(header.ActualWidth - column.ActualWidth) > 0.5)
                {
                    problems.Add($"{step}: header \"{column.Header}\" is {header.ActualWidth:F0} wide "
                        + $"for a column of {column.ActualWidth:F0}");
                }
            }

            x += column.ActualWidth;
        }

        // Offsets are read relative to the first header, so the frozen/scrollable split and the
        // horizontal offset drop out and what is left is the strip's internal order and spacing.
        var first = expected.Keys.FirstOrDefault();
        if (first is null)
        {
            problems.Add($"{step}: no headers at all");
            return problems;
        }

        var origin = first.TransformToVisual(_table).TransformPoint(new Windows.Foundation.Point(0, 0)).X;

        var firstCell = RealizedRows().FirstOrDefault()?.Cells.FirstOrDefault();
        var cellOrigin = firstCell?.TransformToVisual(_table).TransformPoint(new Windows.Foundation.Point(0, 0)).X;
        Console.WriteLine($"[hoster:resize] {step}: offset={_table.HorizontalOffset:F0} "
            + $"firstHeaderX={origin:F0} firstCellX={cellOrigin:F0} "
            + $"headerRowScrolled={(origin - cellOrigin):F0}");

        foreach (var (header, want) in expected)
        {
            var got = header.TransformToVisual(_table).TransformPoint(new Windows.Foundation.Point(0, 0)).X - origin;
            if (Math.Abs(got - want) > 1.0)
            {
                problems.Add($"{step}: header \"{header.Column?.Header}\" sits at {got:F0}, "
                    + $"widths say {want:F0}");
            }
        }

        // And the cells under them: a strip that is right on its own but disagrees with the rows is
        // the same bug seen from the other side.
        foreach (var row in RealizedRows().Take(4))
        {
            foreach (var cell in row.Cells)
            {
                if (cell.Column?.HeaderControl is not { } header)
                {
                    continue;
                }

                var headerX = header.TransformToVisual(_table).TransformPoint(new Windows.Foundation.Point(0, 0)).X;
                var cellX = cell.TransformToVisual(_table).TransformPoint(new Windows.Foundation.Point(0, 0)).X;

                if (Math.Abs(headerX - cellX) > 1.0)
                {
                    problems.Add($"{step}: cell for \"{cell.Column?.Header}\" sits at {cellX:F0} "
                        + $"under a header at {headerX:F0}");
                }
            }
        }

        return problems;
    }

    private async Task FlickProbeAsync()
    {
        var cap = double.TryParse(ReadArg("--cap"), out var c) ? c : 0;

        BuildSkeletonAndModel();
        ScrollToOffset(0);
        await SettleAsync();
        _scrollViewer ??= FindScrollViewer(_table);
        if (_scrollViewer is not { } sv)
        {
            Console.WriteLine("[hoster:flick] no ScrollViewer");
            Finish(false);
            return;
        }

        _table.MaxScrollRowsPerFrame = cap;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var gaps = new List<long>();
        var last = sw.ElapsedMilliseconds;

        // Four flicks, each a burst that decays the way momentum does.
        for (var flick = 0; flick < 4; flick++)
        {
            var velocity = 900.0;      // pixels per frame at the top of the flick
            while (velocity > 8)
            {
                var t0 = sw.ElapsedMilliseconds;
                gaps.Add(t0 - last);
                last = t0;

                _table.ScrollByPixels(velocity);
                velocity *= 0.92;

                await Task.Delay(16);
            }

            // Let it settle before the next flick, as a hand does.
            for (var i = 0; i < 12; i++)
            {
                var t0 = sw.ElapsedMilliseconds;
                gaps.Add(t0 - last);
                last = t0;
                await Task.Delay(16);
            }
        }

        gaps.RemoveAt(0);
        gaps.Sort();
        var median = gaps[gaps.Count / 2];
        var p99 = gaps[(int)(gaps.Count * 0.99)];
        var worst = gaps[^1];
        var janky = gaps.Count(g => g >= 50);

        Console.WriteLine($"[hoster:flick] cap={cap:0.#} rows/frame frames={gaps.Count} "
            + $"offset={sv.VerticalOffset:F0} gapMedian={median}ms gapP99={p99}ms gapWorst={worst}ms "
            + $"framesOver50ms={janky}");

        var ok = worst <= 100;
        Console.WriteLine(ok
            ? $"[hoster:flick] PASS — worst frame {worst}ms"
            : $"[hoster:flick] FAIL — worst frame {worst}ms");
        Finish(ok);
    }

    private async Task WheelProbeAsync()
    {
        // --nolatency: no simulated store latency, so a stall that survives is not a page landing.
        if (Environment.GetCommandLineArgs().Contains("--nolatency"))
        {
            _latencyMs = 0;
        }

        BuildSkeletonAndModel();
        ScrollToOffset(0);
        await SettleAsync();
        _scrollViewer ??= FindScrollViewer(_table);
        if (_scrollViewer is not { } sv)
        {
            Console.WriteLine("[hoster:wheel] no ScrollViewer");
            Finish(false);
            return;
        }

        // A macOS trackpad glide delivers tens of pixels a frame; a wheel notch is a few rows at
        // once. Both are the same gesture as far as the panel is concerned — walk the list — and
        // the bigger the notch the more rows each frame has to realize.
        var notch = double.TryParse(ReadArg("--notch"), out var n) ? n : 40;
        var steps = int.TryParse(ReadArg("--steps"), out var s) ? s : 400;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var walls = new List<long>();
        var gaps = new List<long>();
        var stalls = new List<string>();
        var last = sw.ElapsedMilliseconds;

        // Counters read at the START of each step, so a step's deltas cover the whole frame that
        // preceded it — the scroll call AND everything the thread did after it returned, which is
        // where a landing page patches rows and the panel re-realizes them.
        long prep0 = 0, prepT0 = 0, postT0 = 0, measT0 = 0, cellT0 = 0, cellPreT0 = 0, cells0 = 0,
             arrT0 = 0, arr0 = 0, reads0 = 0, scans0 = 0, lists0 = 0, states0 = 0, statesT0 = 0, rowIdx0 = 0;

        long Ms(long ticks) => ticks * 1000 / System.Diagnostics.Stopwatch.Frequency;

        for (var i = 1; i <= steps; i++)
        {
            var t0 = sw.ElapsedMilliseconds;
            var gap = t0 - last;
            last = t0;

            if (gap >= 50 && i > 2)
            {
                stalls.Add($"[hoster:wheel] stall step={i} gap={gap}ms offset={sv.VerticalOffset:F0} "
                    + $"prepares={TableView.DiagPrepares - prep0} prepMs={Ms(TableView.DiagPrepareTicks - prepT0)} "
                    + $"postPrepMs={Ms(TableView.DiagPostPrepareTicks - postT0)} "
                    + $"rowMeasureMs={Ms(TableView.DiagRowMeasureTicks - measT0)} "
                    + $"cellMeasures={TableView.DiagCellMeasures - cells0} "
                    + $"cellMs={Ms(TableView.DiagCellMeasureTicks - cellT0)} "
                    + $"cellPreMs={Ms(TableView.DiagCellPreMeasureTicks - cellPreT0)} "
                    + $"rowArranges={TableView.DiagRowArranges - arr0} arrangeMs={Ms(TableView.DiagRowArrangeTicks - arrT0)} "
                    + $"reads={VirtualTreeModel.DiagReads - reads0} idxScans={VirtualTreeModel.DiagIndexOfScans - scans0} "
                    + $"cellLists={TableView.DiagCellListBuilds - lists0} rowIdx={TableView.DiagRowIndexLookups - rowIdx0} "
                    + $"goToStates={TableView.DiagGoToStates - states0} "
                    + $"goToStateMs={Ms(TableView.DiagGoToStateTicks - statesT0)}");
            }

            prep0 = TableView.DiagPrepares;
            prepT0 = TableView.DiagPrepareTicks;
            postT0 = TableView.DiagPostPrepareTicks;
            measT0 = TableView.DiagRowMeasureTicks;
            cellT0 = TableView.DiagCellMeasureTicks;
            cellPreT0 = TableView.DiagCellPreMeasureTicks;
            cells0 = TableView.DiagCellMeasures;
            arrT0 = TableView.DiagRowArrangeTicks;
            arr0 = TableView.DiagRowArranges;
            reads0 = VirtualTreeModel.DiagReads;
            scans0 = VirtualTreeModel.DiagIndexOfScans;
            lists0 = TableView.DiagCellListBuilds;
            rowIdx0 = TableView.DiagRowIndexLookups;
            states0 = TableView.DiagGoToStates;
            statesT0 = TableView.DiagGoToStateTicks;

            sv.ChangeView(null, notch * i, null, disableAnimation: true);
            var wall = sw.ElapsedMilliseconds - t0;

            if (i > 2)
            {
                walls.Add(wall);
                gaps.Add(gap);
            }

            await Task.Delay(16);       // the next frame, as a hand would deliver it
        }

        walls.Sort();
        gaps.Sort();
        var median = gaps[gaps.Count / 2];
        var p99 = gaps[(int)(gaps.Count * 0.99)];
        var worst = gaps[^1];

        foreach (var stall in stalls.Take(12))
        {
            Console.WriteLine(stall);
        }

        Console.WriteLine($"[hoster:wheel] steps={steps} notch={notch:F0}px "
            + $"gapMedian={median}ms gapP99={p99}ms gapWorst={worst}ms "
            + $"wallMedian={walls[walls.Count / 2]}ms wallWorst={walls[^1]}ms "
            + $"stalls>=50ms={stalls.Count}");

        // A frame is 16ms. A step that costs more than three of them is a hitch a hand feels.
        var ok = p99 <= 50 && worst <= 150;
        Console.WriteLine(ok
            ? $"[hoster:wheel] PASS — no step past 150ms, p99 {p99}ms"
            : $"[hoster:wheel] FAIL — worst step {worst}ms, p99 {p99}ms");
        Finish(ok);
    }

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
        // Does the scrollbar agree with the content? The extent the panel reports divided by the
        // number of rows is the height it BELIEVES a row is; the realized rows say what one
        // actually is. When those disagree the bar is the wrong length, and dragging it to the
        // bottom stops short of the end — or runs past it.
        var rowsNow = RealizedRows();
        var actualRowHeight = rowsNow.Count > 0 ? rowsNow[0].ActualHeight : 0;
        var count = _source.Count;
        var believedRowHeight = count > 0 ? sv.ExtentHeight / count : 0;
        Console.WriteLine($"[hoster:fling] EXTENT rows={count} extent={sv.ExtentHeight:F0} "
            + $"believedRowHeight={believedRowHeight:F1} actualRowHeight={actualRowHeight:F1} "
            + $"setRowHeight={_table.RowHeight:F1} overshoot={(actualRowHeight > 0 ? believedRowHeight / actualRowHeight : 0):P0}");
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
