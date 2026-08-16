using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using WinUI.TableView.Extensions;
using WinUI.TableView.Helpers;

namespace WinUI.TableView;

/// <summary>
/// Represents a control that displays data in customizable table-like interface.
/// </summary>
[StyleTypedProperty(Property = nameof(ColumnHeaderStyle), StyleTargetType = typeof(TableViewColumnHeader))]
[StyleTypedProperty(Property = nameof(CellStyle), StyleTargetType = typeof(TableViewCell))]
public partial class TableView : ListView
{
    private TableViewHeaderRow? _headerRow;
    private ScrollViewer? _scrollViewer;
    private RowDefinition? _headerRowDefinition;
    private bool _shouldThrowSelectionModeChangedException;
    private bool _ensureColumns = true;

    // FOBO fork: suppresses the OnBaseItemsSourceChanged guard during
    // SwapItemsSource. The upstream callback throws whenever
    // base.ItemsSource is touched after construction so callers can't
    // bypass the new ItemsSource property; the swap path here is a
    // legitimate internal use, so we open a tiny window for it.
    private bool _allowInternalBaseItemsSourceSet;

    private bool _isItemsSourceSuspended;
    private readonly List<TableViewRow> _rows = [];

    // FOBO fork: typed as the abstract <see cref="ITableViewItemsSource"/>
    // contract instead of the concrete <see cref="CollectionView"/>, so a
    // host application can supply its own implementation (e.g. a
    // SQL-backed virtual source). The default constructor still uses
    // <see cref="CollectionView"/>; nothing about runtime behaviour
    // changes for callers that don't supply their own source.
    //
    // Not <c>readonly</c> because <see cref="SwapItemsSource"/> replaces
    // the slot when the caller hands us a custom <see cref="ITableViewItemsSource"/>.
    private ITableViewItemsSource _collectionView = new CollectionView();

    private Border? _dragRectangle;
    private Point? _dragStartPoint;
    private bool _cellSelectionDirty;
    private bool _suppressSelectionChangedCellClear;
    private Point? _lastDragCanvasPoint;
    private DispatcherTimer? _autoScrollTimer;
    private double _autoScrollVerticalDelta;
    private double _autoScrollHorizontalDelta;
    private double _dragStartVerticalOffset;
    private double _dragStartHorizontalOffset;

    // FIX A (two-phase drag): a plain press ARMS a drag — records the start point and the
    // scroll baseline — but does not ENGAGE it. Only real pointer travel past the cell's drag
    // threshold promotes the armed press into an active drag-selection, so a click never
    // subscribes ViewChanged, flashes the rectangle, or scrolls the current cell into view on
    // release.
    private bool _dragArmed;
    private Point _armedDragStartPoint;
    private double _armedDragVerticalOffset;
    private double _armedDragHorizontalOffset;

    /// <summary>
    /// Initializes a new instance of the TableView class.
    /// </summary>
    public TableView()
    {
        DefaultStyleKey = typeof(TableView);

        Columns = new TableViewColumnsCollection(this);
        FilterHandler = new ColumnFilterHandler(this);

        base.ItemsSource = _collectionView;
        base.SelectionMode = SelectionMode;
        _collectionView.VectorChanged += OnItemsMoved;
#if !WINDOWS
        HookUnoVectorChanged(_collectionView);
#endif

        SetValue(ConditionalCellStylesProperty, new TableViewConditionalCellStylesCollection());
        RegisterPropertyChangedCallback(ItemsControl.ItemsSourceProperty, OnBaseItemsSourceChanged);
        RegisterPropertyChangedCallback(ListViewBase.SelectionModeProperty, OnBaseSelectionModeChanged);

        // Pointer events carry the LIVE OS modifier state. InputKeyboardSource's tracked state
        // goes stale when the app is switched away mid-modifier (the KeyUp lands in the other
        // app), leaving every later click acting as a shift/ctrl-click until the key is pressed
        // again in this window — so pointer-driven selection reads these instead (see
        // LastPointerKeyModifiers). handledEventsToo: cells handle these events.
        AddHandler(PointerPressedEvent, new PointerEventHandler(OnAnyPointerForModifiers), handledEventsToo: true);
        AddHandler(PointerMovedEvent, new PointerEventHandler(OnAnyPointerForModifiers), handledEventsToo: true);
        AddHandler(PointerReleasedEvent, new PointerEventHandler(OnAnyPointerForModifiers), handledEventsToo: true);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SelectionChanged += TableView_SelectionChanged;
        _collectionView.ItemPropertyChanged += OnItemPropertyChanged;
    }

    /// <summary>
    /// Handles the SelectionChanged event of the TableView control.
    /// </summary>
    private void TableView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionChangedCellClear)
        {
            _suppressSelectionChangedCellClear = false;
        }
        else
        {
            if (!KeyboardHelper.IsCtrlKeyDown())
            {
                SelectedCellRanges.Clear();
            }
            else
            {
                SelectedCellRanges.RemoveWhere(slots =>
                {
                    slots.RemoveWhere(slot => SelectedRanges.Any(range => range.IsInRange(slot.Row)));
                    return slots.Count == 0;
                });
            }

            CurrentCellSlot = null;
            OnCellSelectionChanged();
        }

        if (SelectedItems?.Count == 1)
        {
            DispatcherQueue.TryEnqueue(async () => await ScrollRowIntoView(SelectedIndex));
        }
    }

    /// <summary>
    /// Handles the PropertyChanged event of an item in the TableView.
    /// </summary>
    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var row = ContainerFromItem(sender) as TableViewRow;

        row?.EnsureCellsStyle(default, sender);

        // Text columns write their value instead of binding it (see TableViewTextColumn), which
        // trades a binding expression per cell per scrolled row for this: the one row that actually
        // changed, re-read. An edit, a track finishing its analysis, a cloud state moving — rare,
        // and cheap when it happens.
        row?.RefreshCells(sender);
    }

    /// <summary>
    /// Anything the HOST wants on the end of every stall line.
    ///
    /// <para>The counters here describe the grid, and a frame the grid had no part in is exactly the
    /// frame they cannot explain — which is the state a well-optimized grid ends up in. Rather than
    /// teach this library about the app around it, the app says what it knows: which of its own
    /// animations are running, what it is loading, whatever the question of the day is.</para>
    /// </summary>
    public static Func<string>? DiagAnnotation;

    /// <summary>Diagnostics for the hoster's fling probe: container prepares per scroll hop
    /// and the time spent inside them (stopwatch ticks).</summary>
    public static int DiagPrepares;
    public static long DiagPrepareTicks;
    public static long DiagRowMeasureTicks;

    /// <summary>
    /// Where a scroll hop's time goes, and how much of it is repeated work. The hoster's fling
    /// probe prints these per hop: a count that scales with rows × columns when it should scale
    /// with one of them is the signal to go looking. The timings say which of those counts is
    /// worth caring about — several turned out not to be.
    /// </summary>
    public static long DiagPostPrepareTicks;
    public static long DiagCellMeasureTicks;
    public static long DiagCellPreMeasureTicks;
    public static long DiagCellMeasures;
    public static long DiagRowArrangeTicks;
    public static long DiagRowArranges;
    public static long DiagGoToStates;
    public static long DiagGoToStateTicks;
    public static long DiagCellListBuilds;
    public static long DiagRowIndexLookups;
    public static long DiagAlternateSweeps;
    public static long DiagInsertCellScans;

    /// <summary>
    /// The work that happens BETWEEN layout passes, which the counters above cannot see and which a
    /// stall line without them reports as an unexplained gap.
    ///
    /// <para>A grid over a paged source does most of its expensive work off the layout path: a page
    /// lands and raises an event per row in it, the burst is drained on a dispatcher turn, the drain
    /// re-points the source or re-seats the panel, and only then does anything measure. Every one of
    /// those is a frame the reader lost with prepares, measures and arranges all reading zero — which
    /// is exactly what the first report of this stall looked like.</para>
    /// </summary>
    public static long DiagContainerNews;
    public static long DiagContainerNewTicks;
    public static long DiagPageLands;
    public static long DiagPageLandTicks;
    public static long DiagRowEvents;
    public static long DiagSourceResets;
    public static long DiagPatches;
    public static long DiagPatchTicks;
    public static long DiagReseats;
    public static long DiagReseatTicks;
    public static long DiagRebinds;
    public static long DiagRebindTicks;

    /// <summary>
    /// The leaf timings — the regions that are charged for their own time wherever they run. Anything
    /// that can drive a layout pass synchronously (a rebind, a page landing, a burst drain) subtracts
    /// the delta in these across itself, so its own figure is the time it spent on ITS work and the
    /// same milliseconds are not counted twice in two buckets.
    ///
    /// <para>Cell measures are deliberately absent: they run inside a row measure and are reported as
    /// a breakdown of it, not alongside it.</para>
    /// </summary>
    private static long DiagLeafTicks()
        => DiagPrepareTicks + DiagRowMeasureTicks + DiagRowArrangeTicks + DiagContainerNewTicks;

    /// <summary>
    /// Runs <paramref name="body"/> and charges it to a bucket, net of any layout it caused.
    /// </summary>
    internal static void DiagCharge(ref long ticks, ref long count, Action body)
    {
        var t0 = Stopwatch.GetTimestamp();
        var leaf0 = DiagLeafTicks();
        try
        {
            body();
        }
        finally
        {
            count++;
            ticks += Stopwatch.GetTimestamp() - t0 - (DiagLeafTicks() - leaf0);
        }
    }

    /// <summary>
    /// The grid the counters are actually about. They are static — one set for every TableView in the
    /// app — but the watch below is started by whichever grid loads first, and reads its offset, its
    /// scroll viewer and its item count. On a page holding two grids that is reliably the wrong one:
    /// the first report of this stall read offset=0 moved=0 rows=0 on every line, because the grid
    /// being scrolled was the second one and the one being asked was an empty queue.
    ///
    /// <para>Set by whichever grid last realized or measured a row, which during a scroll is the grid
    /// doing the scrolling.</para>
    /// </summary>
    internal static TableView? DiagActiveGrid;

    /// <summary>
    /// A scrolling list that is NOT a TableView, reporting through the same watch: its name, where it
    /// is, and how many rows it is over.
    ///
    /// <para>For weighing an alternative against this one. The watch measures the frame, not the
    /// control, so pointing it at something else costs nothing and keeps the comparison honest — the
    /// same threshold, the same clock, the same line. Every counter below stays at zero while it is
    /// set, which is the point: whatever <c>rest</c> comes back as is the alternative's own cost, with
    /// none of this control's work mixed into it.</para>
    /// </summary>
    public static Func<(string Name, double Offset, int Count)>? DiagExternalScroll;

    /// <summary>
    /// What the last <see cref="ScrollRowIntoView"/> decided and what it decided it from — the row
    /// asked for, the offset arithmetic, and whether it jumped, declined, or was clamped short.
    ///
    /// <para>Reading this after a reveal is the difference between "the grid walked" and "the grid
    /// jumped to the wrong place": both leave the same stack. Set by <see cref="JumpToRow"/>.</para>
    /// </summary>
    public static string DiagLastJump = "";

    /// <summary>
    /// Where the diagnostics write. Null sends them to stdout and to the debugger, which is fine for
    /// a harness and no use at all inside a real app: an app launched from an IDE may have no
    /// console attached, and its debug output may be filtered down to its own assemblies. Point this
    /// at the host's logger and the lines land wherever the host's own lines do.
    /// </summary>
    public static Action<string>? DiagnosticsWriter;

    private static void Report(string line)
    {
        if (DiagnosticsWriter is { } writer)
        {
            writer(line);
            return;
        }

        Console.WriteLine(line);
        System.Diagnostics.Debug.WriteLine(line);
    }

    /// <summary>
    /// Watches for frames that never came. A timer ticking every few milliseconds cannot tick while
    /// the thread is blocked, so a late tick measures the block exactly — and the counters above say
    /// what the thread was doing while it held on.
    ///
    /// <para>Off unless <c>TABLEVIEW_STALL_MS</c> is set, which is also the threshold: a stall
    /// shorter than that is not worth a line. This is the instrument for a stall in a REAL app,
    /// where the work a row does is the app's own and no harness can stand in for it.</para>
    /// </summary>
    private static readonly int StallThresholdMs = ReadStallThreshold();

    /// <summary>The threshold, from the environment or from <c>--stall-ms N</c> on the command
    /// line. Both, because a launcher that can pass arguments cannot always pass an environment.</summary>
    private static int ReadStallThreshold()
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("TABLEVIEW_STALL_MS"), out var fromEnv) && fromEnv > 0)
        {
            return fromEnv;
        }

        var args = Environment.GetCommandLineArgs();
        var at = Array.IndexOf(args, "--stall-ms");
        return at >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], out var fromArgs) ? fromArgs : 0;
    }

    private static Microsoft.UI.Dispatching.DispatcherQueueTimer? _stallTimer;
    private static long _stallLastTick;
    private static double _stallLastOffset;
    private static long _stallPrepares, _stallPrepareTicks, _stallPostTicks, _stallMeasureTicks,
                        _stallCellTicks, _stallCells, _stallArrangeTicks, _stallArranges,
                        _stallLists, _stallRowIdx;
    private static long _stallNews, _stallNewTicks, _stallLands, _stallLandTicks, _stallRowEvents,
                        _stallResets, _stallPatches, _stallPatchTicks, _stallReseats,
                        _stallReseatTicks, _stallRebinds, _stallRebindTicks;
    private static long _stallAlloc;
    private static int _stallGen0, _stallGen1, _stallGen2;

    /// <summary>Total time this process has spent stopped for the collector. The DELTA across a
    /// stalled frame is how much of that frame was the collector rather than anything that ran —
    /// which collection COUNTS cannot say, a hundred cheap gen0s and one expensive gen2 pause
    /// reading much the same. It is the difference between "the grid allocates too much" and "the
    /// grid was not involved".</summary>
    private static TimeSpan _stallGcPause;

    /// <summary>
    /// When the compositor last got a turn, and how many turns it got. The one thing that separates
    /// the two ways a frame can be lost with every managed counter reading zero.
    ///
    /// <para>Renders during the stall means the thread WAS in the frame loop and the frame itself was
    /// slow: laying out and painting a viewport is the cost, and no amount of making the grid's own
    /// bookkeeping cheaper will touch it. No renders at all means the loop never got a turn — the
    /// thread was held somewhere else entirely, and the grid is a bystander.</para>
    /// </summary>
    private static long _stallRenders, _stallLastRenderTick;
    private static long _renderCount;
    private static long _stallDrains, _stallDrainTicks;
    private static long _stallCellsInf, _stallCellContent, _stallCellLoaded, _stallWheel, _stallTextSets;

    /// <summary>How many paced scroll steps were let out, and what they cost. A step is one
    /// <c>ChangeView</c>, and the pacer's whole premise is that a step fits in a frame; if it does
    /// not, the budget collapses to its floor and the list crawls at three rows a frame however
    /// hard the gesture was.</summary>
    public static long DiagDrains;
    public static long DiagDrainTicks;

    /// <summary>Cells measured with no constraint, cells whose content was swapped, and the Loaded
    /// handlers that turn the second into the first. A frame where these carry the cell count is a
    /// frame spent re-measuring content out of band, not laying a viewport out.</summary>
    /// <summary>Cell values actually written. The gap between this and the cells re-shown is what the
    /// equality check saves: a scroll re-shows a great many cells whose text has not changed.</summary>
    public static long DiagCellTextSets;

    public static long DiagCellInfiniteMeasures;
    public static long DiagCellContentChanges;
    public static long DiagCellLoadedMeasures;

    /// <summary>Wheel events delivered. The one counter that says whether a still list is a list
    /// nobody is scrolling or a list that cannot be scrolled.</summary>
    public static long DiagWheelEvents;

    private void StartStallWatch()
    {
        if (StallThresholdMs <= 0 || _stallTimer is not null || DispatcherQueue is null)
        {
            return;
        }

        _stallLastTick = Stopwatch.GetTimestamp();
        Snapshot();

        // The frame loop's own pulse. Everything the grid does in managed code is already counted;
        // this counts the turns the compositor got, which is the only way to tell a frame the thread
        // spent PAINTING from a frame the thread never reached.
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += (_, _) =>
        {
            _renderCount++;
            _stallLastRenderTick = Stopwatch.GetTimestamp();
        };

        _stallTimer = DispatcherQueue.CreateTimer();
        _stallTimer.Interval = TimeSpan.FromMilliseconds(8);
        _stallTimer.IsRepeating = true;
        _stallTimer.Tick += (_, _) =>
        {
            var now = Stopwatch.GetTimestamp();
            var elapsedMs = (now - _stallLastTick) * 1000 / Stopwatch.Frequency;
            _stallLastTick = now;

            // The grid the counters are about, not the one that happened to load first. See
            // DiagActiveGrid — asking the wrong grid is what made every line read offset=0 rows=0.
            var grid = DiagActiveGrid ?? this;
            var external = DiagExternalScroll?.Invoke();

            // How far the reader actually moved while the thread was busy. Rows realized far in
            // excess of the distance travelled are rows being built twice, not rows being reached.
            var offset = external is { } ext ? ext.Offset : grid._scrollViewer?.VerticalOffset ?? 0;
            var moved = offset - _stallLastOffset;
            _stallLastOffset = offset;

            if (elapsedMs >= StallThresholdMs)
            {
                static long Ms(long ticks) => ticks * 1000 / Stopwatch.Frequency;

                var prepMs = Ms(DiagPrepareTicks - _stallPrepareTicks);
                var newMs = Ms(DiagContainerNewTicks - _stallNewTicks);
                var postPrepMs = Ms(DiagPostPrepareTicks - _stallPostTicks);
                var measureMs = Ms(DiagRowMeasureTicks - _stallMeasureTicks);
                var arrangeMs = Ms(DiagRowArrangeTicks - _stallArrangeTicks);
                var landMs = Ms(DiagPageLandTicks - _stallLandTicks);
                var patchMs = Ms(DiagPatchTicks - _stallPatchTicks);
                var reseatMs = Ms(DiagReseatTicks - _stallReseatTicks);
                var rebindMs = Ms(DiagRebindTicks - _stallRebindTicks);

                // What the thread was doing that none of the buckets claimed. A large rest is not a
                // shrug — it is the finding: the time went somewhere this instrument does not look
                // yet, and the next bucket to add is whatever the app was doing on that turn.
                var drainMs = Ms(DiagDrainTicks - _stallDrainTicks);
                var rest = elapsedMs - (prepMs + newMs + postPrepMs + measureMs + arrangeMs
                                        + landMs + patchMs + reseatMs + rebindMs + drainMs);

                var own = string.IsNullOrEmpty(grid.Name) ? "?" : grid.Name;

                // When something else is being watched, the grid still doing the work is named too.
                // Without it an alternative can be credited with a table's numbers: a control that is
                // not on screen reports no position and no rows, every counter below belongs to the
                // table that IS on screen, and the line reads as one list that cannot scroll.
                // Which path drew the rows, on every line. The whole point of the light path is that
                // the same gesture on the same grid produces different numbers, and a run whose
                // opt-in silently did not take (see TableView.LightRows) would otherwise read as the
                // light path having bought nothing.
                own += grid.AreRowsLight ? "(light)" : "";

                var gridName = external is { } extName ? $"{extName.Name}(work={own})" : own;
                var rowCount = external is { } extRows ? extRows.Count : grid.Items?.Count ?? 0;

                Report($"[stall] {elapsedMs}ms rest={rest}ms grid={gridName} "
                    + $"offset={offset:F0} moved={moved:F0} rows={rowCount} "
                    + $"| prepares={DiagPrepares - _stallPrepares} prepMs={prepMs} "
                    + $"news={DiagContainerNews - _stallNews} newMs={newMs} postPrepMs={postPrepMs} "
                    + $"| rowMeasureMs={measureMs} cells={DiagCellMeasures - _stallCells} cellMs={Ms(DiagCellMeasureTicks - _stallCellTicks)} "
                    + $"textSets={DiagCellTextSets - _stallTextSets} "
                    + $"cellsInf={DiagCellInfiniteMeasures - _stallCellsInf} "
                    + $"cellContent={DiagCellContentChanges - _stallCellContent} cellLoaded={DiagCellLoadedMeasures - _stallCellLoaded} "
                    + $"| arranges={DiagRowArranges - _stallArranges} arrangeMs={arrangeMs} "
                    + $"| lands={DiagPageLands - _stallLands} landMs={landMs} "
                    + $"rowEvents={DiagRowEvents - _stallRowEvents} resets={DiagSourceResets - _stallResets} "
                    + $"| patches={DiagPatches - _stallPatches} patchMs={patchMs} "
                    + $"reseats={DiagReseats - _stallReseats} reseatMs={reseatMs} "
                    + $"rebinds={DiagRebinds - _stallRebinds} rebindMs={rebindMs} "
                    + $"| wheel={DiagWheelEvents - _stallWheel} pendingPx={grid._pendingScroll:F0} "
                    + $"pace={(double.IsFinite(grid._paceBudget) ? grid._paceBudget.ToString("F0") : "-")} "
                    + $"drains={DiagDrains - _stallDrains} drainMs={drainMs} "
                    + $"| renders={_renderCount - _stallRenders} "
                    + $"sinceRenderMs={(_stallLastRenderTick == 0 ? -1 : (now - _stallLastRenderTick) * 1000 / Stopwatch.Frequency)} "
                    + $"| gc={GC.CollectionCount(0) - _stallGen0}/{GC.CollectionCount(1) - _stallGen1}/{GC.CollectionCount(2) - _stallGen2} "
                    + $"gcPauseMs={(long)(GC.GetTotalPauseDuration() - _stallGcPause).TotalMilliseconds} "
                    + $"allocMB={(GC.GetTotalAllocatedBytes(precise: false) - _stallAlloc) / (1024 * 1024)} "
                    + $"| cells/frame={(grid._rows.Count * grid.Columns.VisibleColumns.Count)} "
                    + $"| cellLists={DiagCellListBuilds - _stallLists} rowIdx={DiagRowIndexLookups - _stallRowIdx}"
                    + (DiagAnnotation?.Invoke() is { Length: > 0 } note ? $" | {note}" : ""));
            }

            Snapshot();
        };
        _stallTimer.Start();

        Report($"[stall] watching, reporting frames longer than {StallThresholdMs}ms");

        static void Snapshot()
        {
            _stallPrepares = DiagPrepares;
            _stallPrepareTicks = DiagPrepareTicks;
            _stallPostTicks = DiagPostPrepareTicks;
            _stallMeasureTicks = DiagRowMeasureTicks;
            _stallCellTicks = DiagCellMeasureTicks;
            _stallCells = DiagCellMeasures;
            _stallArrangeTicks = DiagRowArrangeTicks;
            _stallArranges = DiagRowArranges;
            _stallLists = DiagCellListBuilds;
            _stallRowIdx = DiagRowIndexLookups;
            _stallNews = DiagContainerNews;
            _stallNewTicks = DiagContainerNewTicks;
            _stallLands = DiagPageLands;
            _stallLandTicks = DiagPageLandTicks;
            _stallRowEvents = DiagRowEvents;
            _stallResets = DiagSourceResets;
            _stallPatches = DiagPatches;
            _stallPatchTicks = DiagPatchTicks;
            _stallReseats = DiagReseats;
            _stallReseatTicks = DiagReseatTicks;
            _stallRebinds = DiagRebinds;
            _stallRebindTicks = DiagRebindTicks;
            _stallGen0 = GC.CollectionCount(0);
            _stallGen1 = GC.CollectionCount(1);
            _stallGen2 = GC.CollectionCount(2);
            _stallGcPause = GC.GetTotalPauseDuration();
            _stallAlloc = GC.GetTotalAllocatedBytes(precise: false);
            _stallRenders = _renderCount;
            _stallDrains = DiagDrains;
            _stallDrainTicks = DiagDrainTicks;
            _stallCellsInf = DiagCellInfiniteMeasures;
            _stallCellContent = DiagCellContentChanges;
            _stallCellLoaded = DiagCellLoadedMeasures;
            _stallWheel = DiagWheelEvents;
            _stallTextSets = DiagCellTextSets;
        }
    }

    /// <summary>
    /// Bumped whenever items can have changed position, which is the only thing that can make a
    /// realized row's cached <see cref="TableViewRow.Index"/> wrong without that row itself being
    /// rebound. Starts at 1 so a row's initial generation of 0 always reads as stale.
    /// </summary>
    internal int RowIndexGeneration { get; private set; } = 1;

    private void OnItemsMoved(IObservableVector<object> sender, IVectorChangedEventArgs args)
        => RowIndexGeneration++;

    /// <inheritdoc/>
    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        DiagPrepares++;
        DiagActiveGrid = this;

        // Before the base call, because the base call is what puts the item on the row — and a row
        // whose content changes rebuilds or refills itself, which it cannot do without knowing the
        // grid it belongs to.
        //
        // This used to be assigned a dispatcher tick later, so every recycled row did that work with
        // a null grid. It cost nothing while a row's columns were bindings that re-resolve on their
        // own; it costs everything now that a row writes its own values, because the row read as not
        // being a light one and refreshed the handful of cells it still had. A sort showed it plainly:
        // the rows stay on screen, only their values move, and only the two template columns moved.
        if (element is TableViewRow tracked)
        {
            tracked.TableView = this;
            tracked.InvalidateIndex();

            // Tracked as currently-realized. Added here (on realize), removed in
            // ClearContainerForItemOverride (on recycle) so _rows stays bounded to the viewport.
            if (!_rows.Contains(tracked))
            {
                _rows.Add(tracked);
            }
        }

        var diagT0 = System.Diagnostics.Stopwatch.GetTimestamp();
        base.PrepareContainerForItemOverride(element, item);
        DiagPrepareTicks += System.Diagnostics.Stopwatch.GetTimestamp() - diagT0;

