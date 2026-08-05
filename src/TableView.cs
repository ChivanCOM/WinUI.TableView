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
    }

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
    public static long DiagCellListBuilds;
    public static long DiagRowIndexLookups;
    public static long DiagAlternateSweeps;
    public static long DiagInsertCellScans;

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
            ReseatPanelRows();
        }
#endif

        // Track the row as currently-realized. Added here (on realize), removed
        // in ClearContainerForItemOverride (on recycle) so _rows stays bounded
        // to the viewport. Previously rows were added in GetContainerForItemOverride
        // and never removed — _rows grew without bound across scrolling, leaking
        // every TableViewRow ever created and turning the _rows iteration in
        // selection / layout / grid-line passes into an O(rows-ever-realized)
        // walk that compounds the per-click cost on large virtualized sources.
        if (element is TableViewRow tracked)
        {
            tracked.InvalidateIndex();

            if (!_rows.Contains(tracked))
            {
                _rows.Add(tracked);
            }
        }

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
        var row = new TableViewRow { TableView = this };

        // Set bindings for FontFamily and FontSize to propagate from TableView to TableViewRow
        row.SetBinding(FontFamilyProperty, new Binding { Path = new("TableView.FontFamily"), RelativeSource = new() { Mode = RelativeSourceMode.Self } });
        row.SetBinding(FontSizeProperty, new Binding { Path = new("TableView.FontSize"), RelativeSource = new() { Mode = RelativeSourceMode.Self } });

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
            ReseatPanelRows();
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
        var pointerPoint = e.GetCurrentPoint(this);
        var isShiftButton = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift);
        var isHorizontalScroll = isShiftButton || pointerPoint.Properties.IsHorizontalMouseWheel;

        if (isHorizontalScroll && _scrollViewer?.ComputedHorizontalScrollBarVisibility is Visibility.Visible)
        {
            e.Handled = true;
            var mouseWheelDelta = isShiftButton ? -pointerPoint.Properties.MouseWheelDelta : pointerPoint.Properties.MouseWheelDelta;
            var xOffset = HorizontalOffset + (mouseWheelDelta / 4.0);
            SetValue(HorizontalOffsetProperty, Math.Clamp(xOffset, 0, _scrollViewer.ScrollableWidth));
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
    /// <summary>How many rows the list had at the previous Reset, so the next one can tell a list
    /// that changed shape from a list that was merely re-read.</summary>
    private int _lastResetCount;

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

            // And if the list changed size wholesale — a search box being typed into, a filter
            // applied, ten thousand duplicates removed — the offset goes back to the top NOW,
            // synchronously, before anything can lay out against it.
            //
            // It cannot wait for the rebind on the dispatcher. A layout pass in between finds the
            // panel still parked at an offset deep inside a list that has just shrunk, and Uno's
            // layouter fills line by line from its seed towards that position, realizing a whole
            // row of cells for every line on the way. That is the interface freezing solid on a
            // keystroke, and the further down the list the search box was reached for, the longer
            // it freezes. The reader's place was destroyed by the change, not moved by it; the top
            // is the honest answer and the only cheap one.
            var count = Items?.Count ?? 0;
            if (_lastResetCount > 0 && Math.Abs(count - _lastResetCount) * 10 > _lastResetCount
                && _scrollViewer is { VerticalOffset: > 0 } sv)
            {
                sv.ChangeView(null, 0, null, disableAnimation: true);
            }

            _lastResetCount = count;
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
                RebindBaseItemsSource();
                return;
            }

            var changedIndices = new HashSet<int>(_unoChangedIndices);
            _unoChangedIndices.Clear();
            PatchRealizedRows(changedIndices);

            if (_unoShiftReseat)
            {
                _unoShiftReseat = false;
                ReseatPanelRows();
                ItemsPanelRoot?.InvalidateMeasure();
            }

            if (_unoReseatBurstsLeft > 0)
            {
                _unoReseatBurstsLeft--;
                ReseatPanelRows();
            }
        });
    }

    // Uno's ItemsStackPanel re-realizes a re-pointed source from ITEM 0 while the ScrollViewer
    // keeps its old offset: every container lands above the viewport and the grid looks empty
    // (the "disappearing rows" / blank band after a collapse while scrolled down). The panel
    // only re-anchors to the offset on a VIEW CHANGE, and its rebuild finishes asynchronously
    // whole seconds after the re-point — nudges fired in the first few hundred ms provably get
    // wiped, while a double-nudge (offset−1, then offset a beat later; two DISTINCT offsets,
    // same-offset ChangeView is a no-op) once things settle provably re-anchors. So repeat
    // that double-nudge on a paced schedule across ~5s, driven by awaited UI-thread delays
    // (a DispatcherTimer proved unreliable here), cancelled by the next rebind's generation
    // bump or by the user scrolling away.
    private double _unoReanchorOffset;
    private int _unoReanchorGeneration;

    private static readonly bool ReanchorTrace = Environment.GetEnvironmentVariable("TREEGRID_TRACE") == "1";

    private void RebindBaseItemsSource()
    {
        _unoReanchorOffset = _scrollViewer?.VerticalOffset ?? 0;
        _unoReanchorCountBefore = Items?.Count ?? 0;

        // Capture the visible rows' items (top-down) BEFORE the re-point: whichever of them
        // still resolves to an index afterwards anchors the viewport again. Skip contents
        // whose IndexOf disagrees with the row (shared placeholders resolve to a duplicate).
        _unoReanchorCandidates.Clear();
        if (_unoReanchorOffset > 0 && _scrollViewer is { } svA)
        {
            foreach (var row in _rows
                .Where(r => r.ActualHeight > 0 && r.Content is not null)
                .Select(r => { try { return (Row: r, Y: r.TransformToVisual(svA).TransformPoint(new Point(0, 0)).Y); } catch (ArgumentException) { return (Row: r, Y: double.NaN); } })
                .Where(t => !double.IsNaN(t.Y) && t.Y > -t.Row.ActualHeight && t.Y < svA.ViewportHeight)
                .OrderBy(t => t.Y))
            {
                _unoReanchorCandidates.Add(row.Row.Content);
            }
        }

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
                Console.WriteLine($"[reanchor] rebind offset={_unoReanchorOffset:F0} sv={(_scrollViewer is not null)}");

        if (_unoReanchorOffset > 0 && _scrollViewer is { } sv)
        {
            // A list that changed wholesale — a search box being typed into, a filter applied, ten
            // thousand duplicates removed — did not move the reader's place, it destroyed it. Going
            // back to the top is the honest answer, and it is the only cheap one: leaving the offset
            // deep in a list that just shrank leaves the panel filling line by line from its seed
            // towards a position that no longer means anything, realizing a whole row of cells for
            // each line on the way. That is the interface freezing solid on a keystroke, and the
            // further down the list the search box was reached for, the longer it freezes.
            if (!AnchorRowIsMeaningful())
            {
                sv.ChangeView(null, 0, null, disableAnimation: true);
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

    /// <summary>How many rows there were before the re-point. A rebind that changed the row count
    /// wholesale did not move the reader's place — it destroyed it.</summary>
    private int _unoReanchorCountBefore;

    /// <summary>
    /// Whether a row-derived anchor is worth aiming at.
    ///
    /// <para>Re-anchoring restores a POSITION. That is meaningful when the list is substantially the
    /// same list — an edit, a re-sort, a page landing — and meaningless when it is not: delete ten
    /// thousand of twenty thousand rows and the row somebody was looking at is either gone or has
    /// moved half the list, so aiming at it means a long scroll to somewhere they never asked to be.
    /// Worse, the restore loop retries until the offset lands within a couple of pixels of the
    /// target, so an unreachable one costs hundreds of layout passes — which is a hang.</para>
    ///
    /// <para>Below the threshold the old offset is restored instead, clamped by the ScrollViewer,
    /// which is both instant and the honest answer to "that place no longer exists".</para>
    /// </summary>
    private bool AnchorRowIsMeaningful()
    {
        var before = _unoReanchorCountBefore;
        var now = Items?.Count ?? 0;
        if (before == 0)
            return false;
        return Math.Abs(now - before) * 10 <= before;   // within 10% of the list it was
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

    private void TryUnoReanchorRestore()
    {
        if (_scrollViewer is not { } sv)
        {
            _unoReanchorPending = false;
            return;
        }

        var meaningful = AnchorRowIsMeaningful();
        var anchorIndex = -1;
        if (meaningful)
        {
            foreach (var item in _unoReanchorCandidates)
            {
                anchorIndex = ResolveAnchorIndex(item);
                if (anchorIndex >= 0) break;
            }
        }

        // A list that changed wholesale — a search box being typed into, a filter applied, ten
        // thousand duplicates removed — did not move the reader's place. It destroyed it. There is
        // nothing left to restore, so whatever the ScrollViewer has already clamped the offset to
        // is the honest answer, and this is finished.
        //
        // What it did instead was aim at the offset from the PREVIOUS list and keep aiming: every
        // ChangeView triggers prepares, every prepare comes back here, and the target is never
        // reached because the extent is still growing underneath it — so it ran to the
        // four-hundred-round cap, each round realizing a whole viewport of cells. That is the
        // interface freezing solid on every keystroke of a search, and the further down the list
        // the search box was reached for, the longer it freezes.
        if (!meaningful)
        {
            _unoReanchorPending = false;
            _unoReanchorCandidates.Clear();
            ReseatPanelRows();
            _unoReseatBurstsLeft = 5;
            return;
        }

        var pitch = _rows.FirstOrDefault(r => r.ActualHeight > 0)?.ActualHeight + 1 ?? 41;
        // Mixed row heights (RowHeightSelector) make index × pitch wrong by the accumulated
        // difference above the anchor — the host's offset function is exact where provided.
        var target = anchorIndex >= 0
            ? RowOffsetOfIndex?.Invoke(anchorIndex) ?? anchorIndex * pitch
            : _unoReanchorOffset;
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

    private void InvokeLayouterMethod(string name)
    {
        try
        {
            var layouter = GetLayouterViaReflection();
            layouter?.GetType().GetMethod(name,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                ?.Invoke(layouter, null);
            if (ReanchorTrace)
                Console.WriteLine($"[reanchor] layouter {name} invoked");
        }
        catch (Exception ex)
        {
            if (ReanchorTrace)
                Console.WriteLine($"[reanchor] layouter {name} failed: {ex.Message}");
        }
    }

    private object? GetLayouterViaReflection()
    {
        if (ItemsPanelRoot is not { } panel)
        {
            return null;
        }
        var get = panel.GetType().GetMethod("Microsoft.UI.Xaml.Controls.IVirtualizingPanel.GetLayouter",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? panel.GetType().GetMethod("GetLayouter",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        return get?.Invoke(panel, null);
    }

    private bool RestoreAnchorViaLayouter()
    {
        var anchorIndex = -1;
        if (AnchorRowIsMeaningful())
        {
            foreach (var item in _unoReanchorCandidates)
            {
                anchorIndex = ResolveAnchorIndex(item);
                if (anchorIndex >= 0) break;
            }
        }

        try
        {
            var layouter = GetLayouterViaReflection();

            var core = layouter?.GetType().GetMethod("ScrollIntoViewCore",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

            if (ReanchorTrace)
                Console.WriteLine($"[reanchor] layouter={layouter?.GetType().Name} core={core is not null} anchorIndex={anchorIndex}");

            if (core is not null && anchorIndex >= 0 && _scrollViewer is { } svc)
            {
                core.Invoke(layouter, new object[] { anchorIndex, ScrollIntoViewAlignment.Leading });

                // Anchored when the offset now sits within a viewport of where the anchor
                // row belongs (pitch estimated from a live container; the host's offset
                // function is exact under mixed row heights).
                var pitch = _rows.FirstOrDefault(r => r.ActualHeight > 0)?.ActualHeight + 1 ?? 41;
                var expected = RowOffsetOfIndex?.Invoke(anchorIndex) ?? anchorIndex * pitch;
                var ok = Math.Abs(svc.VerticalOffset - expected) < pitch * 4;
                if (ReanchorTrace)
                    Console.WriteLine($"[reanchor] after core: offset={svc.VerticalOffset:F0} expected~{expected:F0} ok={ok}");
                return ok;
            }
        }
        catch (Exception ex)
        {
            if (ReanchorTrace)
                Console.WriteLine($"[reanchor] layouter anchor failed: {ex.Message}");
        }

        // Fallback: plain offset restore.
        _scrollViewer?.ChangeView(null, Math.Min(_unoReanchorOffset, Math.Max(0, _scrollViewer.ScrollableHeight)), null, disableAnimation: true);
        return false;
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
        }
        catch (ArgumentException)
        {
            // Element not in visual tree during container recycling
        }
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
            return row.Cells.ElementAt(slot.Column);
        }

        SetValue(HorizontalOffsetProperty, xOffset);

        return row?.Cells.ElementAt(slot.Column)!;
    }

    /// <summary>
    /// Scrolls the specified row into view.
    /// </summary>
    /// <param name="index">The index of the row to scroll into view.</param>
    public async Task<TableViewRow?> ScrollRowIntoView(int index)
    {
        if (_scrollViewer is null || index < 0) return default!;

        var item = Items[index];
        // FIX D: keep the caller's index — it is already valid (guarded above). On a virtualized
        // source an unfetched leaf resolves to a shared placeholder whose Items.IndexOf is -1 (or
        // a wrong duplicate index), and overwriting index with that silently broke PageDown/End
        // and scrolling into a cold region. ContainerFromIndex(index) in the retry loop below
        // needs the real index. (There is no "item without index" caller here to reconcile.)
        ScrollIntoView(item);

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
        return slot.IsValid(this) && ContainerFromIndex(slot.Row) is TableViewRow row ? row.Cells[slot.Column] : default;
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
