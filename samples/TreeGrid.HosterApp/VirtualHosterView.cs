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
        if (MusicPath is not null)
        {
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

        // The state mark and the duplicate badge.
        for (var i = 0; i < 2; i++)
        {
            _table.Columns.Add(new TableViewTemplateColumn
            {
                Header = "",
                Width = new GridLength(26),
                CellTemplate = MarkTemplate(),
            });
        }

        _table.Columns.Add(new TableViewTreeColumn
        {
            Header = "Name",
            Width = new GridLength(260),
            Binding = new Microsoft.UI.Xaml.Data.Binding { Path = new PropertyPath(nameof(DemoNode.Name)) },
            GlyphBinding = new Microsoft.UI.Xaml.Data.Binding { Path = new PropertyPath(nameof(DemoNode.Glyph)) },
        });

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

                artistNode ??= new DemoNode(artist, 0);
                var albumNode = new DemoNode(album, 1) { LeafCount = hits.Length };
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
            var artistNode = new DemoNode(artist, 0);
            _roots.Add(artistNode);

            foreach (var (album, tracks) in albums)
            {
                var albumNode = new DemoNode(album, 1) { LeafCount = tracks.Length };
                artistNode.Children.Add(albumNode);
                _musicLeaves[albumNode] = tracks;
            }
        }

        _model = new VirtualTreeModel(
            childrenOf: n => ((DemoNode)n).Children,
            leafCountOf: n => n is DemoNode d ? d.LeafCount : 0,
            fetchLeaves: FetchMusicLeavesAsync,
            placeholderOf: PlaceholderFor,
            pageSize: PageSize);
        _model.SetRoots(_roots);
        _rootsAll = _roots;
        _source = new VirtualTreeItemsSource(_model);
        _table.ItemsSource = _source;

        Console.WriteLine($"[hoster:music] {_music.TrackCount} tracks, {_roots.Count} artists, "
            + $"{_music.AlbumCount} albums, loaded in {t0.ElapsedMilliseconds}ms");
    }

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
            rows.Add(new DemoNode(track.DisplayName, album.Depth + 1) { Track = track });
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

        // --paint: are the cells actually SHOWING anything? Every other check here asks whether
        // the right rows are realized in the right places; none of them looks inside a cell. A
        // grid whose cells are all present, correctly sized and empty passes all of them.
        if (Environment.GetCommandLineArgs().Contains("--paint"))
        {
            await PaintProbeAsync();
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