#if !WINDOWS
        // Post-rebind scroll restore rides the panel's own rebuild: prepares are the only
        // signal that provably fires in that window (see TryUnoReanchorRestore).
        if (_unoReanchorPending)
        {
            ++_unoReanchorPrepares;
            TryUnoReanchorRestore();
        }
        else if (_unoReseatBurstsLeft > 0)
        {
            // Recovery materialization continues after the verified restore; with a warm page
            // cache no ItemChanged bursts arrive to drain the armed re-seats, so prepares do.
            _unoReseatBurstsLeft--;
            DiagCharge(ref DiagReseatTicks, ref DiagReseats, ReseatPanelRows);
        }
#endif

        DispatcherQueue.TryEnqueue(() =>
        {
            if (element is TableViewRow row)
            {
                var postT0 = System.Diagnostics.Stopwatch.GetTimestamp();
                if (!_rows.Contains(row))
                {
                    _rows.Add(row);
                }

                row.TableView = this;
                row.EnsureCellsStyle(default, item);
                row.ApplyCellsSelectionState();
                row.RowPresenter?.ApplyDetailsPaneState(item);

                // Logical all-selected: a row scrolled into view shows selected
                // unless it's been excluded.
                ApplyLogicalSelectionVisual(row);

                if (CurrentCellSlot.HasValue)
                {
                    row.ApplyCurrentCellState(CurrentCellSlot.Value);
                }

                DiagPostPrepareTicks += System.Diagnostics.Stopwatch.GetTimestamp() - postT0;
            }
        });
    }

    /// <inheritdoc/>
    protected override void ClearContainerForItemOverride(DependencyObject element, object item)
    {
        if (element is TableViewRow row)
        {
            _rows.Remove(row);
            row.TableView = null;
        }

        base.ClearContainerForItemOverride(element, item);
    }

    /// <inheritdoc/>
    protected override DependencyObject GetContainerForItemOverride()
    {
        // A container BUILT, as against a container recycled. Timed separately from the prepare
        // because they answer different questions: prepares scaling with the distance scrolled is
        // virtualization working, and news doing the same is the recycler being defeated — every row
        // a fresh control and a fresh template, which is the expensive half by a wide margin.
        DiagContainerNews++;
        DiagActiveGrid = this;
        var diagT0 = Stopwatch.GetTimestamp();

        var row = new TableViewRow { TableView = this };

        // Set bindings for FontFamily and FontSize to propagate from TableView to TableViewRow
        row.SetBinding(FontFamilyProperty, new Binding { Path = new("TableView.FontFamily"), RelativeSource = new() { Mode = RelativeSourceMode.Self } });
        row.SetBinding(FontSizeProperty, new Binding { Path = new("TableView.FontSize"), RelativeSource = new() { Mode = RelativeSourceMode.Self } });

        DiagContainerNewTicks += Stopwatch.GetTimestamp() - diagT0;

        // NOTE: do NOT add to _rows here. Containers are created once and then
        // recycled across many items; tracking on creation (without a matching
        // removal) leaked every row ever made. _rows membership is maintained
        // in Prepare/ClearContainerForItemOverride instead.
        return row;
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        var shiftKey = KeyboardHelper.IsShiftKeyDown();
        var ctrlKey = KeyboardHelper.IsCtrlKeyDown();

        if (HandleShortKeys(shiftKey, ctrlKey, e.Key))
        {
            e.Handled = true;
            return;
        }

        HandleNavigations(e, shiftKey, ctrlKey);
    }

    /// <summary>
    /// Handles navigation keys.
    /// </summary>
    private void HandleNavigations(KeyRoutedEventArgs e, bool shiftKey, bool ctrlKey)
    {
        var currentCell = CurrentCellSlot.HasValue ? GetCellFromSlot(CurrentCellSlot.Value) : default;

        if (e.Key is VirtualKey.F2 && currentCell is { IsReadOnly: false } && !IsEditing)
        {
            e.Handled = currentCell.BeginCellEditing(e);
        }
        else if (e.Key is VirtualKey.Escape && currentCell is not null && IsEditing)
        {
            // Transfer focus from the editing element (e.g. TextBox) to the cell
            // itself BEFORE EndCellEditing tears down that element.  If we wait,
            // WinUI's focus manager will move focus to the next focusable sibling
            // the moment the editing element is removed from the visual tree, and
            // screen readers will announce that sibling instead of the current cell.
            currentCell.Focus(FocusState.Programmatic);

            e.Handled = EndCellEditing(TableViewEditAction.Cancel, currentCell);
            SetIsEditing(false);
        }
        else if (e.Key is VirtualKey.Space && currentCell is not null && CurrentCellSlot.HasValue && !IsEditing)
        {
            if (!currentCell.IsSelected)
            {
                MakeSelection(CurrentCellSlot.Value, shiftKey, ctrlKey);
            }
            else
            {
                DeselectCell(CurrentCellSlot.Value);
            }
        }

        // Handle navigation keys
        else if (e.Key is VirtualKey.Tab or VirtualKey.Enter)
        {
            var isEditing = IsEditing;

            var newSlot = CurrentCellSlot ?? new();

            do
            {
                newSlot = GetNextSlot(newSlot, shiftKey, e.Key is VirtualKey.Enter);

            } while (isEditing && Columns[newSlot.Column].IsReadOnly);

            if (isEditing && currentCell is not null)
            {
                if (!EndCellEditing(TableViewEditAction.Commit, currentCell)) return;

                if (CurrentCellSlot == newSlot || GetCellFromSlot(newSlot) is not { } nextCell || !nextCell.BeginCellEditing(e))
                {
                    SetIsEditing(false);
                }
            }

            MakeSelection(newSlot, false);

            e.Handled = true;
        }
        else if ((e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down)
                 && !IsEditing)
        {
            var row = (LastSelectionUnit is TableViewSelectionUnit.Row ? CurrentRowIndex : CurrentCellSlot?.Row) ?? -1;
            var column = CurrentCellSlot?.Column ?? -1;

            if (row == -1 && column == -1)
            {
                row = column = 0;
            }
            else if (e.Key is VirtualKey.Left or VirtualKey.Right)
            {
                column = e.Key is VirtualKey.Left ? ctrlKey ? 0 : column - 1 : ctrlKey ? Columns.VisibleColumns.Count - 1 : column + 1;
                if (column >= Columns.VisibleColumns.Count)
                {
                    column = 0;
                    row++;
                }
            }
            else
            {
                row = e.Key == VirtualKey.Up ? ctrlKey ? 0 : row - 1 : ctrlKey ? Items.Count - 1 : row + 1;
            }

            var newSlot = new TableViewCellSlot(row, column);
            MakeSelection(newSlot, shiftKey);
            e.Handled = true;
        }
        else if (e.Key is VirtualKey.Home or VirtualKey.End)
        {
            var row = ctrlKey ? (e.Key == VirtualKey.Home ? 0 : _collectionView.Count - 1) : CurrentCellSlot?.Row;
            var column = e.Key == VirtualKey.Home ? 0 : Columns.VisibleColumns.Count - 1;

            var newSlot = new TableViewCellSlot(row ?? -1, column);
            MakeSelection(newSlot, shiftKey);
            e.Handled = true;
        }
        else if (e.Key is VirtualKey.PageDown or VirtualKey.PageUp)
        {
            var pageSize = CalculateAvailablePageSize();

            var row = (LastSelectionUnit is TableViewSelectionUnit.Row ? CurrentRowIndex : CurrentCellSlot?.Row) ?? -1;
            var column = CurrentCellSlot?.Column ?? -1;

            var numRows = CollectionView.Count;
            var nextRow = e.Key == VirtualKey.PageDown
                ? Math.Min(numRows - 1, row + pageSize)
                : Math.Max(0, row - pageSize);

            var newSlot = new TableViewCellSlot(nextRow, column);
            MakeSelection(newSlot, shiftKey);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Calculates how many rows should be able to fit within the actual height of the table without scrolling.
    /// </summary>
    private int CalculateAvailablePageSize()
    {
        var rowHeight = RowHeight is not double.NaN ? RowHeight : RowMinHeight;
        var headerHeight = HeaderRowHeight is not double.NaN ? HeaderRowHeight : HeaderRowMinHeight;
        var availableHeight = ActualHeight - headerHeight;
        return (int)Math.Floor(availableHeight / rowHeight);
    }

    internal bool EndCellEditing(TableViewEditAction editAction, TableViewCell cell)
    {
        var editingElement = cell.Content as FrameworkElement;
        var endingArgs = new TableViewCellEditEndingEventArgs(cell, cell.Row?.Content, cell.Column!, editingElement!, editAction);
        OnCellEditEnding(endingArgs);
        if (endingArgs.Cancel)
        {
            return false;
        }

        cell.EndEditing(editAction);

        var endArgs = new TableViewCellEditEndedEventArgs(cell, cell.Row?.Content, cell.Column!, editAction);
        OnCellEditEnded(endArgs);

        return true;
    }

    /// <summary>
    /// Handles shortcut keys.
    /// </summary>
    private bool HandleShortKeys(bool shiftKey, bool ctrlKey, VirtualKey key)
    {
        if (key == VirtualKey.A && ctrlKey && !shiftKey)
        {
            SelectAll();
            return true;
        }
        else if (key == VirtualKey.A && ctrlKey && shiftKey)
        {
            DeselectAll();
            return true;
        }
        else if (key == VirtualKey.C && ctrlKey)
        {
            CopyToClipboardInternal(shiftKey);
            return true;
        }
        else if (key == VirtualKey.V && ctrlKey && !shiftKey)
        {
            return TryStartPasteFromClipboard();
        }

        return false;
    }

    /// <inheritdoc/>
    protected async override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _headerRow = GetTemplateChild("HeaderRow") as TableViewHeaderRow;
        _scrollViewer = GetTemplateChild("ScrollViewer") as ScrollViewer;
        _headerRowDefinition = GetTemplateChild("HeaderRowDefinition") as RowDefinition;
        DragRectangleCanvas = GetTemplateChild("DragRectangleCanvas") as Canvas;
        _dragRectangle = GetTemplateChild("DragRectangle") as Border;
        _scrollViewer?.Loaded += OnScrollViewerLoaded;

        if (IsLoaded)
        {
            while (ItemsPanelRoot is null) await Task.Yield();

            EnsureAutoColumns();
        }

        SetHeadersVisibility();
    }

    /// <summary>
    /// Handles the Loaded event of the ScrollViewer control.
    /// </summary>
    private void OnScrollViewerLoaded(object sender, RoutedEventArgs e)
    {
        var scrollPresenter = _scrollViewer?.FindDescendant<ScrollContentPresenter>();
        var xScrollBar = _scrollViewer?.FindDescendant<ScrollBar>(sb => sb.Name is "HorizontalScrollBar2");
        var yScrollBar = _scrollViewer?.FindDescendant<ScrollBar>(sb => sb.Name is "VerticalScrollBar");

        // FIX C: re-hosting the grid (flyout, dock/undock, new window) re-raises the
        // ScrollViewer's Loaded on the same template parts. Unsubscribe before subscribing so
        // handlers don't accumulate — otherwise one wheel notch is applied N times and the
        // visual tree is rewalked once per stale subscription.
        if (scrollPresenter is not null)
        {
            scrollPresenter.PointerWheelChanged -= OnScrollContentPresenterPointerWheelChanged;
            scrollPresenter.PointerWheelChanged += OnScrollContentPresenterPointerWheelChanged;
        }

        if (yScrollBar is not null)
        {
            yScrollBar.ValueChanged -= OnVerticalScrollBarValueChanged;
            yScrollBar.ValueChanged += OnVerticalScrollBarValueChanged;
        }

        // SetBinding replaces any existing binding, so re-running it on re-host is idempotent.
        xScrollBar?.SetBinding(RangeBase.ValueProperty, new Binding
        {
            Path = new PropertyPath(nameof(HorizontalOffset)),
            Mode = BindingMode.TwoWay,
            Source = this
        });

        if (_scrollViewer is not null)
        {
            _scrollViewer.ViewChanged -= OnScrollViewerViewChangedForAutoWidth;
            _scrollViewer.ViewChanged += OnScrollViewerViewChangedForAutoWidth;
        }
    }

    // Cells-mode auto-width accumulates a running max of every realized cell's desired width,
    // so on a huge source one long value anywhere in the scroll session widened its column
    // forever. Once scrolling settles AND the viewport has moved at least a page since the
    // last baseline, re-baseline the auto columns from the rows that are realized NOW
    // (RefreshColumnsAutoWidth) so widths can shrink back — debounced so widths never move
    // while the user is still scrolling.
    private DispatcherTimer? _autoWidthRebaselineTimer;
    private double _autoWidthBaselineOffset = double.NaN;

    private void OnScrollViewerViewChangedForAutoWidth(object? sender, ScrollViewerViewChangedEventArgs e)
    {
#if !WINDOWS
        // Post-rebind restore also re-attempts on every view change: our own ChangeView
        // attempts raise ViewChanged, so restore self-sustains even when container prepares
        // stop (small viewports produce very few).
        if (_unoReanchorPending)
        {
            TryUnoReanchorRestore();
        }
        else if (_unoFinalHopTarget >= 0 && _scrollViewer is { } svHop)
        {
            var hopTarget = _unoFinalHopTarget;
            _unoFinalHopTarget = -1;
            svHop.ChangeView(null, hopTarget, null, disableAnimation: true);
            DiagCharge(ref DiagReseatTicks, ref DiagReseats, ReseatPanelRows);
        }
#endif
        if (e.IsIntermediate || IsDragSelecting || _scrollViewer is null)
        {
            return;
        }

        if (!double.IsNaN(_autoWidthBaselineOffset)
            && Math.Abs(_scrollViewer.VerticalOffset - _autoWidthBaselineOffset) < _scrollViewer.ViewportHeight)
        {
            return;
        }

        if (_autoWidthRebaselineTimer is null)
        {
            _autoWidthRebaselineTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _autoWidthRebaselineTimer.Tick += OnAutoWidthRebaselineTick;
        }

        _autoWidthRebaselineTimer.Stop();
        _autoWidthRebaselineTimer.Start();
    }

    private void OnAutoWidthRebaselineTick(object? sender, object e)
    {
        _autoWidthRebaselineTimer?.Stop();

        if (_scrollViewer is null || IsDragSelecting)
        {
            return;
        }

        var columns = Columns.VisibleColumns
            .Where(c => c.Width.IsAuto
                && (c.ColumnAutoWidthMode ?? ColumnAutoWidthMode)
                    is TableViewColumnAutoWidthMode.Cells or TableViewColumnAutoWidthMode.Both)
            .ToList();

        if (columns.Count == 0)
        {
            return;
        }

        _autoWidthBaselineOffset = _scrollViewer.VerticalOffset;
        RefreshColumnsAutoWidth(columns);
    }

    /// <summary>
    /// Mirrors the vertical scroll bar's value onto <see cref="VerticalOffset"/>. A named handler
    /// (not a lambda) so <see cref="OnScrollViewerLoaded"/> can unsubscribe it on re-host and stay
    /// idempotent.
    /// </summary>
    private void OnVerticalScrollBarValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        SetValue(VerticalOffsetProperty, e.NewValue);
    }

    /// <summary>
    /// Handles the Loaded event of the TableView control.
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_isItemsSourceSuspended) // indicates that the control was unloaded and loaded back
        {
            _headerRow?.CalculateHeaderWidths();  // Needed when switching back to an existing TableView (without provided column Widths)
        }

        ResumeItemsSource();
        EnsureAutoColumns();
        StartStallWatch();
    }

    /// <summary>
    /// Handles the Unloaded event of the TableView control.
    /// </summary>
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        EndDragSelection();
        StopAutoScroll();

        if (IsEditing && CurrentCellSlot.HasValue && GetCellFromSlot(CurrentCellSlot.Value) is { } currentCell)
        {
            currentCell.EndEditing(TableViewEditAction.Commit);
        }

        SuspendItemsSource();
    }

    /// <summary>
    /// Suspends subscriptions to the current items source while the control is unloaded.
    /// </summary>
    private void SuspendItemsSource()
    {
        if (_isItemsSourceSuspended)
        {
            return;
        }

        // FOBO fork: only the in-memory CollectionView gets suspended. Custom
        // ITableViewItemsSource implementations own their lifecycle — clearing
        // Source would wipe a SQL-backed view, and Resume would self-assign
        // (ItemsSource IS the source instance on that path).
        // `is not CollectionView _` — bare type name resolves against the public
        // CollectionView property here (same trick as ItemsSourceChanged).
        if (_collectionView is not CollectionView _)
        {
            return;
        }

        _collectionView.ItemPropertyChanged -= OnItemPropertyChanged;
        _collectionView.Source = Enumerable.Empty<object>();
        _isItemsSourceSuspended = true;
    }

    /// <summary>
    /// Restores subscriptions to the current items source when the control is loaded.
    /// </summary>
    private void ResumeItemsSource()
    {
        if (!_isItemsSourceSuspended)
        {
            return;
        }

        _collectionView.ItemPropertyChanged += OnItemPropertyChanged;

        if (ItemsSource is IEnumerable source)
        {
            _collectionView.Source = source;
        }

        _isItemsSourceSuspended = false;
    }

    /// <summary>
    /// Handles the PointerWheelChanged event of the ScrollContentPresenter.
    /// </summary>
    private void OnScrollContentPresenterPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        // Whether the reader is asking for anything at all. The offset cannot answer that: it only
        // moves when the thread gets to move it, so a held thread reports a still list whether the
        // wheel is turning or not. The events themselves are queued by the platform and delivered
        // when the thread frees up, so a count of them across a stalled frame IS the gesture.
        DiagWheelEvents++;

        var pointerPoint = e.GetCurrentPoint(this);
        var isShiftButton = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift);
        var isHorizontalScroll = isShiftButton || pointerPoint.Properties.IsHorizontalMouseWheel;

        if (isHorizontalScroll && _scrollViewer?.ComputedHorizontalScrollBarVisibility is Visibility.Visible)
        {
            e.Handled = true;
            var mouseWheelDelta = isShiftButton ? -pointerPoint.Properties.MouseWheelDelta : pointerPoint.Properties.MouseWheelDelta;
            var xOffset = HorizontalOffset + (mouseWheelDelta / 4.0);
            SetValue(HorizontalOffsetProperty, Math.Clamp(xOffset, 0, _scrollViewer.ScrollableWidth));
            return;
        }

        // A trackpad flick is not one big scroll, it is a few hundred small ones — and a scroll
        // smaller than a viewport makes the layouter WALK the list, realizing every row it passes.
        // At a few milliseconds a row that is a frame's whole budget spent several times over, so
        // the list stops dead until the flick's momentum runs out. (A scrollbar drag is smooth for
        // exactly the opposite reason: one delta bigger than a viewport, and the layouter jumps.)
        //
        // So the list travels no further per frame than it can draw, and what is left over is
        // carried into the next frame rather than dropped: the gesture arrives in full, a little
        // later, instead of arriving at once and freezing.
        //
        // Only for a flick. A wheel delivers a few dozen detents a second at most, and each one is a
        // couple of rows the panel can draw inside its frame — there is nothing to pace. Pacing it
        // anyway meant a wheel put pixels into a queue that drained at its own rate, so the list
        // carried on moving after the hand stopped, and every turn of the wheel was a scroll the
        // platform had not been allowed to perform. WheelGesture is what tells the two apart.
        if (MaxScrollRowsPerFrame > 0 && !isHorizontalScroll && _scrollViewer is not null)
        {
            if (IsFlickScroll(pointerPoint.Properties.MouseWheelDelta))
            {
                e.Handled = true;
                ScrollByPixels(-pointerPoint.Properties.MouseWheelDelta / WheelUnitsPerLine * LinePixels);
            }

            // A wheel is left to the ScrollViewer, unhandled: it scrolls by the platform's own
            // amount, immediately, and stops when the hand does.
        }
    }

    private readonly WheelGesture _wheelGesture = new();
    private long _lastWheelTimestamp;

    /// <summary>
    /// Whether the wheel event just delivered belongs to a gesture that has to be paced. The clock
    /// is this method's own — the gap between events is the whole of what
    /// <see cref="WheelGesture"/> needs from the outside, and taking it here keeps the decision
    /// itself testable without one.
    /// </summary>
    private bool IsFlickScroll(int delta)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var sinceLastMs = _lastWheelTimestamp == 0
            ? double.PositiveInfinity
            : (now - _lastWheelTimestamp) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        _lastWheelTimestamp = now;

        return _wheelGesture.Observe(delta, sinceLastMs);
    }

    /// <summary>
    /// The FEWEST rows a paced frame will travel, and the switch for pacing itself: zero leaves
    /// scrolling alone entirely.
    ///
    /// <para>It used to be the most, which is what made a paced list feel slow — a number chosen for
    /// the worst frame on the slowest content was then charged to every frame, including the ones
    /// that could have delivered ten times as much. What a frame may deliver is now measured
    /// (<see cref="_paceBudget"/>) and this is only the floor under it, so a list that cannot keep
    /// up still moves rather than stalling.</para>
    /// </summary>
    public double MaxScrollRowsPerFrame { get; set; }

    /// <summary>One wheel notch, as the platform reports it.</summary>
    private const double WheelUnitsPerLine = 40;

    /// <summary>What a notch is worth in pixels — three lines, as a wheel usually scrolls.</summary>
    private const double LinePixels = 16;

    /// <summary>The frame this is all trying to fit inside, in milliseconds.</summary>
    private const double TargetFrameMs = 16;

    /// <summary>How late a drain has to be before the frame it measures counts as overrun. A tick
    /// cannot arrive while the thread is laying out, so its lateness IS the cost of what the last
    /// one asked for.</summary>
    private const double OverrunFrameMs = TargetFrameMs * 1.5;

    /// <summary>How many screens of backlog the pacer will still try to pace through. Past this the
    /// gesture has outrun the list by more than pacing can recover, and the queue is taken a screen
    /// at a time instead — bounded, so no single frame costs more than one re-seed.</summary>
    private const double BacklogScreens = 2;

    /// <summary>What one frame is allowed to travel, in pixels, as it stands. Grows while frames
    /// come back on time and shrinks when they do not; clamped between the floor
    /// (<see cref="MaxScrollRowsPerFrame"/>) and one viewport every time it is used.</summary>
    private double _paceBudget = double.PositiveInfinity;

    private long _lastDrainAt;
    private double _pendingScroll;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _scrollDrainTimer;

    /// <summary>
    /// Queues a scroll of <paramref name="pixels"/> and lets it out at most one frame's worth at a
    /// time. Public so the harness can drive the same path a wheel does.
    /// </summary>
    public void ScrollByPixels(double pixels)
    {
        if (_scrollViewer is not { } sv)
        {
            return;
        }

        _pendingScroll += pixels;

        if (MaxScrollRowsPerFrame <= 0)
        {
            Drain();
            return;
        }

        if (_scrollDrainTimer is null && DispatcherQueue is not null)
        {
            _scrollDrainTimer = DispatcherQueue.CreateTimer();
            _scrollDrainTimer.Interval = TimeSpan.FromMilliseconds(16);
            _scrollDrainTimer.IsRepeating = true;
            _scrollDrainTimer.Tick += (_, _) => Drain();
        }

        _scrollDrainTimer?.Start();

        void Drain()
        {
            if (Math.Abs(_pendingScroll) < 0.5)
            {
                _pendingScroll = 0;
                _lastDrainAt = 0;
                _scrollDrainTimer?.Stop();
                return;
            }

            var pitch = _rows.FirstOrDefault(r => r.ActualHeight > 0)?.ActualHeight + 1 ?? RowHeight + 1;
            var budget = NextPaceBudget(pitch);

            // Two ways to take what is queued, and which applies is a question of how far behind the
            // gesture has left us.
            //
            // Within a couple of screens, the paced step. That is the mechanism doing its job.
            //
            // Past that, a screen at a time — not the whole backlog, and not three rows either. Both
            // extremes were tried and both are wrong. Taking it all was what the code did, and it
            // fired on every drain of every fling, so the budget decided nothing: the lines had it
            // converged on seventy-eight pixels while the step took nine hundred. Pacing through it
            // instead is worse in a way that is easier to feel than to measure — four thousand
            // pixels at seventy-eight a frame is the list still travelling three seconds after the
            // hand has stopped.
            //
            // A screen is the honest middle. It is what the panel re-seeds anyway once a step
            // crosses a viewport, so it is the largest step that costs no more than one re-seed, and
            // a backlog of any size clears in a handful of them.
            var screen = Math.Max(budget, sv.ViewportHeight);

            var step = Math.Abs(_pendingScroll) > screen * BacklogScreens
                ? Math.Sign(_pendingScroll) * screen
                : Math.Clamp(_pendingScroll, -budget, budget);
            var target = Math.Clamp(sv.VerticalOffset + step, 0, Math.Max(0, sv.ScrollableHeight));

            // Nothing left to give — at either end the rest of the gesture has nowhere to go.
            if (Math.Abs(target - sv.VerticalOffset) < 0.5)
            {
                _pendingScroll = 0;
                _lastDrainAt = 0;
                _scrollDrainTimer?.Stop();
                return;
            }

            _pendingScroll -= step;

            // Charged, and net of the layout it causes. What is left in this bucket is what the
            // scroll itself cost outside the grid's own measure and arrange.
            var drainT0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var drainLeaf0 = DiagLeafTicks();
            sv.ChangeView(null, target, null, disableAnimation: true);
            DiagDrains++;
            DiagDrainTicks += System.Diagnostics.Stopwatch.GetTimestamp() - drainT0 - (DiagLeafTicks() - drainLeaf0);
        }

        /// <summary>
        /// What this frame may travel, in pixels.
        ///
        /// <para>Three things decide it, and none of them is a number somebody picked. The CEILING
        /// is one viewport, because a frame cannot show more than a screen; a gesture that has run
        /// further ahead than that is not paced at all but taken in one step, which is the caller's
        /// business. The FLOOR is <see cref="MaxScrollRowsPerFrame"/>, so a list that cannot keep up
        /// still moves rather than stalling. Between them the budget is MEASURED: the drain timer
        /// cannot tick while the thread is laying out, so a late tick is the last frame's cost, and
        /// the budget backs off when frames overrun and opens up again when they stop.</para>
        ///
        /// <para>Which is the point of it. The number this replaced was one constant for every
        /// frame, and it had to be small enough for the worst of them — the deep rows, the cold
        /// page, the machine under load — so the other ninety-nine per cent were held to a crawl
        /// they had no reason to be. Cheap rows now reach the ceiling within a few frames.</para>
        /// </summary>
        double NextPaceBudget(double pitch)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();

            // Opened at the floor rather than at the ceiling. A budget that starts unbounded gives
            // the first frame of every gesture a whole viewport of rows to realize, which is the
            // most expensive frame there is — and it hands it out before a single frame has been
            // timed, so it is not a measurement, it is a guess that is wrong once per gesture.
            if (double.IsNaN(_paceBudget) || double.IsInfinity(_paceBudget))
            {
                _paceBudget = MaxScrollRowsPerFrame > 0 ? MaxScrollRowsPerFrame * pitch : pitch;
            }

            if (_lastDrainAt != 0)
            {
                var frameMs = (now - _lastDrainAt) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

                // Down by a factor, up by a row. Multiplicative both ways — which is what this was —
                // does not settle anywhere: one overrun takes a fifth off the budget and the two
                // cheap frames that follow put it all back, so the budget spends the whole gesture
                // oscillating around the ceiling and every third frame is a long one. The stall
                // lines said so plainly: a frame prepared six rows and took 41ms, or fifty-four and
                // took 149, with nothing in between.
                //
                // Additive increase against multiplicative decrease is the standard answer to
                // exactly this, and for the same reason: it converges on the largest budget the
                // content can actually deliver instead of repeatedly overshooting it.
                _paceBudget = frameMs > OverrunFrameMs
                    ? _paceBudget * 0.6
                    : _paceBudget + pitch;
            }

            _lastDrainAt = now;

            var floor = MaxScrollRowsPerFrame > 0 ? MaxScrollRowsPerFrame * pitch : pitch;
            var ceiling = Math.Max(floor, sv.ViewportHeight);

            _paceBudget = Math.Clamp(_paceBudget, floor, ceiling);
            return _paceBudget;
        }
    }

    /// <summary>
    /// Gets the next cell slot based on the current slot and input keys.
    /// </summary>
    private TableViewCellSlot GetNextSlot(TableViewCellSlot? currentSlot, bool isShiftKeyDown, bool isEnterKey)
    {
        var rows = Items.Count;
        var columns = Columns.VisibleColumns.Count;
        var currentRow = currentSlot?.Row ?? SelectedIndex;
        var currentColumn = currentSlot?.Column ?? -1;
        var nextRow = currentRow;
        var nextColumn = currentColumn;

        if (nextRow == -1 && nextColumn == -1)
        {
            nextRow = nextColumn = 0;
        }
        else if (isEnterKey)
        {
            nextRow += isShiftKeyDown ? -1 : 1;
            if (nextRow < 0)
            {
                nextRow = rows - 1;
                nextColumn = (nextColumn - 1 + columns) % columns;
            }
            else if (nextRow >= rows)
            {
                nextRow = 0;
                nextColumn = (nextColumn + 1) % columns;
            }
        }
        else
        {
            nextColumn += isShiftKeyDown ? -1 : 1;
            if (nextColumn < 0)
            {
                nextColumn = columns - 1;
                nextRow = (nextRow - 1 + rows) % rows;
            }
            else if (nextColumn >= columns)
            {
                nextColumn = 0;
                nextRow = (nextRow + 1) % rows;
            }
        }

        return new TableViewCellSlot(nextRow, nextColumn);
    }

    /// <summary>
    /// Copies the selected rows or cells content to the clipboard.
    /// </summary>
    internal void CopyToClipboardInternal(bool includeHeaders)
    {
        // Skip TableView copy logic when a cell editor already handles Ctrl+C.
        // TextBox, PasswordBox, and RichEditBox all implement their own copy behavior.
        // FocusManager.GetFocusedElement(XamlRoot) replaces the obsolete no-arg
        // overload (CS0618); guard XamlRoot in case we're called before the
        // control is attached to a visual tree.
        var focused = XamlRoot is { } xr
            ? FocusManager.GetFocusedElement(xr) as FrameworkElement
            : null;
        if (focused is TextBox or PasswordBox or RichEditBox)
        {
            return;
        }

        var args = new TableViewCopyToClipboardEventArgs(includeHeaders);
        OnCopyToClipboard(args);

        if (!CanCopy || args.Handled)
        {
            return;
        }

        var content = GetSelectedClipboardContent(includeHeaders);

        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        // Try/catch to prevent CLIPBRD_E_CANT_OPEN crashes.
        try
        {
            var package = new DataPackage();
            package.SetText(content);

            Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            // Clipboard failures are normal on Windows (e.g., CLIPBRD_E_CANT_OPEN).
            // Swallow to avoid crashing the application.
            Debug.WriteLine(
                $"TableView: Clipboard.SetContent failed: {ex}");
        }
    }

    /// <summary>
    /// Returns the selected cells' or rows' content as a string, optionally including headers, with values separated by the given character.
    /// </summary>
    /// <param name="includeHeaders">Whether to include headers in the output.</param>
    /// <param name="separator">The character used to separate cell values (default is tab).</param>
    /// <returns>A string of selected cell content separated by the specified character.</returns>
    public string GetSelectedContent(bool includeHeaders, char separator = '\t')
    {
        var slots = GetSelectedCellSlots();

        return GetCellsContent(slots, includeHeaders, separator);
    }

    /// <summary>
    /// Returns the selected cells' or rows' clipboard content as a string, optionally including headers, with values separated by the given character.
    /// </summary>
    /// <param name="includeHeaders">Whether to include headers in the output.</param>
    /// <param name="separator">The character used to separate cell values (default is tab).</param>
    /// <returns>A string of selected cell clipboard content separated by the specified character.</returns>
    public string GetSelectedClipboardContent(bool includeHeaders, char separator = '\t')
    {
        var slots = GetSelectedCellSlots();

        return GetCellsContent(slots, includeHeaders, separator, true);
    }

    private IEnumerable<TableViewCellSlot> GetSelectedCellSlots()
    {
        var slots = Enumerable.Empty<TableViewCellSlot>();

        if (SelectedItems.Any() || SelectedCells.Count != 0)
        {
            slots = SelectedRanges.SelectMany(x => Enumerable.Range(x.FirstIndex, (int)x.Length))
                                  .SelectMany(r => Enumerable.Range(0, Columns.VisibleColumns.Count)
                                                                     .Select(c => new TableViewCellSlot(r, c)))
                                  .Concat(SelectedCells)
                                  .OrderBy(x => x.Row)
                                  .ThenByDescending(x => x.Column);
        }
        else if (CurrentCellSlot.HasValue)
        {
            slots = [CurrentCellSlot.Value];
        }

        return slots;
    }

    /// <summary>
    /// Returns all the cells' content as a string, optionally including headers, with values separated by the given character.
    /// </summary>
    /// <param name="includeHeaders">Whether to include headers in the output.</param>
    /// <param name="separator">The character used to separate cell values (default is tab).</param>
    /// <returns>A string of all cell content separated by the specified character.</returns>
    public string GetAllContent(bool includeHeaders, char separator = '\t')
    {
        var rows = Enumerable.Range(0, Items.Count).ToArray();

        return GetRowsContent(rows, includeHeaders, separator);
    }

    /// <summary>
    /// Returns specified rows' content as a string, optionally including headers, with values separated by the given character.
    /// </summary>
    /// <param name="rows">Row indexes to get content for.</param>
    /// <param name="includeHeaders">Whether to include headers in the output.</param>
    /// <param name="separator">The character used to separate cell values.</param>
    /// <returns>A string of specified row content separated by the specified character.</returns>
    public string GetRowsContent(int[] rows, bool includeHeaders, char separator = '\t')
    {
        var slots = rows.SelectMany(r => Enumerable.Range(0, Columns.VisibleColumns.Count)
                                                           .Select(c => new TableViewCellSlot(r, c)))
                        .OrderBy(x => x.Row)
                        .ThenByDescending(x => x.Column);

        return GetCellsContent(slots, includeHeaders, separator);
    }

    /// <summary>
    /// Returns specified cells' content as a string, optionally including headers, with values separated by the given character.
    /// </summary>
    /// <param name="slots">Cell slots to get content for.</param>
    /// <param name="includeHeaders">Whether to include headers in the output.</param>
    /// <param name="separator">The character used to separate cell values.</param>
    /// <returns>A string of specified cell content separated by the specified character.</returns>
    public string GetCellsContent(IEnumerable<TableViewCellSlot> slots, bool includeHeaders, char separator = '\t')
    {
        return GetCellsContent(slots, includeHeaders, separator, false);
    }

    private string GetCellsContent(IEnumerable<TableViewCellSlot> slots, bool includeHeaders, char separator, bool isClipboardContent)
    {
        if (!slots.Any())
        {
            return string.Empty;
        }

        var minColumn = slots.Select(x => x.Column).Min();
        var maxColumn = slots.Select(x => x.Column).Max();
        var stringBuilder = new StringBuilder();

        if (includeHeaders)
        {
            stringBuilder.Append(GetHeadersContent(separator, minColumn, maxColumn));
            stringBuilder.Append('\n');
        }

        foreach (var row in slots.Select(x => x.Row).Distinct())
        {
            var item = Items[row];

            for (var col = minColumn; col <= maxColumn; col++)
            {
                if (Columns.VisibleColumns[col] is not TableViewColumn column ||
                   !slots.Contains(new TableViewCellSlot(row, col)))
                {
                    stringBuilder.Append(separator);
                    continue;
                }

                var content = isClipboardContent ? column.GetClipboardContent(item) : column.GetCellContent(item);
                stringBuilder.Append($"{content}{separator}");
            }

            stringBuilder.Remove(stringBuilder.Length - 1, 1); // remove extra separator at the end of the line
            stringBuilder.Append('\n');
        }

        stringBuilder.Remove(stringBuilder.Length - 1, 1); // remove extra line at the end

        return stringBuilder.ToString();
    }

    /// <summary>
    /// Returns all headers content as a string with values separated by the given character.
    /// </summary>
    /// <param name="separator">The character used to separate cell values.</param>
    /// <param name="minColumn">Min index column.</param>
    /// <param name="maxColumn">Max index column.</param>
    /// <returns>A string of all headers content separated by the specified character.</returns>
    private string GetHeadersContent(char separator, int minColumn, int maxColumn)
    {
        var stringBuilder = new StringBuilder();
        for (var col = minColumn; col <= maxColumn; col++)
        {
            var column = Columns.VisibleColumns[col];
            stringBuilder.Append($"{column.Header}{separator}");
        }

        stringBuilder.Remove(stringBuilder.Length - 1, 1); // remove extra separator at the end of the line

        return stringBuilder.ToString();
    }

    /// <summary>
    /// Generates columns based on the types of the properties of the ItemsSource collection type.
    /// </summary>
    private void GenerateColumns()
    {
        if (ItemsSource is not IEnumerable source) return;

        var dataType = source?.GetItemType();
        if (dataType is null || dataType.IsPrimitive())
        {
            var columnArgs = GenerateColumn(dataType, null, "", dataType?.IsInheritedFromIComparable() is true);
            OnAutoGeneratingColumn(columnArgs);

            if (!columnArgs.Cancel && columnArgs.Column is not null)
            {
                Columns.Insert(Columns.Count, columnArgs.Column);
            }
        }
        else
        {
            foreach (var propertyInfo in dataType.GetProperties())
            {
                var displayAttribute = propertyInfo.GetCustomAttributes().OfType<DisplayAttribute>().FirstOrDefault();
                var autoGenerateField = displayAttribute?.GetAutoGenerateField();
                if (autoGenerateField == false)
                {
                    continue;
                }

                var header = displayAttribute?.GetShortName() ?? propertyInfo.Name;
                var canFilter = displayAttribute?.GetAutoGenerateFilter() is true or null;
                var columnArgs = GenerateColumn(propertyInfo.PropertyType, propertyInfo.Name, header, canFilter);
                OnAutoGeneratingColumn(columnArgs);

                if (!columnArgs.Cancel && columnArgs.Column is not null)
                {
                    columnArgs.Column.Order = displayAttribute?.GetOrder();
                    Columns.Add(columnArgs.Column);
                }
            }
        }
    }

    /// <summary>
    /// Generates a column based on the property type.
    /// </summary>
    private static TableViewAutoGeneratingColumnEventArgs GenerateColumn(Type? propertyType, string? propertyName, string header, bool canFilter)
    {
        var newColumn = GetTableViewColumnFromType(propertyName, propertyType);
        newColumn.Header = header;
        newColumn.CanFilter = canFilter;
        newColumn.IsAutoGenerated = true;

        return new TableViewAutoGeneratingColumnEventArgs(propertyName!, propertyType, newColumn);
    }

    /// <summary>
    /// Gets a TableViewColumn based on the property type.
    /// </summary>
    private static TableViewBoundColumn GetTableViewColumnFromType(string? propertyName, Type? type)
    {
        var binding = new Binding { Path = new PropertyPath(propertyName), Mode = BindingMode.TwoWay };
        TableViewBoundColumn column = new TableViewTextColumn { Binding = binding };

        if (type is null)
        {
            return column;
        }
        else if (type.IsTimeSpan() || type.IsTimeOnly())
        {
            column = new TableViewTimeColumn();
        }
        else if (type.IsDateOnly() || type.IsDateTime() || type.IsDateTimeOffset())
        {
            column = new TableViewDateColumn();
        }
        else if (type.IsNumeric())
        {
            column = new TableViewNumberColumn();
        }
        else if (type.IsBoolean())
        {
            column = new TableViewCheckBoxColumn();
        }
        else if (type.IsUri())
        {
            column = new TableViewHyperlinkColumn();
        }

        column.Binding = binding;

        return column;
    }

    /// <summary>
    /// Handles the ItemsSource property changed event.
    /// </summary>
    /// <remarks>
    /// FOBO fork: when the caller hands us an
    /// <see cref="ITableViewItemsSource"/> directly we replace the
    /// in-memory <see cref="CollectionView"/> slot with it instead of
    /// wrapping it. That preserves whatever virtualization strategy
    /// the custom source implements (SQL paging, async loading, etc.)
    /// — wrapping it in a <see cref="CollectionView"/> would force
    /// every row to materialise during the wrapper's source-changed
    /// scan, which defeats the point.
    ///
    /// If the caller later sets ItemsSource back to a plain
    /// <see cref="IEnumerable"/> we restore the default in-memory
    /// source so the original behaviour is fully reversible.
    /// </remarks>
    private void ItemsSourceChanged(DependencyPropertyChangedEventArgs e)
    {
        DetailsPaneStates.Clear();

        // A new items source invalidates any selection — drop logical all-selected
        // + exclusions so the flag can't carry over to unrelated data.
        ClearAllSelectedState();

        // Custom source: take it directly and stop here.
        if (e.NewValue is ITableViewItemsSource customSource &&
            !ReferenceEquals(customSource, _collectionView))
        {
            SwapItemsSource(customSource);
            EnsureAutoColumns();
            return;
        }

        // Fall-through: default in-memory wrapping. If we previously
        // swapped to a custom source, swap back to a fresh
        // CollectionView so the existing Source-based path applies.
        // (`is not CollectionView _` rather than `is not CollectionView`
        // because TableView also exposes a public CollectionView property,
        // which makes the bare type name resolve as a constant pattern
        // here.)
        if (_collectionView is not CollectionView _)
        {
            SwapItemsSource(new CollectionView());
        }

        using (_collectionView.DeferRefresh())
        {
            _collectionView.Source = null!;

            if (e.NewValue is IEnumerable source)
            {
                EnsureAutoColumns();

                if (!_isItemsSourceSuspended)
                {
                    _collectionView.Source = source;
                }
            }
        }

    }

#if !WINDOWS
    // FOBO fork, Uno-only. The in-memory CollectionView deliberately raises ONLY the WinRT
    // IObservableVector.VectorChanged event (its INotifyCollectionChanged add/remove is a
    // no-op — see CollectionView.Events.cs). Windows' ListView consumes VectorChanged, but
    // Uno's ListView ignores it from a custom ICollectionView: with the ctor-assigned
    // instance reused across source changes, nothing ever invalidates item realization —
    // Items.Count is right, headers render, yet zero TableViewRows appear, and later
    // in-place mutations (e.g. a tree-grid expand/collapse inserting/removing rows) do
    // nothing visually. Cheapest reliable remedy: coalesce every VectorChanged burst into
    // one re-point of base.ItemsSource on the dispatcher, which forces Uno's ListView to
    // re-read the source. Custom ITableViewItemsSource implementations get the same hookup
    // via SwapItemsSource.
    private bool _unoRefreshQueued;

    // FIX B: a dispatcher-tick burst made up purely of CollectionChange.ItemChanged (the
    // virtual tree source raises up to a page of these when a fetched leaf page lands — see
    // VirtualTreeItemsSource.OnModelItemReplaced) is patched in place instead of re-pointing
    // base.ItemsSource. A full re-point recycles the whole viewport and re-realizes every
    // container, which is the direct cause of the "rows stay empty too long" jank. Anything
    // structural in the burst (Reset / insert / remove), or a burst larger than the cap, still
    // forces the full re-point.
    private bool _unoBurstNeedsRebind;
    private bool _unoShiftReseat;
    private readonly HashSet<int> _unoChangedIndices = new();
    private const int UnoInPlacePatchCap = 512;

    private void HookUnoVectorChanged(ITableViewItemsSource source)
        => source.VectorChanged += OnUnoVectorChanged;

    private void UnhookUnoVectorChanged(ITableViewItemsSource source)
        => source.VectorChanged -= OnUnoVectorChanged;

    private void OnUnoVectorChanged(object? sender, IVectorChangedEventArgs e)
    {
        // How much the source is SAYING, as against how much of it is worth hearing. A paged source
        // raises one of these per row in the page it just fetched — two hundred for a viewport of
        // twenty — and each one arrives on the UI thread whether the row is realized or not.
        DiagRowEvents++;
        if (e.CollectionChange is CollectionChange.Reset)
        {
            DiagSourceResets++;
        }

        // Accumulate the burst's change kinds until the dispatcher drains it.
        if (_unoBurstNeedsRebind)
        {
            // already committed to a rebind — no need to track individual indices
        }
        else if (e.CollectionChange is CollectionChange.ItemChanged)
        {
            _unoChangedIndices.Add((int)e.Index);
            if (_unoChangedIndices.Count > UnoInPlacePatchCap)
            {
                _unoBurstNeedsRebind = true; // too many to patch cheaply — fall back to rebind
            }
        }
        else if (e.CollectionChange is CollectionChange.ItemInserted or CollectionChange.ItemRemoved)
        {
            // Small structural deltas (expand/collapse under the model's bulk threshold) do NOT
            // re-point the ItemsSource: rebuilding the whole panel to remove a handful of rows
            // is what produced the blank band (the rebuild loses the scroll anchor). Instead
            // every realized row from the change point on is re-seated in place — contents
            // shift by the delta, unrealized rows read through on realization. The extent
            // estimate lags by |delta| rows until the next Reset; invisible next to the band.
            _unoShiftReseat = true;
        }
        else
        {
            _unoBurstNeedsRebind = true; // Reset: structural beyond repair, re-point

            // The reader's place is remembered and the offset goes to the top NOW, synchronously,
            // before anything can lay out against it.
            //
            // It cannot wait for the rebind on the dispatcher. A layout pass in between finds the
            // panel parked at an offset belonging to the list that has just been replaced, and
            // Uno's layouter fills line by line from its seed towards that position, measuring a
            // whole row of cells for every line on the way — from the top of a twenty-thousand-row
            // list to half a million pixels down it. That is the interface freezing solid on a
            // keystroke, and the further down the list the search box was reached for, the longer
            // it freezes.
            //
            // Whether the place is worth remembering depends on what kind of Reset this is, and
            // the size of the list answers that. A rebuild that changed nothing about WHICH rows
            // there are — the skeleton rebuilt after a metadata edit, a re-sort, a page landing —
            // is the same list, and the reader is put back where they were. A search box typed
            // into, a filter applied, ten thousand duplicates removed: the reader's place was not
            // moved by that, it was destroyed by it, and the top is the honest answer.
            //
            // Restoring a place in a list that changed wholesale is not merely wrong, it is a
            // hang: the row somebody was looking at can still exist and still be thousands of
            // rows from where it was, so aiming at it lands the panel in the same line-by-line
            // fill this clamp exists to prevent.
            var count = Items?.Count ?? 0;
            var sameList = _unoKnownCount > 0 && Math.Abs(count - _unoKnownCount) * 10 <= _unoKnownCount;

            if (sameList)
            {
                CaptureReanchorState();
            }
            else
            {
                _unoReanchorOffset = _scrollViewer?.VerticalOffset ?? 0;
                _unoReanchorCandidates.Clear();
            }
            _unoReanchorCaptured = true;
            _unoKnownCount = count;

            if (_unoReanchorOffset > 0 && _scrollViewer is { } sv)
            {
                if (ReanchorTrace)
                {
                    Console.WriteLine($"[reset] clamping from offset={_unoReanchorOffset:F0} "
                        + $"count={count} sameList={sameList}");
                }

                sv.ChangeView(null, 0, null, disableAnimation: true);

                if (!sameList)
                {
                    _unoReanchorOffset = 0;   // nothing to go back to
                }
            }
        }

        if (_unoRefreshQueued)
        {
            return;
        }

        _unoRefreshQueued = true;
        DispatcherQueue?.TryEnqueue(() =>
        {
            _unoRefreshQueued = false;

            if (_unoBurstNeedsRebind)
            {
                _unoBurstNeedsRebind = false;
                _unoChangedIndices.Clear();
                DiagCharge(ref DiagRebindTicks, ref DiagRebinds, RebindBaseItemsSource);
                return;
            }

            var changedIndices = new HashSet<int>(_unoChangedIndices);
            _unoChangedIndices.Clear();
            DiagCharge(ref DiagPatchTicks, ref DiagPatches, () => PatchRealizedRows(changedIndices));

            if (_unoShiftReseat)
            {
                _unoShiftReseat = false;
                DiagCharge(ref DiagReseatTicks, ref DiagReseats, ReseatPanelRows);
                ItemsPanelRoot?.InvalidateMeasure();
            }

            if (_unoReseatBurstsLeft > 0)
            {
                _unoReseatBurstsLeft--;
                DiagCharge(ref DiagReseatTicks, ref DiagReseats, ReseatPanelRows);
            }
        });
    }

    // Uno's ItemsStackPanel re-realizes a re-pointed source from ITEM 0 while the ScrollViewer
    // keeps its old offset: every container lands above the viewport and the grid looks empty
    // (the "disappearing rows" / blank band after a collapse while scrolled down). The panel
    // only re-anchors to the offset on a VIEW CHANGE, and its rebuild finishes asynchronously
    // well after the re-point — so the position is aimed at again from the container prepares
    // the rebuild produces (see TryUnoReanchorRestore), not once and hopefully.
    //
    /// <summary>Where the reader was before the Reset clamped the offset to the top. Zero once
    /// there is nothing left to aim at.</summary>
    private double _unoReanchorOffset;

    private static readonly bool ReanchorTrace = Environment.GetEnvironmentVariable("TREEGRID_TRACE") == "1";

    /// <summary>Remembers where the reader was: the offset, and the items the viewport was showing
    /// (top-down). Whichever of those items still resolves to an index after the re-point anchors
    /// the viewport again — a rebuilt tree hands back all-new objects, so the item's own identity
    /// is no help and the source is asked to place it (see <see cref="ResolveAnchorIndex"/>).
    /// Taken at the Reset, before the offset is clamped, because by rebind time it is gone.</summary>
    private void CaptureReanchorState()
    {
        _unoReanchorOffset = _scrollViewer?.VerticalOffset ?? 0;

        _unoReanchorCandidates.Clear();
        if (_unoReanchorOffset > 0 && _scrollViewer is { } svA && ItemsPanelRoot is { } panel)
        {
            // The PANEL's children, not _rows: the layouter's large-scroll recovery recycles
            // containers without re-preparing them, and _rows only tracks what has been prepared.
            foreach (var row in panel.Children.OfType<TableViewRow>()
                .Where(r => r.ActualHeight > 0 && r.Content is not null)
                .Select(r => { try { return (Row: r, Y: r.TransformToVisual(svA).TransformPoint(new Point(0, 0)).Y); } catch (ArgumentException) { return (Row: r, Y: double.NaN); } })
                .Where(t => !double.IsNaN(t.Y) && t.Y > -t.Row.ActualHeight && t.Y < svA.ViewportHeight)
                .OrderBy(t => t.Y))
            {
                _unoReanchorCandidates.Add(row.Row.Content);
            }
        }

        if (ReanchorTrace)
        {
            Console.WriteLine($"[capture] offset={_unoReanchorOffset:F0} "
                + $"candidates={_unoReanchorCandidates.Count}");
        }
    }

    /// <summary>Set when the Reset already captured the reader's place — the rebind that follows
    /// must not capture again, because the offset it would read is the clamped zero.</summary>
    private bool _unoReanchorCaptured;

    /// <summary>How many rows the grid was showing before this Reset. Read at the Reset, when
    /// <see cref="Items"/> already reports the NEW list, so the two cannot be compared there;
    /// this is the last count the grid actually bound to.</summary>
    private int _unoKnownCount;

    private void RebindBaseItemsSource()
    {
        if (!_unoReanchorCaptured)
        {
            CaptureReanchorState();
        }
        _unoReanchorCaptured = false;

        _allowInternalBaseItemsSourceSet = true;
        try
        {
            base.ItemsSource = null;
            base.ItemsSource = _collectionView;
        }
        finally
        {
            _allowInternalBaseItemsSourceSet = false;
        }

        if (ReanchorTrace)
                Console.WriteLine($"[reanchor] rebind offset={_unoReanchorOffset:F0} sv={(_scrollViewer is not null)} "
                    + $"candidates={_unoReanchorCandidates.Count} anchor={ResolveVisibleAnchorIndex()}");

        if (_unoReanchorOffset > 0 && _scrollViewer is not null)
        {
            // Only a row that the new list can still place is worth aiming at. An edit, a re-sort,
            // a page landing: the rows the viewport was showing are all still there, and the
            // position is restored to them. A search box typed into, a filter applied, ten
            // thousand duplicates removed: not one of them is, and the reader's place was not
            // moved by the change but destroyed by it — the top, where the offset already sits,
            // is the honest answer and the cheap one.
            if (ResolveVisibleAnchorIndex() < 0)
            {
                _unoReanchorOffset = 0;
                _unoReanchorCandidates.Clear();
                return;
            }

            _unoReanchorPending = true;
            _unoReanchorPrepares = 0;
        }
    }

    private bool _unoReanchorPending;
    private int _unoReanchorPrepares;

    /// <summary>Where the topmost row the viewport was showing lives in the list now, or -1 when
    /// the new list can place none of them.</summary>
    private int ResolveVisibleAnchorIndex()
    {
        foreach (var item in _unoReanchorCandidates)
        {
            var index = ResolveAnchorIndex(item);
            if (index >= 0)
            {
                return index;
            }
        }
        return -1;
    }

    /// <summary>Restores the scroll position after a rebind, driven by container prepares —
    /// the only post-rebind signal that provably fires. The rebind momentarily shrinks the
    /// extent, so the ScrollViewer CLAMPS the offset (deep positions land near 0): the position
    /// is genuinely lost, not stale. A plain ChangeView back to the anchor-derived target trips
    /// the layouter's own large-scroll recovery (ClearLines + reseed + rebuild), which
    /// re-materializes correctly; retried every few prepares until the offset verifiably sits
    /// at the target (the extent may still be growing, re-clamping early attempts).</summary>
    private readonly List<object> _unoReanchorCandidates = new();

    /// <summary>
    /// The index of a row the viewport was showing before the source was re-pointed.
    ///
    /// <para>Plain IndexOf is right whenever the item is still in the list. It is useless against a
    /// source that REBUILT — a virtualized tree grouped by its data hands back all-new objects, so
    /// every captured row resolves to -1 and the re-anchor gives up. Such a source can place a stale
    /// row itself, and is asked first.</para>
    /// </summary>
    private int ResolveAnchorIndex(object? item)
    {
        if (item is null)
            return -1;
        if (ItemsSource is VirtualTreeItemsSource tree)
            return tree.ResolveAnchor(item);
        return Items.IndexOf(item);
    }
    private int _unoReseatBurstsLeft;
    private double _unoFinalHopTarget = -1;

    /// <summary>Re-seats every panel child whose content no longer matches the source at its
    /// index — walks the panel's children, NOT _rows, because recovery-recycled containers are
    /// missing from _rows until something re-prepares them.</summary>
    private void ReseatPanelRows()
    {
        if (ItemsPanelRoot is not { } panel)
        {
            return;
        }
        foreach (var child in panel.Children)
        {
            if (child is not TableViewRow row)
            {
                continue;
            }
            var index = row.Index;
            if (index < 0 || index >= Items.Count)
            {
                continue;
            }
            var item = Items[index];
            if (item is not null && !ReferenceEquals(row.Content, item))
            {
                row.DataContext = item;
                row.Content = item;
            }
        }
    }

    /// <summary>
    /// What one row occupies, for turning a row index into a scroll offset.
    ///
    /// <para>From the panel's OWN extent first, because that is what the offset is being aimed at:
    /// a caller wants to land where the panel will put the row, and the panel places rows against
    /// the extent it is publishing, not against any row's measurement. Sampling one container
    /// instead is off by whatever that container includes and the placement does not — a 22px grid
    /// laid out at 23px per row samples a row at 23, adds the customary border, and aims at 24,
    /// which is a row of drift for every row above the target (measured: 12,000px, ~520 rows, on a
    /// restore twelve thousand rows down).</para>
    ///
    /// <para>The measured row and then <see cref="RowHeight"/> are the fallbacks, for before there
    /// is an extent to read, and for while the extent is still an underestimate — a panel part-way
    /// through its first fill publishes an extent worth a few screens, and dividing that by the
    /// whole count gives a pitch shorter than a row, which would aim every offset near the top. A
    /// pitch below the grid's own row height is the tell.</para>
    ///
    /// <para>A grid with mixed row heights gets an average out of this and should provide
    /// <see cref="RowOffsetOfIndex"/>, which is exact.</para>
    /// </summary>
    private double EstimatedRowPitch()
    {
        var count = Items?.Count ?? 0;
        if (count > 0 && _scrollViewer is { ExtentHeight: > 0 } sv)
        {
            var fromExtent = sv.ExtentHeight / count;
            if (double.IsNaN(RowHeight) || fromExtent >= RowHeight)
            {
                return fromExtent;
            }
        }

        return _rows.FirstOrDefault(r => r.ActualHeight > 0)?.ActualHeight + 1
               ?? (double.IsNaN(RowHeight) ? 41 : RowHeight + 1);
    }

    /// <summary>Set between asking for a restore and the restore running, so the several signals
    /// that all mean "the rebuild moved on" queue one attempt between them and not one each.</summary>
    private bool _unoRestoreQueued;

    /// <summary>
    /// Asks for a restore attempt, on a turn of the loop of its own.
    ///
    /// <para>Both of the signals this rides arrive with a layout pass on the stack: a container
    /// prepare happens inside the panel's fill, and a view change is raised from inside the pass
    /// that changed the view. Moving the scroll offset from either one moves it underneath a fill
    /// in flight, and that fill does not start again at the new place — it carries on, walking
    /// every row between the two, building a container and a template for each. Over a screen
    /// nobody notices. Over a rebuild that restores a reader twelve thousand rows down, or a reveal
    /// that follows a re-filed track to the far end of a queue, it is a measure pass that does not
    /// return: measured at 429 MB/s of template garbage, half of every second in collections, and
    /// an audio buffer emptying because of it.</para>
    ///
    /// <para>So the attempt is posted instead. The restore is not losing anything by waiting: it
    /// re-arms on every prepare and every view change, and the one it takes is the one where the
    /// panel is between passes and a move is a jump.</para>
    /// </summary>
    private void TryUnoReanchorRestore()
    {
        if (_scrollViewer is null)
        {
            _unoReanchorPending = false;
            return;
        }

        if (_unoRestoreQueued)
        {
            return;
        }

        _unoRestoreQueued = true;
        if (DispatcherQueue?.TryEnqueue(RunUnoReanchorRestore) is not true)
        {
            // No queue to post to — better a restore that risks the walk than no restore at all.
            RunUnoReanchorRestore();
        }
    }

    private void RunUnoReanchorRestore()
    {
        _unoRestoreQueued = false;

        if (!_unoReanchorPending)
        {
            return;
        }

        if (_scrollViewer is not { } sv)
        {
            _unoReanchorPending = false;
            return;
        }

        var anchorIndex = ResolveVisibleAnchorIndex();

        // Nothing the viewport was showing survives in this list, so there is no place to restore
        // and whatever the offset already sits at is the honest answer. What this used to do
        // instead was aim at the offset from the PREVIOUS list and keep aiming: every ChangeView
        // triggers prepares, every prepare comes back here, and the target is never reached
        // because the extent is still growing underneath it — so it ran to the four-hundred-round
        // cap, each round realizing a whole viewport of cells.
        if (anchorIndex < 0)
        {
            _unoReanchorPending = false;
            _unoReanchorCandidates.Clear();
            ReseatPanelRows();
            _unoReseatBurstsLeft = 5;
            return;
        }

        var pitch = EstimatedRowPitch();
        // Mixed row heights (RowHeightSelector) make index × pitch wrong by the accumulated
        // difference above the anchor — the host's offset function is exact where provided.
        var target = RowOffsetOfIndex?.Invoke(anchorIndex) ?? anchorIndex * pitch;
        target = Math.Min(target, Math.Max(0, sv.ScrollableHeight));

        if (ReanchorTrace)
                Console.WriteLine($"[reanchor] restore n={_unoReanchorPrepares} anchorIndex={anchorIndex} target={target:F0} offset={sv.VerticalOffset:F0} scrollable={sv.ScrollableHeight:F0}");

        if (Math.Abs(sv.VerticalOffset - target) <= 2 || _unoReanchorPrepares > 400)
        {
            _unoReanchorPending = false;
            _unoReanchorCandidates.Clear();

            // The large-scroll recovery recycles containers WITHOUT re-preparing them: they
            // hold stale content, get no ItemChanged (the swap event fired long ago for
            // cached pages), and are absent from _rows (recycling removed them, no prepare
            // re-added them). Re-seat from the PANEL's actual children, and stay armed for
            // the next few fill bursts — pages fetched for the restored viewport land later
            // and their patch pass misses these same unlisted containers.
            ReseatPanelRows();
            _unoReseatBurstsLeft = 5;

            // Finish with the proven cure at the one moment it reliably works (extent settled,
            // offset verified): a 1px hop whose ViewChanged forces the panel to re-realize the
            // viewport through real prepares — recovery-recycled containers with stale content
            // get re-prepared with the current source rows. The hop is restored by the
            // ViewChanged handler below.
            _unoFinalHopTarget = target;
            sv.ChangeView(null, Math.Max(0, target - pitch), null, disableAnimation: true);
            return;
        }

        sv.ChangeView(null, target, null, disableAnimation: true);
    }

    // FIX B: refresh only the realized rows whose item object was replaced. Unrealized indices
    // need nothing — Uno's ListView reads the item through the IList indexer when it realizes a
    // container, so it picks up the new object then (this in-place path relies on that
    // read-through realization). For realized rows we re-seat the recycled container on the new
    // object: DataContext is the source the cells' bindings resolve against, and setting Content
    // drives OnContentChanged (which regenerates template-column cells).
    private void PatchRealizedRows(HashSet<int> changedIndices)
    {
        // One pass over the realized rows, not one _rows scan per changed index: row.Index
        // resolves through IndexFromContainer, and a landing page is a burst of up to 512
        // indices against a whole viewport of realized rows. Unrealized indices need nothing —
        // read-through realization covers them.
        foreach (var row in _rows)
        {
            var index = row.Index;
            if (index < 0 || index >= Items.Count || !changedIndices.Contains(index))
            {
                continue;
            }

            var item = Items[index];
            if (ReferenceEquals(row.Content, item))
            {
                continue;
            }

            row.DataContext = item;
            row.Content = item;
        }
    }
#endif

    /// <summary>
    /// FOBO fork: swap the active items-source. Detaches event handlers
    /// from the old source, re-points the underlying ListView at the
    /// new one, and re-attaches handlers. Called by
    /// <see cref="ItemsSourceChanged"/> when the host transitions
    /// between in-memory and custom sources.
    ///
    /// <para>
    /// The base.ItemsSource assignment is bracketed by
    /// <see cref="_allowInternalBaseItemsSourceSet"/> so the upstream
    /// guard in <c>OnBaseItemsSourceChanged</c> doesn't throw.
    /// </para>
    /// </summary>
    private void SwapItemsSource(ITableViewItemsSource newSource)
    {
        _collectionView.ItemPropertyChanged -= OnItemPropertyChanged;
        _collectionView.VectorChanged -= OnItemsMoved;
        newSource.VectorChanged += OnItemsMoved;
        RowIndexGeneration++;   // a whole new list: every cached row index is about to be wrong
#if !WINDOWS
        UnhookUnoVectorChanged(_collectionView);
        HookUnoVectorChanged(newSource);
#endif
        _collectionView = newSource;
        _allowInternalBaseItemsSourceSet = true;
        try
        {
            base.ItemsSource = _collectionView;
        }
        finally
        {
            _allowInternalBaseItemsSourceSet = false;
        }
        _collectionView.ItemPropertyChanged += OnItemPropertyChanged;
#if !WINDOWS
        _unoKnownCount = _collectionView.Count;
#endif
    }


    /// <summary>
    /// Ensures that columns are automatically generated based on the current state of the control.
    /// </summary>
    private void EnsureAutoColumns(bool force = false)
    {
        if ((_ensureColumns || force) && IsLoaded && AutoGenerateColumns && ItemsSource is not null)
        {
            RemoveAutoGeneratedColumns();
            GenerateColumns();

            _ensureColumns = false;
        }
    }

    /// <summary>
    /// Removes auto-generated columns.
    /// </summary>
    private void RemoveAutoGeneratedColumns()
    {
        Columns.RemoveWhere(x => x.IsAutoGenerated);
    }

    /// <summary>
    /// Exports the selected rows or cells content to a CSV file.
    /// </summary>
    internal async void ExportSelectedToCSV()
    {
        var args = new TableViewExportContentEventArgs();
        OnExportSelectedContent(args);

        if (args.Handled)
        {
            return;
        }

        try
        {
            if (await GetStorageFile() is not { } file)
            {
                return;
            }

            var content = GetSelectedContent(true, ',');
            using var stream = await file.OpenStreamForWriteAsync();
            stream.SetLength(0);

            using var tw = new StreamWriter(stream);
            await tw.WriteAsync(content);
        }
        catch { }
    }

    /// <summary>
    /// Exports all rows content to a CSV file.
    /// </summary>
    internal async void ExportAllToCSV()
    {
        var args = new TableViewExportContentEventArgs();
        OnExportAllContent(args);

        if (args.Handled)
        {
            return;
        }

        try
        {
            if (await GetStorageFile() is not { } file)
            {
                return;
            }

            var content = GetAllContent(true, ',');
            using var stream = await file.OpenStreamForWriteAsync();
            stream.SetLength(0);

            using var tw = new StreamWriter(stream);
            await tw.WriteAsync(content);
        }
        catch { }
    }

    /// <summary>
    /// Gets a storage file for saving the CSV.
    /// </summary>
    private
#if !WINDOWS
    static
#endif
    async Task<StorageFile> GetStorageFile()
    {
        var savePicker = new FileSavePicker();
        savePicker.FileTypeChoices.Add("CSV (Comma delimited)", [".csv"]);
#if WINDOWS
        var hWnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId);
        WinRT.Interop.InitializeWithWindow.Initialize(savePicker, hWnd);
#endif

        return await savePicker.PickSaveFileAsync();
    }

    /// <summary>
    /// Refreshes the items view of the TableView.
    /// </summary>
    public void RefreshView()
    {
        DeselectAll();
        _collectionView.Refresh();
    }

    /// <summary>
    /// Refreshes the sorting applied to the items in the TableView.
    /// </summary>
    public void RefreshSorting()
    {
        DeselectAll();
        _collectionView.RefreshSorting();
    }

    /// <summary>
    /// Clears all sorting applied to the items.
    /// </summary>
    public void ClearAllSorting()
    {
        DeselectAll();
        SortDescriptions.Clear();

        foreach (var column in Columns.Where(c => c.SortDirection is not null))
        {
            column?.SortDirection = null;
        }
    }

    /// <summary>
    /// Clears all sorting applied to the items with event.
    /// </summary>
    internal void ClearAllSortingWithEvent()
    {
        var eventArgs = new TableViewClearSortingEventArgs();
        OnClearSorting(eventArgs);

        if (eventArgs.Handled)
        {
            return;
        }

        ClearAllSorting();
    }

    /// <summary>
    /// Clears all filters applied to the items.
    /// </summary>
    public void ClearAllFilters()
    {
        FilterHandler.ClearFilter(null);
    }

    /// <summary>
    /// Refreshes all applied filters.
    /// </summary>
    public void RefreshFilter()
    {
        DeselectAll();
        _collectionView.RefreshFilter();
    }

    /// <summary>
    /// Selects all rows or cells in the TableView.
    /// </summary>
    internal new void SelectAll()
    {
        if (IsEditing)
        {
            return;
        }

        if (SelectionUnit is TableViewSelectionUnit.Cell)
        {
            SelectAllCells();
            CurrentCellSlot = null;
        }
        else
        {
            switch (SelectionMode)
            {
                case ListViewSelectionMode.Single:
                    SelectedItem = Items.FirstOrDefault();
                    break;
                case ListViewSelectionMode.Multiple:
                case ListViewSelectionMode.Extended:
                    // Logical all-selected — O(1) flag, never materializes the
                    // virtualized source. Replaces SelectRange(0, Items.Count),
                    // which paged in + selected every row (hang on large data).
                    SelectAllLogical();
                    break;
            }
        }
    }

    /// <summary>
    /// Selects all cells in the TableView.
    /// </summary>
    private void SelectAllCells()
    {
        switch (SelectionMode)
        {
            case ListViewSelectionMode.Single:
                if (Items.Count > 0 && Columns.VisibleColumns.Count > 0)
                {
                    SelectedCellRanges.Clear();
                    SelectedCellRanges.Add([new TableViewCellSlot(0, 0)]);
                }
                break;
            case ListViewSelectionMode.Multiple:
            case ListViewSelectionMode.Extended:
                // FIX F: a cell-wise select-all materializes rows×columns slots. On a large
                // virtualized source (~1M rows) that HashSet build walks the whole source and
                // hangs the UI thread, so above a sane bound fall back to the O(1) logical row
                // select-all (IsAllSelected flag) instead of enumerating every cell.
                if (Items.Count > 100_000)
                {
                    SelectAllLogical();
                    return;
                }

                SelectedCellRanges.Clear();
                var selectionRange = new HashSet<TableViewCellSlot>();

                for (var row = 0; row < Items.Count; row++)
                {
                    for (var column = 0; column < Columns.VisibleColumns.Count; column++)
                    {
                        selectionRange.Add(new TableViewCellSlot(row, column));
                    }
                }
                SelectedCellRanges.Add(selectionRange);
                break;
        }

        OnCellSelectionChanged();
    }

    /// <summary>
    /// Deselects all rows or cells in the TableView.
    /// </summary>
    public void DeselectAll()
    {
        DeselectAllItems();
        DeselectAllCells();
    }

    /// <summary>
    /// Deselects all rows in the TableView.
    /// </summary>
    private void DeselectAllItems()
    {
        // Clear logical all-selected FIRST — under it SelectedRanges is empty, so
        // the early-return below would otherwise skip clearing the flag.
        var wasAllSelected = IsAllSelected;
        ClearAllSelectedState();

        if (!wasAllSelected && SelectedRanges.Count is 0) return;

        switch (SelectionMode)
        {
            case ListViewSelectionMode.Single:
                SelectedItem = null;
                break;
            case ListViewSelectionMode.Multiple:
            case ListViewSelectionMode.Extended:
                // Deselect ONLY the items that are actually selected — O(selected).
                // Previously this range-deselected [0, Items.Count), which walks
                // the entire (virtualized) source via the indexer per call —
                // paging in every row and hanging the app on large data sets even
                // when just a couple of rows were selected.
                ClearItemSelection();
                break;
        }
    }

    /// <summary>
    /// Clears the row selection in O(selected) by removing the items we already
    /// hold in <see cref="ListViewBase.SelectedItems"/> — never an index walk
    /// over <c>Items.Count</c> and never a re-fetch by index (which on a
    /// virtualized source returns placeholders for evicted pages and would also
    /// fail to remove the real item).
    /// </summary>
    private void ClearItemSelection()
    {
#if WINDOWS
        foreach (var item in SelectedItems.Cast<object>().ToArray())
        {
            SelectedItems.Remove(item);
        }
#else
        var removed = SelectedItems.Cast<object>().ToArray();
        if (removed.Length == 0) return;

        SetDisableRaiseSelectionChanged(true);
        SelectedItems.Clear();
        SelectedRanges.Clear();
        SetDisableRaiseSelectionChanged(false);

        InvokeSelectionChanged(removed, []);
#endif
    }

    /// <summary>
    /// FOBO fork. A plain (modifier-less) click must end with exactly the clicked row
    /// selected. On Windows the <c>SelectedIndex</c> assignment does that by itself; Uno's
    /// <c>Selector</c> ADDS to <c>SelectedItems</c> in Multiple/Extended mode instead of
    /// replacing, so each click grew the selection by one row. Clear explicitly first —
    /// O(selected) via <see cref="ClearItemSelection"/>, never an index walk.
    /// </summary>
    private void ClearSelectionBeforePlainClick(int row)
    {
#if !WINDOWS
        if (SelectedItems.Count > 0 && !(SelectedItems.Count == 1 && SelectedIndex == row))
        {
            ClearItemSelection();
        }
#endif
    }

    /// <summary>
    /// FOBO fork, Uno path. Called from <see cref="TableViewCell.OnPointerPressed"/>: a plain
    /// (no ctrl, no shift) press starts a NEW selection, whether it turns out to be a click or
    /// a drag-select. The click-path clear in <see cref="SelectRows"/> is not enough on Uno —
    /// once the pointer moves a few pixels the gesture recognizer treats it as a manipulation
    /// and never raises Tapped, so only <see cref="TableViewCell.OnManipulationDelta"/> runs,
    /// whose shift-semantics SelectRange ADDS to whatever was selected before.
    /// </summary>
    internal void OnSelectionDragStart(int rowIndex, bool ctrlDown)
    {
        if (SelectionMode is ListViewSelectionMode.Multiple or ListViewSelectionMode.Extended
            && !ctrlDown)
        {
            ClearSelectionBeforePlainClick(rowIndex);
        }
    }

    private void OnAnyPointerForModifiers(object sender, PointerRoutedEventArgs e)
        => LastPointerKeyModifiers = e.KeyModifiers;

    /// <summary>Modifier flags stamped on the most recent pointer event anywhere in the grid —
    /// the stale-proof source for pointer-driven selection (see the ctor hookup comment).</summary>
    internal VirtualKeyModifiers LastPointerKeyModifiers { get; private set; }

    internal bool IsPointerShiftDown => LastPointerKeyModifiers.HasFlag(VirtualKeyModifiers.Shift);

    internal bool IsPointerCtrlDown => LastPointerKeyModifiers.HasFlag(VirtualKeyModifiers.Control)
#if !WINDOWS
        // macOS: the multi-select click convention is Cmd-click, which surfaces as the
        // Windows modifier flag.
        || LastPointerKeyModifiers.HasFlag(VirtualKeyModifiers.Windows)
#endif
        ;

    /// <summary>
    /// Deselects all cells in the TableView.
    /// </summary>
    private void DeselectAllCells()
    {
        if (SelectedCellRanges.Count is 0) return;

        SelectedCellRanges.Clear();
        OnCellSelectionChanged();
        CurrentCellSlot = null;
    }

    /// <summary>
    /// Selects a row or cell based on the specified cell slot.
    /// </summary>
    internal void MakeSelection(TableViewCellSlot slot, bool shiftKey, bool ctrlKey = false)
    {
        if (!slot.IsValidRow(this))
        {
            return;
        }

        if (SelectionMode != ListViewSelectionMode.None)
        {
            ctrlKey = ctrlKey || SelectionMode is ListViewSelectionMode.Multiple;

            _suppressSelectionChangedCellClear = SelectionUnit is TableViewSelectionUnit.CellWithRow;
            var shouldSelectRows = SelectionUnit is TableViewSelectionUnit.Row
                || (SelectionUnit is TableViewSelectionUnit.CellWithRow && !slot.IsValidColumn(this))
                || (LastSelectionUnit is TableViewSelectionUnit.Row && slot.IsValidRow(this) && !slot.IsValidColumn(this))
                || (SelectionUnit is TableViewSelectionUnit.CellOrRow && slot.IsValidRow(this) && !slot.IsValidColumn(this));

            if (shouldSelectRows)
            {
                if (!ctrlKey)
                    DeselectAllCells();
                SelectRows(slot, shiftKey, ctrlKey);
                LastSelectionUnit = TableViewSelectionUnit.Row;
            }
            else
            {
                if (SelectionUnit is TableViewSelectionUnit.CellWithRow)
                {                    
                    SelectRows(slot, shiftKey, ctrlKey);
                }
                else if (!ctrlKey)
                {
                    DeselectAllItems();
                }

                SelectCells(slot, shiftKey, ctrlKey);
                LastSelectionUnit = TableViewSelectionUnit.Cell;
            }
        }
        else if (!IsReadOnly)
        {
            SelectionStartCellSlot = slot;
            CurrentCellSlot = slot;
        }
    }

    /// <summary>
    /// Selects rows based on the specified cell slot.
    /// </summary>
    private void SelectRows(TableViewCellSlot slot, bool shiftKey, bool ctrlKey)
    {
        // Clicks while logical all-selected:
        //  • Ctrl-click → toggle that one row's exclusion (O(1)), stay all-selected.
        //  • plain / shift click → collapse to an explicit selection, then proceed
        //    through the normal path below (which selects the clicked row/range).
        if (IsAllSelected)
        {
            if (ctrlKey && !shiftKey)
            {
                ToggleAllSelectionExclusion(slot.Row);
                if (slot.IsValid(this)) CurrentCellSlot = slot;
                return;
            }
            ClearAllSelectedState();
        }

        var selectionRange = SelectedRanges.FirstOrDefault(x => x.IsInRange(slot.Row));
        SelectionStartRowIndex ??= slot.Row;

        if (selectionRange is not null && ctrlKey && !shiftKey && (CurrentRowIndex != slot.Row || CurrentCellSlot == slot))
        {
            DeselectRange(new ItemIndexRange(slot.Row, 1));
        }
        else if ((!shiftKey && !ctrlKey && SelectedItems.Count <= 1) || SelectionMode is ListViewSelectionMode.Single)
        {
            ClearSelectionBeforePlainClick(slot.Row);
            SelectionStartRowIndex = CurrentRowIndex = SelectedIndex = slot.Row;
        }
        else if ((!ctrlKey && !shiftKey) || !(SelectionMode is ListViewSelectionMode.Multiple or ListViewSelectionMode.Extended))
        {
            ClearSelectionBeforePlainClick(slot.Row);
            SelectionStartRowIndex = CurrentRowIndex = SelectedIndex = slot.Row;
        }
        else if (SelectionMode is ListViewSelectionMode.Multiple or ListViewSelectionMode.Extended)
        {
            var min = Math.Min(SelectionStartRowIndex.Value, slot.Row);
            var max = Math.Max(SelectionStartRowIndex.Value, slot.Row);
            var newSelection = new ItemIndexRange(min, (uint)(max - min) + 1);

            if (!ctrlKey && newSelection.Length == 1)
            {
                SelectionStartRowIndex = CurrentRowIndex = SelectedIndex = slot.Row;
            }
            if (selectionRange?.LastIndex > newSelection.LastIndex)
            {
                var deselectRange = new ItemIndexRange(newSelection.LastIndex + 1, (uint)(selectionRange.LastIndex - newSelection.LastIndex));
                DeselectRange(deselectRange);
            }
            else if (selectionRange?.FirstIndex < newSelection.FirstIndex)
            {
                var deselectRange = new ItemIndexRange(selectionRange.FirstIndex, (uint)(newSelection.FirstIndex - selectionRange.FirstIndex));
                DeselectRange(deselectRange);
            }
            else if (selectionRange != newSelection)
            {
                SelectRange(newSelection);
            }
        }

        if (!IsReadOnly && slot.IsValid(this))
        {
            CurrentCellSlot = slot;
        }
        else
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                var row = await ScrollRowIntoView(slot.Row);
                row?.Focus(FocusState.Programmatic);
            });
        }
    }

    /// <summary>
    /// Selects cells based on the specified cell slot.
    /// </summary>
    private void SelectCells(TableViewCellSlot slot, bool shiftKey, bool ctrlKey)
    {
        if (!slot.IsValid(this))
        {
            return;
        }

        if (!ctrlKey || !(SelectionMode is ListViewSelectionMode.Multiple or ListViewSelectionMode.Extended))
        {
            if (SelectionUnit is TableViewSelectionUnit.CellWithRow)
            {
                DeselectAllCells();
            }
            else
            {
                DeselectAll();
            }
        }

        var selectionRange = (SelectionStartCellSlot is null ? null : SelectedCellRanges.LastOrDefault(x => SelectionStartCellSlot.HasValue && x.Contains(SelectionStartCellSlot.Value))) ?? [];

        if (ctrlKey && SelectionMode is ListViewSelectionMode.Multiple or ListViewSelectionMode.Extended)
        {
            selectionRange = SelectedCellRanges.SelectMany(x => x).ToHashSet();
            SelectedCellRanges.Clear();
        }
        else
        {
            SelectedCellRanges.Remove(selectionRange);
            selectionRange.Clear();
        }

        SelectionStartCellSlot ??= CurrentCellSlot;
        SelectionStartCellSlot ??= slot;

        if (shiftKey && SelectionMode is ListViewSelectionMode.Multiple or ListViewSelectionMode.Extended)
        {
            var currentSlot = SelectionStartCellSlot.Value;
            var startRow = Math.Min(slot.Row, currentSlot.Row);
            var endRow = Math.Max(slot.Row, currentSlot.Row);
            var startCol = Math.Min(slot.Column, currentSlot.Column);
            var endCol = Math.Max(slot.Column, currentSlot.Column);
            for (var row = startRow; row <= endRow; row++)
            {
                for (var column = startCol; column <= endCol; column++)
                {
                    var nextSlot = new TableViewCellSlot(row, column);
                    selectionRange.Add(nextSlot);
                    if (SelectedCellRanges.LastOrDefault(x => x.Contains(nextSlot)) is { } range)
                    {
                        range.Remove(nextSlot);
                    }
                }
            }
        }
        else
        {
            SelectionStartCellSlot = slot;
            selectionRange.Add(slot);

            if (SelectedCellRanges.LastOrDefault(x => x.Contains(slot)) is { } range)
            {
                range.Remove(slot);
            }
        }

        SelectedCellRanges.Add(selectionRange);
        OnCellSelectionChanged();
        CurrentCellSlot = slot;
    }

    /// <summary>
    /// Deselects the specified cell slot.
    /// </summary>
    internal void DeselectCell(TableViewCellSlot slot)
    {
        var selectionRange = SelectedCellRanges.LastOrDefault(x => x.Contains(slot));
        selectionRange?.Remove(slot);

        if (selectionRange?.Count == 0)
        {
            SelectedCellRanges.Remove(selectionRange);
        }

        CurrentCellSlot = slot;
        OnCellSelectionChanged();
    }

    /// <summary>
    /// Handles changes to the current cell in the table view.
    /// </summary>
    private async Task OnCurrentCellChanged(TableViewCellSlot? oldSlot, TableViewCellSlot? newSlot)
    {
        if (oldSlot == newSlot)
        {
            return;
        }

        if (oldSlot.HasValue)
        {
            var cell = GetCellFromSlot(oldSlot.Value);
            cell?.ApplyCurrentCellState();
        }

        if (newSlot.HasValue)
        {
            // During drag selection, skip expensive scroll-into-view and focus operations.
            // The drag rectangle handles visual feedback, and focus is restored when dragging ends.
            if (IsDragSelecting)
            {
                var cell = GetCellFromSlot(newSlot.Value);
                cell?.ApplyCurrentCellState(skipFocus: true);
            }
            else
            {
                var cell = await ScrollCellIntoView(newSlot.Value);
                cell?.ApplyCurrentCellState();
            }
        }
    }

    /// <summary>
    /// Handles cell selection changes.
    /// </summary>
    private void OnCellSelectionChanged()
    {
        if (_cellSelectionDirty) return;
        _cellSelectionDirty = true;

        if (!DispatcherQueue.TryEnqueue(() =>
        {
            _cellSelectionDirty = false;

            var oldSelection = SelectedCells;
            SelectedCells = [.. SelectedCellRanges.SelectMany(x => x)];

            var rowIndexes = oldSelection.Select(x => x.Row).Concat(SelectedCells.Select(x => x.Row)).Distinct();

            foreach (var rowIndex in rowIndexes)
            {
                var row = _rows.FirstOrDefault(x => x.Index == rowIndex);
                row?.ApplyCellsSelectionState();
            }

            InvokeCellSelectionChangedEvent(oldSelection);
        }))
        {
            _cellSelectionDirty = false;
        }
    }

    /// <summary>
    /// Invokes the <see cref="CellSelectionChanged"/> event to notify subscribers of changes in the selected cells.
    /// </summary>
    private void InvokeCellSelectionChangedEvent(HashSet<TableViewCellSlot> oldSelection)
    {
        var removedCells = oldSelection.Except(SelectedCells).ToList();
        var addedCells = SelectedCells.Except(oldSelection).ToList();

        if (removedCells.Count > 0 || addedCells.Count > 0)
        {
            OnCellSelectionChanged(new TableViewCellSelectionChangedEventArgs(removedCells, addedCells));
        }
    }

    /// <summary>
    /// FIX A: records where a plain press landed (and the scroll baseline) so a later drag can
    /// span from the original point, WITHOUT engaging drag selection — no IsDragSelecting, no
    /// ViewChanged subscription, no rectangle. A press that stays a click never enters drag
    /// state. Promoted by <see cref="BeginArmedDragSelection"/> on the first real movement.
    /// </summary>
    /// <param name="startPoint">The starting point relative to the drag rectangle canvas.</param>
    internal void ArmDragSelection(Point startPoint)
    {
        if (SelectionMode is not (ListViewSelectionMode.Multiple or ListViewSelectionMode.Extended))
        {
            return;
        }

        _dragArmed = true;
        _armedDragStartPoint = startPoint;
        _armedDragVerticalOffset = _scrollViewer?.VerticalOffset ?? 0;
        _armedDragHorizontalOffset = HorizontalOffset;
    }

    /// <summary>
    /// FIX A: promotes an armed press into an active drag selection the first time the pointer
    /// travels past the drag threshold. Idempotent — only the first call after arming engages.
    /// The start point and scroll baseline come from arm time so the rectangle spans from where
    /// the press happened. No-op when nothing is armed.
    /// </summary>
    internal void BeginArmedDragSelection()
    {
        if (!_dragArmed)
        {
            return;
        }

        _dragArmed = false;
        StartDragSelection(_armedDragStartPoint, _armedDragVerticalOffset, _armedDragHorizontalOffset);
    }

    /// <summary>
    /// Starts drag selection tracking, auto-scroll, and optionally the drag rectangle visual.
    /// </summary>
    /// <param name="startPoint">The starting point relative to the drag rectangle canvas.</param>
    internal void StartDragSelection(Point startPoint)
        => StartDragSelection(startPoint, _scrollViewer?.VerticalOffset ?? 0, HorizontalOffset);

    /// <summary>
    /// Engine for <see cref="StartDragSelection(Point)"/> and <see cref="BeginArmedDragSelection"/>.
    /// Takes the scroll baseline explicitly so the armed path anchors the rectangle to the
    /// offsets captured at press time.
    /// </summary>
    private void StartDragSelection(Point startPoint, double baseVerticalOffset, double baseHorizontalOffset)
    {
        if (SelectionMode is not (ListViewSelectionMode.Multiple or ListViewSelectionMode.Extended))
        {
            return;
        }

        // Guard against re-entry (e.g., multi-touch) to prevent double ViewChanged subscription
        if (IsDragSelecting)
        {
            EndDragSelection();
        }

        IsDragSelecting = true;
        _lastDragCanvasPoint = startPoint;
        _dragStartVerticalOffset = baseVerticalOffset;
        _dragStartHorizontalOffset = baseHorizontalOffset;

        if (_scrollViewer is not null)
        {
            _scrollViewer.ViewChanged += OnScrollViewerViewChangedDuringDrag;
        }

        // Show the drag rectangle visual if enabled and template parts are available
        if (ShowDragRectangle && DragRectangleCanvas is not null && _dragRectangle is not null)
        {
            _dragStartPoint = startPoint;

            Canvas.SetLeft(_dragRectangle, startPoint.X);
            Canvas.SetTop(_dragRectangle, startPoint.Y);
            _dragRectangle.Width = 0;
            _dragRectangle.Height = 0;

            _dragRectangle.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// Updates the drag visual and auto-scroll during drag selection.
    /// </summary>
    /// <param name="currentPoint">The current pointer position relative to the drag rectangle canvas.</param>
    internal void UpdateDragRectangleVisual(Point currentPoint)
    {
        if (!IsDragSelecting)
        {
            return;
        }

        _lastDragCanvasPoint = currentPoint;

        // Update the rectangle visual if it's active
        if (_dragStartPoint is not null && DragRectangleCanvas is not null && _dragRectangle is not null)
        {
            PositionDragRectangle(currentPoint);
        }

        UpdateAutoScroll(currentPoint);
    }

    /// <summary>
    /// Positions the drag rectangle visual from the scroll-adjusted start point to the current point,
    /// so the rectangle follows the mouse and extends naturally when content scrolls.
    /// </summary>
    private void PositionDragRectangle(Point currentPoint)
    {
        if (_dragStartPoint is null || DragRectangleCanvas is null || _dragRectangle is null) return;

        // Adjust the start point by how much the view has scrolled since drag began.
        // This makes the rectangle extend naturally as content scrolls.
        var verticalScrollDelta = (_scrollViewer?.VerticalOffset ?? 0) - _dragStartVerticalOffset;
        var horizontalScrollDelta = HorizontalOffset - _dragStartHorizontalOffset;
        var adjustedStartY = _dragStartPoint.Value.Y - verticalScrollDelta;
        var adjustedStartX = _dragStartPoint.Value.X - horizontalScrollDelta;

        var canvasWidth = DragRectangleCanvas.ActualWidth;
        var canvasHeight = DragRectangleCanvas.ActualHeight;

        var left = Math.Max(0, Math.Min(adjustedStartX, currentPoint.X));
        var top = Math.Max(0, Math.Min(adjustedStartY, currentPoint.Y));
        var right = Math.Min(canvasWidth, Math.Max(adjustedStartX, currentPoint.X));
        var bottom = Math.Min(canvasHeight, Math.Max(adjustedStartY, currentPoint.Y));

        Canvas.SetLeft(_dragRectangle, left);
        Canvas.SetTop(_dragRectangle, top);
        _dragRectangle.Width = Math.Max(0, right - left);
        _dragRectangle.Height = Math.Max(0, bottom - top);
    }

    /// <summary>
    /// Manages auto-scroll behavior when the pointer is near the top or bottom edge during drag selection.
    /// </summary>
    private void UpdateAutoScroll(Point canvasPoint)
    {
        if (_scrollViewer is null) return;

        const double edgeThreshold = 40;
        const double maxScrollSpeed = 20;

        var viewportHeight = _scrollViewer.ViewportHeight;
        var viewportWidth = _scrollViewer.ViewportWidth;
        double vDelta = 0;
        double hDelta = 0;

        if (canvasPoint.Y > viewportHeight - edgeThreshold)
        {
            var proximity = Math.Min(1.0, (canvasPoint.Y - (viewportHeight - edgeThreshold)) / edgeThreshold);
            vDelta = proximity * maxScrollSpeed;
        }
        else if (canvasPoint.Y < edgeThreshold)
        {
            var proximity = Math.Min(1.0, (edgeThreshold - canvasPoint.Y) / edgeThreshold);
            vDelta = -(proximity * maxScrollSpeed);
        }

        if (canvasPoint.X > viewportWidth - edgeThreshold)
        {
            var proximity = Math.Min(1.0, (canvasPoint.X - (viewportWidth - edgeThreshold)) / edgeThreshold);
            hDelta = proximity * maxScrollSpeed;
        }
        else if (canvasPoint.X < edgeThreshold)
        {
            var proximity = Math.Min(1.0, (edgeThreshold - canvasPoint.X) / edgeThreshold);
            hDelta = -(proximity * maxScrollSpeed);
        }

        if (Math.Abs(vDelta) > 0.5 || Math.Abs(hDelta) > 0.5)
        {
            _autoScrollVerticalDelta = vDelta;
            _autoScrollHorizontalDelta = hDelta;
            if (_autoScrollTimer is null)
            {
                _autoScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
                _autoScrollTimer.Tick += OnAutoScrollTimerTick;
            }

            _autoScrollTimer.Start();
        }
        else
        {
            StopAutoScroll();
        }
    }

    /// <summary>
    /// Handles the auto-scroll timer tick to scroll the view and update drag selection.
    /// </summary>
    private void OnAutoScrollTimerTick(object? sender, object e)
    {
        if (!IsDragSelecting || _scrollViewer is null)
        {
            StopAutoScroll();
            return;
        }

        var scrolled = false;

        // Vertical auto-scroll via ChangeView
        if (Math.Abs(_autoScrollVerticalDelta) > 0.5)
        {
            var newOffset = Math.Clamp(
                _scrollViewer.VerticalOffset + _autoScrollVerticalDelta,
                0,
                _scrollViewer.ScrollableHeight);

            if (Math.Abs(newOffset - _scrollViewer.VerticalOffset) >= 0.5)
            {
                _scrollViewer.ChangeView(null, newOffset, null, true);
                scrolled = true;
            }
        }

        // Horizontal auto-scroll via HorizontalOffset DP
        if (Math.Abs(_autoScrollHorizontalDelta) > 0.5)
        {
            var newOffset = Math.Clamp(
                HorizontalOffset + _autoScrollHorizontalDelta,
                0,
                _scrollViewer.ScrollableWidth);

            if (Math.Abs(newOffset - HorizontalOffset) >= 0.5)
            {
                SetValue(HorizontalOffsetProperty, newOffset);
                scrolled = true;
            }
        }

        if (!scrolled)
        {
            StopAutoScroll();
            return;
        }

        // Horizontal scroll via HorizontalOffset DP does not fire ViewChanged,
        // so reposition rectangle and update selection here.
        // Vertical scroll fires ViewChanged which handles it via OnScrollViewerViewChangedDuringDrag.
        if (Math.Abs(_autoScrollHorizontalDelta) > 0.5 && _lastDragCanvasPoint is not null)
        {
            if (_dragStartPoint is not null && DragRectangleCanvas is not null && _dragRectangle is not null)
            {
                PositionDragRectangle(_lastDragCanvasPoint.Value);
            }

            SelectCellAtDragPoint();
        }
    }

    /// <summary>
    /// Stops the auto-scroll timer.
    /// </summary>
    private void StopAutoScroll()
    {
        if (_autoScrollTimer is not null)
        {
            _autoScrollTimer.Stop();
            _autoScrollTimer.Tick -= OnAutoScrollTimerTick;
            _autoScrollTimer = null;
        }
    }

    /// <summary>
    /// Handles ScrollViewer.ViewChanged during drag to re-evaluate selection when scroll position changes.
    /// </summary>
    private void OnScrollViewerViewChangedDuringDrag(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!IsDragSelecting || _lastDragCanvasPoint is null) return;

        // Reposition the rectangle using scroll-adjusted start point (if rectangle is active)
        if (_dragStartPoint is not null && DragRectangleCanvas is not null && _dragRectangle is not null)
        {
            PositionDragRectangle(_lastDragCanvasPoint.Value);
        }

        // Update selection for newly visible rows during auto-scroll
        SelectCellAtDragPoint();
    }

    /// <summary>
    /// Selects the cell at the last known drag pointer position.
    /// Used during auto-scroll to select newly visible cells when the pointer isn't moving.
    /// </summary>
    private void SelectCellAtDragPoint()
    {
        if (_scrollViewer is null || _lastDragCanvasPoint is null || DragRectangleCanvas is null)
        {
            return;
        }

        // Clamp to the cell area within the viewport.
        // CellsHorizontalOffset accounts for row headers so we don't hit-test on header area.
        var canvasPoint = _lastDragCanvasPoint.Value;
        var minX = CellsHorizontalOffset + 1;
        var clampedPoint = new Point(
            Math.Clamp(canvasPoint.X, minX, Math.Max(minX, _scrollViewer.ViewportWidth - 1)),
            Math.Clamp(canvasPoint.Y, 1, Math.Max(1, _scrollViewer.ViewportHeight - 1)));

        try
        {
            var screenPoint = DragRectangleCanvas.TransformToVisual(null).TransformPoint(clampedPoint);
#if WINDOWS
            var cell = VisualTreeHelper.FindElementsInHostCoordinates(screenPoint, _scrollViewer)
#else
            var cell = VisualTreeHelper.FindElementsInHostCoordinates(screenPoint, _scrollViewer, true)
                                       .OfType<ContentPresenter>()
                                       .Where(x => x.Name is "Content")
                                       .Select(x => x.FindAscendant<TableViewCell>() is { } c ? c : default)
#endif
                                       .OfType<TableViewCell>()
                                       .FirstOrDefault();

            if (cell is not null && cell.Slot != CurrentCellSlot)
            {
                MakeSelection(cell.Slot, true, IsPointerCtrlDown);
            }
            // A light row has no cell to be found. The sweep is a row-level one either way — the
            // grid selects whole rows or the light path would not be on — so the row itself answers.
            else if (cell is null && AreRowsLight
                     && RowFromHitTest(screenPoint, _scrollViewer) is { } row
                     && row.Index != CurrentRowIndex)
            {
                MakeSelection(new TableViewCellSlot(row.Index, -1), true, IsPointerCtrlDown);
            }
        }
        catch (ArgumentException)
        {
            // Element not in visual tree during container recycling
        }
    }

    /// <summary>
    /// The row under a point in host coordinates.
    /// </summary>
    /// <remarks>
    /// The hit test is asked for everything under the point and then walked upwards, rather than
    /// filtered for a TableViewRow directly: under Uno what comes back is the leaf that was hit, and
    /// a row is several levels above whatever text block the pointer happened to be over.
    /// </remarks>
    internal static TableViewRow? RowFromHitTest(Point screenPoint, ScrollViewer scrollViewer)
    {
        foreach (var element in VisualTreeHelper.FindElementsInHostCoordinates(screenPoint, scrollViewer, true))
        {
            if (element is TableViewRow row)
            {
                return row;
            }

            if (element is FrameworkElement child && child.FindAscendant<TableViewRow>() is { } above)
            {
                return above;
            }
        }

        return null;
    }

    /// <summary>
    /// Ends drag selection tracking, auto-scroll, and hides the drag rectangle if visible.
    /// </summary>
    /// <inheritdoc/>
    protected override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        base.OnPointerMoved(e);

        // A drag-selection with no button down is not a drag-selection.
        //
        // If the window goes to the background mid-press, no release ever arrives: EndDragSelection is
        // never called, IsDragSelecting stays true, and when the window comes back the grid is still
        // "dragging" — so the next plain click extends a range instead of selecting the row. (Undocking
        // and re-docking the player used to clear it, because that tore the grid down and rebuilt it.)
        //
        // The pointer itself is the source of truth: if it is moving over the grid without the button
        // held, whatever gesture was in flight is over.
        if (IsDragSelecting && !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            EndDragSelection();
        }
    }

    /// <inheritdoc/>
    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);

        EndDragSelection();
    }

    internal async void EndDragSelection()
    {
        // FIX A: disarm any armed-but-not-engaged press first. A plain click that never
        // crossed the threshold must not leave a stale armed start point behind for the next
        // manipulation to promote. This runs before the IsDragSelecting early-return so every
        // end path (release, capture lost, lost focus, no-button move, unload) disarms.
        _dragArmed = false;

        if (!IsDragSelecting) return;

        StopAutoScroll();

        if (_scrollViewer is not null)
        {
            _scrollViewer.ViewChanged -= OnScrollViewerViewChangedDuringDrag;
        }

        if (_dragRectangle is not null)
        {
            _dragRectangle.Visibility = Visibility.Collapsed;
        }

        IsDragSelecting = false;
        _dragStartPoint = null;
        _lastDragCanvasPoint = null;

        // Restore focus and scroll to the current cell now that dragging has ended
        try
        {
            if (CurrentCellSlot.HasValue)
            {
                var cell = await ScrollCellIntoView(CurrentCellSlot.Value);
                cell?.ApplyCurrentCellState();
            }
        }
        catch (Exception)
        {
            // Focus restoration is best-effort after drag ends
        }
    }

    /// <summary>
    /// Scrolls the specified cell slot into view.
    /// </summary>
    /// <param name="slot">The cell slot to scroll into view.</param>
    /// <summary>
    /// Takes the viewport to a row by INDEX, by moving the scroll offset there directly.
    ///
    /// <para>Use this and not <see cref="ItemsControl"/>'s ScrollIntoView for a row that has moved a
    /// long way. ScrollIntoView reaches its target by filling the panel forward towards it — one line
    /// at a time, materializing a container, its template and its theme references for every row it
    /// passes. Over a few screens that is unremarkable; over a list of tens of thousands it is a
    /// single measure pass that does not return. Measured on a re-filed track in a 27,000-row queue:
    /// the interface stopped for as long as it was left running, at 429 MB/s of template garbage, and
    /// the resulting collections took half of every second — enough to empty the audio buffer and
    /// stop the music. Setting the offset makes the layouter JUMP: it drops the realized rows and
    /// builds one viewport at the destination.</para>
    ///
    /// <para>The offset comes from <see cref="RowOffsetOfIndex"/> where the host provides one, and
    /// from index × pitch where it does not — the same arithmetic the re-anchor uses, and wrong in
    /// the same way for mixed row heights, which is why a host with those should provide the
    /// function.</para>
    /// </summary>
    /// <param name="index">The row's index in the source.</param>
    /// <returns><see langword="true"/> if the viewport was moved there; <see langword="false"/> when
    /// the row is near enough that the panel's own fill is both cheap and exact, and the caller
    /// should use that instead.</returns>
    /// <summary>Whether a row is far enough off that reaching it means moving the offset rather
    /// than letting the panel fill towards it — the same boundary <see cref="JumpToRow"/> draws,
    /// asked before anything moves.</summary>
    private bool IsFarFromViewport(int index)
    {
        if (index < 0 || _scrollViewer is not { } sv || sv.ViewportHeight <= 0)
        {
            return false;
        }

        var pitch = EstimatedRowPitch();
        var top = RowOffsetOfIndex?.Invoke(index) ?? index * pitch;

        return top <= sv.VerticalOffset - sv.ViewportHeight
               || top + pitch >= sv.VerticalOffset + (sv.ViewportHeight * 2);
    }

    private bool JumpToRow(int index)
    {
        if (index < 0 || _scrollViewer is not { } sv || sv.ViewportHeight <= 0)
        {
            DiagLastJump = $"index={index} declined=no-viewport";
            return false;
        }

        var pitch = EstimatedRowPitch();

        var top = RowOffsetOfIndex?.Invoke(index) ?? index * pitch;
        var bottom = top + pitch;

        var state = $"index={index} pitch={pitch:F0} top={top:F0} offset={sv.VerticalOffset:F0} "
            + $"viewport={sv.ViewportHeight:F0} extent={sv.ExtentHeight:F0} scrollable={sv.ScrollableHeight:F0}";

        // Already on screen: nothing to do, and this is asked on every change that MIGHT have moved
        // a row.
        if (top >= sv.VerticalOffset && bottom <= sv.VerticalOffset + sv.ViewportHeight)
        {
            DiagLastJump = state + " decision=already-visible";
            return true;
        }

        // Within a screen either way, the panel fills there in a handful of lines, and it does it
        // against the real heights rather than an assumed pitch. Leave it to do that.
        if (top > sv.VerticalOffset - sv.ViewportHeight &&
            bottom < sv.VerticalOffset + (sv.ViewportHeight * 2))
        {
            DiagLastJump = state + " decision=declined-near";
            return false;
        }

        // The least travel that shows it: from above, its top; from below, its bottom against the
        // bottom edge. Landing it in the middle would move the reader further than they asked.
        var target = top < sv.VerticalOffset ? top : bottom - sv.ViewportHeight;
        var moved = Math.Clamp(target, 0, Math.Max(0, sv.ScrollableHeight));

        DiagLastJump = state + $" target={target:F0} moved={moved:F0}"
            + (Math.Abs(moved - target) > 1 ? " decision=jumped-CLAMPED" : " decision=jumped");

        sv.ChangeView(null, moved, null, disableAnimation: true);
        return true;
    }

    public async Task<TableViewCell> ScrollCellIntoView(TableViewCellSlot slot)
    {
        if (_scrollViewer is null || !slot.IsValid(this) || await ScrollRowIntoView(slot.Row) is not { } row)
            return default!;

        var (start, end) = GetColumnsInDisplay();
        var xOffset = 0d;
        var yOffset = _scrollViewer.VerticalOffset;

        // Calculate the left and right edge of the cell
        var cellLeft = Columns.VisibleColumns.Take(slot.Column).Sum(x => x.ActualWidth);
        var cellWidth = Columns.VisibleColumns[slot.Column].ActualWidth;
        var cellRight = cellLeft + cellWidth;
        var viewportLeft = HorizontalOffset;
        var headersOffset = CellsHorizontalOffset;
        var viewportRight = viewportLeft + _scrollViewer.ViewportWidth - headersOffset;

        // If cell is wider than the viewport, align left edge
        if (cellWidth > _scrollViewer.ViewportWidth - headersOffset)
        {
            xOffset = cellLeft;
        }
        // If cell is left of the viewport, scroll to its left edge
        else if (cellLeft < viewportLeft)
        {
            xOffset = cellLeft;
        }
        // If cell is right of the viewport, scroll so its right edge is visible
        else if (cellRight > viewportRight)
        {
            xOffset = cellRight - (_scrollViewer.ViewportWidth - headersOffset);
        }

        // If cell is fully in view, just return
        if ((cellLeft >= viewportLeft && cellRight <= viewportRight) ||
            xOffset == HorizontalOffset)
        {
            return row.CellForColumn(slot.Column)!;
        }

        SetValue(HorizontalOffsetProperty, xOffset);

        // The column has only just been scrolled to, so under column virtualization this row may not
        // have built its cell yet: the range change hands the rows out a few per frame, and this
        // caller wants the cell NOW. Asking this one row to catch up is what that costs.
        row.SyncCells();

        return row.CellForColumn(slot.Column)!;
    }

    /// <summary>
    /// Scrolls the specified row into view.
    /// </summary>
    /// <param name="index">The index of the row to scroll into view.</param>
    public async Task<TableViewRow?> ScrollRowIntoView(int index)
    {
        if (_scrollViewer is null || index < 0) return default!;

#if !WINDOWS
        // A LONG reveal asked for in the same turn as a rebuild — which is exactly what following a
        // row that a rebuild MOVED looks like — moves the offset before the panel has laid out
        // against the new collection. The fill that is then in flight does not start again at the
        // new place, it walks to it, a container and a template per row passed; over a queue of
        // tens of thousands that is a measure pass that does not return. One turn of the loop is
        // all it takes for the move to be a jump again.
        //
        // Only for the long ones. A reveal of the row below the last one is what arrow keys do, and
        // spending a frame on each of those to guard against a walk of two rows would cost more
        // than it saves.
        if (IsFarFromViewport(index))
        {
            // The restore a rebuild arms is dropped on the way past: it exists to keep a reader who
            // asked for nothing where they were, and this reader has asked for something. Leaving
            // it armed sets the two of them pulling the viewport in opposite directions. Whether it
            // has been armed yet does not matter — a caller that rebuilds and reveals in one turn
            // gets here before the rebind does.
            _unoReanchorPending = false;
            _unoReanchorCandidates.Clear();

            var settled = new TaskCompletionSource();
            if (DispatcherQueue?.TryEnqueue(() => settled.TrySetResult()) is true)
            {
                await settled.Task;
            }

            if (_scrollViewer is null || index >= Items.Count) return default!;
        }
#endif

        var item = Items[index];
        // FIX D: keep the caller's index — it is already valid (guarded above). On a virtualized
        // source an unfetched leaf resolves to a shared placeholder whose Items.IndexOf is -1 (or
        // a wrong duplicate index), and overwriting index with that silently broke PageDown/End
        // and scrolling into a cold region. ContainerFromIndex(index) in the retry loop below
        // needs the real index. (There is no "item without index" caller here to reconcile.)
        //
        // A row a long way off is reached by MOVING there, not by filling towards it — see JumpToRow.
        // The loop below then corrects the landing against the real heights, which is the part
        // ScrollIntoView was being relied on for and the part that is cheap.
        if (!JumpToRow(index))
        {
            ScrollIntoView(item);
        }

        var tries = 0;
        while (tries < 10)
        {
            tries++;
            await Task.Yield();

            if (ContainerFromIndex(index) is TableViewRow row)
            {
                var transform = row.TransformToVisual(_scrollViewer);
                var positionInScrollViewer = transform.TransformPoint(new Point(0, 0));
                if ((index == 0 && _scrollViewer.VerticalOffset > 0) || (index > 0 && positionInScrollViewer.Y < HeaderRowHeight))
                {
                    var yOffset = index == 0 ? 0d : _scrollViewer.VerticalOffset - row.ActualHeight + positionInScrollViewer.Y + 8;
                    var tcs = new TaskCompletionSource<object?>();

                    try
                    {
                        _scrollViewer.ViewChanged += ViewChanged;
                        _scrollViewer.ChangeView(0, yOffset, null, true);
                        await tcs.Task;
                    }
                    finally
                    {
                        _scrollViewer.ViewChanged -= ViewChanged;
                    }

                    void ViewChanged(object? _, ScrollViewerViewChangedEventArgs e)
                    {
                        if (e.IsIntermediate)
                        {
                            return;
                        }

                        tcs.TrySetResult(result: default);
                    }
                }

                return row;
            }
        }

        return default;
    }

    /// <summary>
    /// Gets the cell based on the specified cell slot.
    /// </summary>
    internal TableViewCell? GetCellFromSlot(TableViewCellSlot slot)
    {
        if (!slot.IsValid(this) || ContainerFromIndex(slot.Row) is not TableViewRow row)
        {
            return default;
        }

        return row.CellForColumn(slot.Column);
    }

    /// <summary>
    /// Gets the columns currently in view.
    /// </summary>
    private (int start, int end) GetColumnsInDisplay()
    {
        if (_scrollViewer is null) return default!;

        var start = -1;
        var end = -1;
        var width = 0d;
        var headersOffset = CellsHorizontalOffset;

        foreach (var column in Columns.VisibleColumns)
        {
            if (width >= HorizontalOffset &&
                width + column.ActualWidth <= HorizontalOffset + _scrollViewer.ViewportWidth - headersOffset)
            {
                if (start == -1)
                {
                    start = end = Columns.VisibleColumns.IndexOf(column);
                }
                else
                {
                    end = Columns.VisibleColumns.IndexOf(column);
                }
            }

            width += column.ActualWidth;
        }

        return (start, end);
    }

    /// <summary>
    /// Updates the base SelectionMode property.
    /// </summary>
    private void UpdateBaseSelectionMode()
    {
        _shouldThrowSelectionModeChangedException = true;

        // EXPERIMENT (logical-selection redesign): force base None for rows too,
        // so Uno's O(N) ExtendedSelectionCase never runs on a row click — the fork's
        // MakeSelection/SelectRows is the sole row-selection authority (mirrors how
        // cell selection already works under base None). Was:
        //   SelectionUnit is Cell ? None : SelectionMode
        base.SelectionMode = ListViewSelectionMode.None;

        UpdateHorizontalScrollBarMargin();
        _headerRow?.SetHeadersVisibility();

        foreach (var row in _rows)
        {
            row.EnsureLayout();
            row.RowPresenter?.SetRowHeaderVisibility();

        }

        _shouldThrowSelectionModeChangedException = false;
    }

    /// <summary>
    /// Ensures grid lines are applied to the header row and body rows.
    /// </summary>
    private void EnsureGridLines()
    {
        _headerRow?.EnsureGridLines();

        foreach (var row in _rows)
        {
            row.RowPresenter?.EnsureGridLines();
        }
    }

    /// <summary>
    /// Ensures alternate row colors are applied.
    /// </summary>
    internal void EnsureAlternateRowColors()
    {
        // Every row rebinding asks for this, and a scroll rebinds the whole viewport — so a
        // single hop used to queue one whole-grid sweep PER ROW, each one re-colouring every
        // other row and resolving every one of their indexes. One sweep says everything the
        // twenty said; the rest were the same answer, computed again.
        if (_alternateColorsQueued)
        {
            return;
        }

        _alternateColorsQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _alternateColorsQueued = false;
            DiagAlternateSweeps++;
            foreach (var row in _rows)
            {
                row.EnsureAlternateColors();
            }
        });
    }

    private bool _alternateColorsQueued;

    /// <summary>
    /// Resets the auto-calculated widths of the specified columns and recalculates them.
    /// </summary>
    /// <param name="columns">The columns to refresh. When null, all columns are refreshed.</param>
    internal void RefreshColumnsAutoWidth(IEnumerable<TableViewColumn>? columns = null)
    {
        var targetColumns = (columns ?? Columns).ToHashSet();
        if (targetColumns.Count == 0)
        {
            return;
        }

        foreach (var column in targetColumns)
        {
            column.DesiredWidth = 0d;
            column.HeaderControl?.InvalidateMeasure();
        }

        foreach (var row in _rows)
        {
            foreach (var cell in row.Cells)
            {
                if (cell.Column is { } cellColumn && targetColumns.Contains(cellColumn))
                {
                    cell.InvalidateMeasure();
                }
            }
        }

        DispatcherQueue.TryEnqueue(() => _headerRow?.CalculateHeaderWidths());
    }

    /// <summary>
    /// Ensures the column headers style is applied.
    /// </summary>
    private void EnsureColumnHeadersStyle()
    {
        foreach (var column in Columns)
        {
            column.EnsureHeaderStyle();
        }
    }

    /// <summary>
    /// Ensures the cells style is applied.
    /// </summary>
    private void EnsureCellsStyle()
    {
        foreach (var row in _rows)
        {
            row.EnsureCellsStyle();
        }
    }

#if !WINDOWS
    /// <summary>
    /// Ensures the cells are created.
    /// </summary>
    internal void EnsureCells()
    {
        foreach (var row in _rows)
        {
            row.EnsureCells();
        }
    }
#endif

    /// <summary>
    /// Shows the context flyout for the specified row.
    /// </summary>
    internal bool ShowRowContext(TableViewRow row, Point position)
    {
        var eventArgs = new TableViewRowContextFlyoutEventArgs(row.Index, row, row.Content, RowContextFlyout);
        OnRowContextFlyoutOpening(eventArgs);

        if (RowContextFlyout is not null && !eventArgs.Handled)
        {
#if !WINDOWS
            RowContextFlyout.DataContext = row.Content;
#endif
            RowContextFlyout.ShowAt(row.RowPresenter, new FlyoutShowOptions
            {
#if WINDOWS
                ShowMode = FlyoutShowMode.Standard,
#endif
                Placement = RowContextFlyout.Placement,
                Position = position
            });

            return true;
        }

        return false;
    }

    /// <summary>
    /// Shows the context flyout for the specified cell.
    /// </summary>
    internal bool ShowCellContext(TableViewCell cell, Point position)
    {
        var eventArgs = new TableViewCellContextFlyoutEventArgs(cell.Slot, cell, cell.Row?.Content!, CellContextFlyout);
        OnCellContextFlyoutOpening(eventArgs);

        if (CellContextFlyout is not null && !eventArgs.Handled)
        {
#if !WINDOWS
            CellContextFlyout.DataContext = cell.Row?.Content;
#endif
            CellContextFlyout.ShowAt(cell, new FlyoutShowOptions
            {
#if WINDOWS
                ShowMode = FlyoutShowMode.Standard,
#endif
                Placement = CellContextFlyout.Placement,
                Position = position
            });

            return true;
        }

        return false;
    }

    /// <summary>
    /// Sets the state of the corner button.
    /// </summary>
    internal void UpdateCornerButtonState()
    {
        _headerRow?.SetCornerButtonState();

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (SelectionMode is ListViewSelectionMode.Multiple && SelectionUnit is not TableViewSelectionUnit.Cell)
            {
                foreach (var row in _rows)
                {
                    row.UpdateSelectCheckMarkOpacity();
                }
            }
        });
    }

    internal void SetIsEditing(bool value)
    {
        if (IsEditing == value)
        {
            return;
        }

        IsEditing = value;
        UpdateCornerButtonState();
    }

    /// <summary>
    /// Sets the visibility of the headers.
    /// </summary>
    private void SetHeadersVisibility()
    {
        if (_headerRowDefinition is not null)
        {
            var areColumnHeadersVisible = HeadersVisibility is TableViewHeadersVisibility.All or TableViewHeadersVisibility.Columns;
            _headerRowDefinition.Height = areColumnHeadersVisible ? GridLength.Auto : new(0);
        }

        _headerRow?.SetHeadersVisibility();

        foreach (var row in _rows)
        {
            row.RowPresenter?.SetRowHeaderVisibility();
        }
    }

    /// <summary>
    /// Updates the margin of the horizontal scroll bar to account for frozen columns and row headers.
    /// </summary>
    internal void UpdateHorizontalScrollBarMargin()
    {
        if (_scrollViewer is null) return;

        var offset = CellsHorizontalOffset + Columns.VisibleColumns.Where(c => c.IsFrozen).Sum(c => c.ActualWidth);
        AttachedPropertiesHelper.SetFrozenColumnScrollBarSpace(_scrollViewer, offset);
    }
}
