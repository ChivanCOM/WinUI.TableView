using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Foundation;
using WinUI.TableView.Extensions;
using WinUI.TableView.Helpers;

namespace WinUI.TableView;

/// <summary>
/// Represents a cell in a TableView.
/// </summary>
[TemplateVisualState(Name = VisualStates.StateNormal, GroupName = VisualStates.GroupCommon)]
[TemplateVisualState(Name = VisualStates.StatePointerOver, GroupName = VisualStates.GroupCommon)]
[TemplateVisualState(Name = VisualStates.StateRegular, GroupName = VisualStates.GroupCurrent)]
[TemplateVisualState(Name = VisualStates.StateCurrent, GroupName = VisualStates.GroupCurrent)]
[TemplateVisualState(Name = VisualStates.StateSelected, GroupName = VisualStates.GroupSelection)]
[TemplateVisualState(Name = VisualStates.StateUnselected, GroupName = VisualStates.GroupSelection)]
#if WINDOWS
[WinRT.GeneratedBindableCustomProperty]
#endif
public partial class TableViewCell : ContentControl
{
    private ScrollViewer? _scrollViewer;
    private ContentPresenter? _contentPresenter;
    private Border? _selectionBorder;
    private Rectangle? _v_gridLine;
    private object? _uneditedValue;
    private RoutedEventArgs? _editingArgs;
    private IList<TableViewConditionalCellStyle>? _cellStyles;

    /// <summary>
    /// Initializes a new instance of the TableViewCell class.
    /// </summary>
    public TableViewCell()
    {
        DefaultStyleKey = typeof(TableViewCell);
        ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY;
        Loaded += OnLoaded;
#if WINDOWS
        ContextRequested += OnContextRequested;
#endif
    }

#if !WINDOWS
    /// <inheritdoc/>
    protected override void OnRightTapped(RightTappedRoutedEventArgs e)
    {
        base.OnRightTapped(e);

        var position = e.GetPosition(this);
#else
    /// <summary>
    /// Handles the ContextRequested event.
    /// </summary>
    private void OnContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (!e.TryGetPosition(sender, out var position)) return;
#endif

        // Select the cell before showing the Context Menu
        if (TableView is not null && TableView.ForceRowOrCellSelectionOnContextRequested && !IsSelected)
        {
            TableView.MakeSelection(Slot, false);
        }

        e.Handled = TableView?.ShowCellContext(this, position) is true;
    }


    /// <summary>
    /// Handles the Loaded event.
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        InvalidateMeasure();
        ApplySelectionState();
    }

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _contentPresenter = GetTemplateChild("Content") as ContentPresenter;
        _selectionBorder = GetTemplateChild("SelectionBorder") as Border;
        _v_gridLine = GetTemplateChild("VerticalGridLine") as Rectangle;

        EnsureGridLines();
        EnsureStyle(Row?.Content);
    }

    /// <inheritdoc/>
    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);

        if (newContent is ContentControl contentControl)
        {
            contentControl.Loaded += OnContentLoaded;
        }

        void OnContentLoaded(object sender, RoutedEventArgs e)
        {
            ((ContentControl)sender).Loaded -= OnContentLoaded;
            Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        }
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        if (TableView is not null && Column is not null && Row is not null && _contentPresenter is not null && Content is FrameworkElement element)
        {
            if (Column is TableViewTemplateColumn)
            {
#if WINDOWS
                if (element is ContentControl { ContentTemplateRoot: FrameworkElement root })
#else
                if (element.FindDescendant<ContentPresenter>() is { ContentTemplateRoot: FrameworkElement root })
#endif
                    element = root;
                else
                    return base.MeasureOverride(availableSize);
            }

            #region TEMP_FIX_FOR_ISSUE https://github.com/microsoft/microsoft-ui-xaml/issues/9860
            element.MaxWidth = double.PositiveInfinity;
            element.MaxHeight = double.PositiveInfinity;
            #endregion

            element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            var autoSizeMode = Column.ColumnAutoWidthMode ?? TableView.ColumnAutoWidthMode;
            if (autoSizeMode is TableViewColumnAutoWidthMode.Cells or TableViewColumnAutoWidthMode.Both)
            {
                var desiredWidth = element.DesiredSize.Width;
                desiredWidth += Padding.Left;
                desiredWidth += Padding.Right;
                desiredWidth += BorderThickness.Left;
                desiredWidth += BorderThickness.Right;
                desiredWidth += _selectionBorder?.BorderThickness.Right ?? 0;
                desiredWidth += _selectionBorder?.BorderThickness.Left ?? 0;
                desiredWidth += _v_gridLine?.ActualWidth ?? 0d;

                Column.DesiredWidth = Math.Max(Column.DesiredWidth, desiredWidth);
            }

            #region TEMP_FIX_FOR_ISSUE https://github.com/microsoft/microsoft-ui-xaml/issues/9860
            var contentWidth = Column.ActualWidth;
            contentWidth -= element.Margin.Left;
            contentWidth -= element.Margin.Right;
            contentWidth -= Padding.Left;
            contentWidth -= Padding.Right;
            contentWidth -= BorderThickness.Left;
            contentWidth -= BorderThickness.Right;
            contentWidth -= _selectionBorder?.BorderThickness.Left ?? 0;
            contentWidth -= _selectionBorder?.BorderThickness.Right ?? 0;
            contentWidth -= _v_gridLine?.ActualWidth ?? 0d;

            var height = Height is double.NaN ? double.PositiveInfinity : Height;
            var contentHeight = Math.Min(height, MaxHeight);
            contentHeight -= element.Margin.Top;
            contentHeight -= element.Margin.Bottom;
            contentHeight -= Padding.Top;
            contentHeight -= Padding.Bottom;
            contentHeight -= BorderThickness.Top;
            contentHeight -= BorderThickness.Bottom;
            contentHeight -= _selectionBorder?.BorderThickness.Top ?? 0;
            contentHeight -= _selectionBorder?.BorderThickness.Bottom ?? 0;
            contentHeight -= GetHorizontalGridlineHeight();

            if (contentWidth < 0 || contentHeight < 0)
            {
                _contentPresenter.Visibility = Visibility.Collapsed;
            }
            else
            {
                element.MaxWidth = contentWidth;
                element.MaxHeight = contentHeight;
                _contentPresenter.Visibility = Visibility.Visible;
            }
            #endregion
        }

        return base.MeasureOverride(availableSize);
    }

    /// <inheritdoc/>
    protected override void OnPointerEntered(PointerRoutedEventArgs e)
    {
        base.OnPointerEntered(e);

        if ((TableView?.SelectionMode is not ListViewSelectionMode.None
           && TableView?.SelectionUnit is not TableViewSelectionUnit.Row)
           || !TableView.IsReadOnly)
        {
            VisualStates.GoToState(this, false, VisualStates.StatePointerOver);
        }
    }

    /// <inheritdoc/>
    protected override void OnPointerExited(PointerRoutedEventArgs e)
    {
        base.OnPointerExited(e);

        if ((TableView?.SelectionMode is not ListViewSelectionMode.None
            && TableView?.SelectionUnit is not TableViewSelectionUnit.Row)
            || !TableView.IsReadOnly)
        {
            VisualStates.GoToState(this, false, VisualStates.StateNormal);
        }
    }

    /// <inheritdoc/>
    protected override void OnTapped(TappedRoutedEventArgs e)
    {
        base.OnTapped(e);

        if (!TryEndCurrentCellEdit())
        {
            e.Handled = true;
            return;
        }

        if (TableView?.CurrentCellSlot != Slot || TableView?.LastSelectionUnit is TableViewSelectionUnit.Row)
        {
            MakeSelection();
            e.Handled = true;
        }
    }

    /// <inheritdoc/>
    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (!TryEndCurrentCellEdit())
        {
            e.Handled = true;
            return;
        }

        // e.KeyModifiers, never KeyboardHelper, on pointer paths: the tracked key state goes
        // stale when the app is switched away mid-modifier (alt/cmd-tab), turning every later
        // click into a shift-click. The pointer event carries the live OS state.
        if (!e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift) && TableView is not null)
        {
            TableView.SelectionStartCellSlot = TableView.SelectionUnit is not TableViewSelectionUnit.Row || !IsReadOnly ? Slot : default;
            TableView.SelectionStartRowIndex = Index;
#if !WINDOWS
            // Uno: a moving click becomes a manipulation and Tapped never fires, so the
            // click path can't clear the previous selection — the press must (see
            // TableView.OnSelectionDragStart).
            TableView.OnSelectionDragStart(Index, TableView.IsPointerCtrlDown);
#endif
            CapturePointer(e.Pointer);

            // FIX A: ARM the drag (record the start point + scroll baseline) but do not engage
            // it. A plain click (press+release below the drag threshold) must never enter
            // drag-selection state — no rectangle, no ViewChanged, no scroll on release. The
            // first real movement in OnManipulationDelta promotes it via BeginArmedDragSelection.
            var point = e.GetCurrentPoint(this).Position;
            var canvasPoint = TransformPointToCanvas(point);
            if (canvasPoint.HasValue)
            {
                _dragOrigin = canvasPoint.Value;
                TableView.ArmDragSelection(canvasPoint.Value);
            }
        }
    }

    /// <inheritdoc/>
    protected override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (!e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift) && TableView is not null)
        {
            var cell = FindCell(e.GetCurrentPoint(this).Position);
            TableView.SelectionStartCellSlot = TableView.SelectionUnit is not TableViewSelectionUnit.Row || !IsReadOnly ? cell?.Slot : default;
            TableView.SelectionStartRowIndex = cell?.Slot.Row;
        }

        TableView?.EndDragSelection();
        ReleasePointerCaptures();
        _dragOrigin = null;
        _lastHitCell = null;

        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnPointerCaptureLost(PointerRoutedEventArgs e)
    {
        base.OnPointerCaptureLost(e);

        EndGesture();
    }

    /// <inheritdoc/>
    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);

        // The window went to the background mid-press: no release will ever arrive, so end it here or the
        // grid comes back armed for a drag-selection nobody asked for.
        EndGesture();
    }

    /// <summary>Ends whatever gesture was in flight and releases the pointer, however it ended.</summary>
    private void EndGesture()
    {
        TableView?.EndDragSelection();
        ReleasePointerCaptures();
        _dragOrigin = null;
        _lastHitCell = null;
    }

    /// <summary>Where the pointer went down, so a click can be told from a drag.</summary>
    private Point? _dragOrigin;

    /// <summary>
    /// How far the pointer has to travel before a press becomes a drag-selection, in pixels. Below it
    /// the gesture is a click: it selects the row it landed on and nothing else.
    /// </summary>
    private const double DragThreshold = 6;

    /// <summary>True once the pointer has moved far enough from where it went down to mean a drag.</summary>
    private bool HasLeftTheClick(Point position)
    {
        if (_dragOrigin is not { } origin)
        {
            return true;   // no origin recorded (keyboard, programmatic) — behave as before
        }

        if (TransformPointToCanvas(position) is not { } point)
        {
            return true;
        }

        var dx = point.X - origin.X;
        var dy = point.Y - origin.Y;

        return dx * dx + dy * dy >= DragThreshold * DragThreshold;
    }

    /// <inheritdoc/>
    protected override void OnManipulationDelta(ManipulationDeltaRoutedEventArgs e)
    {
        base.OnManipulationDelta(e);

        // A stale capture is not a drag. Tabbing away to another window leaves the pointer captured with
        // no release ever arriving, and the next pointer move over the grid would then carry on selecting
        // as though the button were still down. The origin is cleared whenever a gesture really ends, so
        // its absence means there is no gesture.
        if (PointerCaptures?.Any() is true && _dragOrigin is not null)
        {
            // A click is not a drag. Uno raises a manipulation for a pointer that wobbles a pixel
            // while the button is down, and every one of those used to extend the selection from the
            // pressed row — so an ordinary click on a row selected a range. Nothing is selected, and
            // no drag state is entered, until the pointer has actually travelled past the threshold.
            if (!HasLeftTheClick(e.Position))
            {
                return;
            }

            // FIX A: the first real movement promotes the press armed in OnPointerPressed into an
            // active drag-selection (IsDragSelecting, ViewChanged, rectangle from the original
            // press point). Idempotent — a no-op on later deltas.
            TableView?.BeginArmedDragSelection();

            // Update drag rectangle visual and auto-scroll
            if (TableView?.IsDragSelecting is true)
            {
                var canvasPoint = TransformPointToCanvas(e.Position);
                if (canvasPoint.HasValue)
                {
                    TableView.UpdateDragRectangleVisual(canvasPoint.Value);
                }
            }

            // Selection via FindCell — same proven path whether rectangle is on or off.
            // When the pointer is outside the viewport, FindCell returns null and selection
            // is updated by the ViewChanged handler on the next auto-scroll tick.
            var cell = FindCell(e.Position);

            if (cell is not null && cell.Slot != TableView?.CurrentCellSlot)
            {
                TableView?.MakeSelection(cell.Slot, true, TableView.IsPointerCtrlDown);
            }
        }
    }

    /// <summary>
    /// Tries to end the current edit operation, if any.
    /// </summary>
    /// <returns>True if an edit operation was successfully ended, or there is no edit operation.
    /// False if the current edit operation can not be ended.</returns>
    private bool TryEndCurrentCellEdit()
    {
        if ((TableView?.IsEditing ?? false) &&
             TableView.CurrentCellSlot != Slot &&
             TableView.CurrentCellSlot.HasValue &&
             TableView.GetCellFromSlot(TableView.CurrentCellSlot.Value) is { } currentCell)
        {
            if (!TableView.EndCellEditing(TableViewEditAction.Commit, currentCell)) return false;

            TableView.SetIsEditing(false);
        }

        return true;
    }

    /// <summary>
    /// Gets the height of the horizontal gridlines/>.
    /// </summary>
    private double GetHorizontalGridlineHeight()
    {
        return TableView?.GridLinesVisibility is TableViewGridLinesVisibility.All or TableViewGridLinesVisibility.Horizontal
            ? TableView.HorizontalGridLinesStrokeThickness : 0d;
    }

    // Drag-selection calls FindCell on every manipulation delta, and a full include-all
    // FindElementsInHostCoordinates over the ScrollViewer subtree per pointer move is the hot
    // cost of dragging across a big viewport. Consecutive moves almost always stay inside the
    // same cell, so remember the last hit and its bounds and only re-hit-test once the pointer
    // leaves them — invalidated whenever the view scrolls (offsets are part of the cache) or
    // the gesture ends (containers may recycle between gestures).
    private TableViewCell? _lastHitCell;
    private Rect _lastHitBounds;
    private double _lastHitVerticalOffset;
    private double _lastHitHorizontalOffset;

    /// <summary>
    /// Finds the cell at the specified position.
    /// </summary>
    private TableViewCell? FindCell(Point position)
    {
        _scrollViewer ??= TableView?.FindDescendant<ScrollViewer>();
        if (_scrollViewer is null) return null;

        try
        {
            var transformedPoint = TransformToVisual(null).TransformPoint(position);

            if (_lastHitCell is { IsLoaded: true } cached
                && ReferenceEquals(cached.TableView, TableView)
                && _lastHitVerticalOffset == _scrollViewer.VerticalOffset
                && _lastHitHorizontalOffset == (TableView?.HorizontalOffset ?? 0d)
                && _lastHitBounds.Contains(transformedPoint))
            {
                return cached;
            }

#if WINDOWS
            var cell = VisualTreeHelper.FindElementsInHostCoordinates(transformedPoint, _scrollViewer)
#else
            var cell = VisualTreeHelper.FindElementsInHostCoordinates(transformedPoint, _scrollViewer, true)
                                       .OfType<ContentPresenter>()
                                       .Where(x => x.Name is "Content")
                                       .Select(x => x.FindAscendant<TableViewCell>() is { } header ? header : default)
#endif
                                       .OfType<TableViewCell>()
                                       .FirstOrDefault();

            if (cell is not null)
            {
                _lastHitCell = cell;
                _lastHitBounds = cell.TransformToVisual(null)
                    .TransformBounds(new Rect(0, 0, cell.ActualWidth, cell.ActualHeight));
                _lastHitVerticalOffset = _scrollViewer.VerticalOffset;
                _lastHitHorizontalOffset = TableView?.HorizontalOffset ?? 0d;
            }

            return cell;
        }
        catch (ArgumentException)
        {
            // Element not in the visual tree during container recycling.
            return null;
        }
    }

    /// <summary>
    /// Transforms a point relative to this cell to coordinates relative to the drag rectangle canvas.
    /// </summary>
    private Point? TransformPointToCanvas(Point position)
    {
        if (TableView?.DragRectangleCanvas is null) return null;

        try
        {
            var transform = TransformToVisual(TableView.DragRectangleCanvas);
            return transform.TransformPoint(position);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <inheritdoc/>
    protected override void OnDoubleTapped(DoubleTappedRoutedEventArgs e)
    {
        var eventArgs = new TableViewCellDoubleTappedEventArgs(Slot, this, Row?.Content);
        TableView?.OnCellDoubleTapped(eventArgs);
        e.Handled = eventArgs.Handled;

        if (e.Handled) return;

        base.OnDoubleTapped(e);

        if (!IsReadOnly && TableView is not null && !TableView.IsEditing && !Column?.UseSingleElement is true)
        {
            e.Handled = BeginCellEditing(e);
        }
        else
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// Makes a selection based on the current cell.
    /// </summary>
    private void MakeSelection()
    {
        // Called from Tapped, which carries no modifiers — the press/release that produced the
        // tap stamped TableView.LastPointerKeyModifiers, so that is the live state here.
        var shiftKey = TableView?.IsPointerShiftDown ?? false;
        var ctrlKey = TableView?.IsPointerCtrlDown ?? false;

        if (TableView is null || Column is null)
        {
            return;
        }

        if ((TableView.IsEditing || Column.UseSingleElement) && IsCurrent)
        {
            return;
        }

        if (IsSelected && (ctrlKey || TableView.SelectionMode is ListViewSelectionMode.Multiple) && !shiftKey)
        {
            TableView.DeselectCell(Slot);
        }
        else
        {
            if (Column.UseSingleElement)
            {
                TableView.DeselectCell(Slot);
            }

            TableView.MakeSelection(Slot, shiftKey, ctrlKey);
        }

        TableView.SetIsEditing(false);
    }

    /// <summary>
    /// Initiates editing mode for the current cell, raising the beginning edit event and allowing cancellation.
    /// </summary>
    /// <param name="editingArgs">The event data associated with the editing request. Cannot be null.</param>
    /// <returns>A task that represents the asynchronous operation. The task result is <see langword="true"/> if cell editing was
    /// successfully started; otherwise, <see langword="false"/> if the operation was canceled.</returns>
    internal bool BeginCellEditing(RoutedEventArgs editingArgs)
    {
        var args = new TableViewBeginningEditEventArgs(this, Row?.Content, Column!, editingArgs);
        TableView?.OnBeginningEdit(args);

        if (!args.Cancel)
        {
            PrepareForEdit(editingArgs);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Prepares the cell for editing.
    /// </summary>
    internal void PrepareForEdit(RoutedEventArgs editingArgs)
    {
        var editingElement = SetEditingElement();
        Content = editingElement;

        if (TableView is not null)
        {
            TableView.SetIsEditing(true);
            TableView.UpdateCornerButtonState();
        }

        if (editingElement is { IsHitTestVisible: true })
        {
            _editingArgs = editingArgs;
            editingElement.Loaded += OnEditingElementLoaded;
        }
    }

    private void OnEditingElementLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement editingElement)
        {
            editingElement.Loaded -= OnEditingElementLoaded;
            editingElement.Focus(FocusState.Pointer);
            _editingArgs ??= new RoutedEventArgs();

            var args = new TableViewPreparingCellForEditEventArgs(this, Row?.Content, Column!, editingElement, _editingArgs);
            _uneditedValue = Column?.PrepareCellForEdit(this, _editingArgs);
            TableView?.OnPreparingCellForEdit(args);
        }
    }

    /// <summary>
    /// Sets the editing element for the cell.
    /// </summary>
    private FrameworkElement? SetEditingElement()
    {
        if (Column?.UseSingleElement ?? false)
        {
            return Content as FrameworkElement;
        }
        else
        {
            var element = Column?.GenerateEditingElement(this, Row?.Content);

            if (element is not null && Column is TableViewBoundColumn { EditingElementStyle: { } } boundColumn)
            {
                element.Style = boundColumn.EditingElementStyle;
            }

            return element;
        }
    }

    internal void EndEditing(TableViewEditAction editAction)
    {
        Column?.EndCellEditing(this, Row?.Content, editAction, _uneditedValue);
        SetElement();
    }

    /// <summary>
    /// Sets the element for the cell.
    /// </summary>
    internal void SetElement()
    {
        var element = Column?.GenerateElement(this, Row?.Content);

        if (element is not null && Column is TableViewBoundColumn { ElementStyle: { } } boundColumn)
        {
            element.Style = boundColumn.ElementStyle;
        }

        Content = element;

#if !WINDOWS
        // FIX G: only steal focus for the current cell. SetElement runs on every cell element
        // generation (realization/reorder), and focusing each freshly generated element — with
        // BringIntoViewOnFocusChange — fought the scroll and stole focus from wherever the user
        // was. Re-check after the delay in case the current cell moved on between enqueue and delay.
        if (IsCurrent)
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                await Task.Delay(20);
                if (IsCurrent)
                {
                    Focus(FocusState.Pointer);
                }
            });
        }
#endif

        // Deferring the measure to a dispatcher tick leaves the freshly-generated element zero-sized for a
        // frame, which on Uno WASM shows as a blank cell that fills a beat later during a fast scroll. When the
        // TableView has a fixed RowHeight the layout is trivial and uniform, so measure now and render in place.
        // Variable-height grids (e.g. the tree grid) keep the deferred path to avoid re-entrant measure churn.
        if (TableView is { } tableView && !double.IsNaN(tableView.RowHeight))
        {
            InvalidateMeasure();
        }
        else
        {
            DispatcherQueue.TryEnqueue(InvalidateMeasure);
        }
    }

    /// <summary>
    /// Refreshes the element for the cell.
    /// </summary>
    internal void RefreshElement()
    {
        Column?.RefreshElement(this, Row?.Content);
    }

    /// <summary>
    /// Applies the selection state to the cell.
    /// </summary>
    internal void ApplySelectionState()
    {
        var stateName = IsSelected ? VisualStates.StateSelected : VisualStates.StateUnselected;
        VisualStates.GoToState(this, false, stateName);
    }

    /// <summary>
    /// Applies the current cell state to the cell.
    /// </summary>
    internal async void ApplyCurrentCellState(bool skipFocus = false)
    {
        var stateName = IsCurrent ? VisualStates.StateCurrent : VisualStates.StateRegular;
        VisualStates.GoToState(this, false, stateName);

        if (IsCurrent && !skipFocus)
        {
            Focus(FocusState.Pointer);

            await Task.Delay(20);
            if (Content is UIElement { IsHitTestVisible: true } element)
            {
                element.Focus(FocusState.Pointer);
            }
        }
    }

    /// <summary>
    /// Updates the element state for the cell.
    /// </summary>
    internal void UpdateElementState()
    {
        Column?.UpdateElementState(this, Row?.Content);
    }

    /// <summary>
    /// Handles changes to the column.
    /// </summary>
    private void OnColumnChanged()
    {
        if (TableView?.IsEditing == true)
        {
            SetEditingElement();
        }
        else
        {
            SetElement();
        }
    }

    /// <summary>
    /// Ensures grid lines are applied to the cell.
    /// </summary>
    internal void EnsureGridLines()
    {
        if (_v_gridLine is not null && TableView is not null)
        {
            _v_gridLine.Fill = TableView.GridLinesVisibility is TableViewGridLinesVisibility.All or TableViewGridLinesVisibility.Vertical
                               ? TableView.VerticalGridLinesStroke : new SolidColorBrush(Colors.Transparent);
            _v_gridLine.Width = TableView.VerticalGridLinesStrokeThickness;
            _v_gridLine.Visibility = TableView.HeaderGridLinesVisibility is TableViewGridLinesVisibility.All or TableViewGridLinesVisibility.Vertical
                                     || TableView.GridLinesVisibility is TableViewGridLinesVisibility.All or TableViewGridLinesVisibility.Vertical
                                     ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Ensures the correct style is applied to the cell.
    /// </summary>
    /// <param name="item">The data item associated with the cell.</param>
    internal void EnsureStyle(object? item)
    {
        _cellStyles ??= [
            .. Column?.ConditionalCellStyles ?? [], // Column styles have first priority
            .. TableView?.ConditionalCellStyles ?? []]; // TableView styles have second priority

        Style = _cellStyles.FirstOrDefault(c => c.Predicate?.Invoke(new(Column!, item)) is true)?
                          .Style ?? Column?.CellStyle ?? TableView?.CellStyle;

        // Per-row tooltip producer on the column lets callers surface
        // row-specific diagnostic text (validation errors, etc.) without
        // subclassing the cell or rewriting the column as a template column.
        var tip = Column?.GetCellToolTip?.Invoke(item);
        ToolTipService.SetToolTip(this, string.IsNullOrEmpty(tip) ? null : tip);
    }

    /// <summary>
    /// Gets a value indicating whether the cell is read-only. Combines the
    /// TableView-wide flag, the column-level flag, the per-row callback on
    /// the column (<see cref="TableViewColumn.IsCellReadOnlyForRow"/>) and
    /// the template-column "no editing template" shortcut.
    /// </summary>
    public bool IsReadOnly => TableView?.IsReadOnly is true
        || Column is TableViewTemplateColumn { EditingTemplate: null, EditingTemplateSelector: null } or { IsReadOnly: true }
        || (Column?.IsCellReadOnlyForRow?.Invoke(DataContext) ?? false);

    /// <summary>
    /// Gets the slot for the cell.
    /// </summary>
    public TableViewCellSlot Slot => new(Row?.Index ?? -1, Index);

    /// <summary>
    /// Gets or sets the index of the cell.
    /// </summary>
    internal int Index { get; set; }

    /// <summary>
    /// Gets a value indicating whether the cell is selected.
    /// </summary>
    public bool IsSelected => TableView?.SelectedCells.Contains(Slot) is true;

    /// <summary>
    /// Gets a value indicating whether the cell is the current cell.
    /// </summary>
    public bool IsCurrent => TableView?.CurrentCellSlot == Slot;

    /// <summary>
    /// Gets or sets the column for the cell.
    /// </summary>
    public TableViewColumn? Column
    {
        get;
        internal set
        {
            if (field != value)
            {
                field = value;
                OnColumnChanged();
            }
        }
    }

    /// <summary>
    /// Gets or sets the row for the cell.
    /// </summary>
    public TableViewRow? Row { get; internal set; }

    /// <summary>
    /// Gets or sets the TableView for the cell.
    /// </summary>
    public TableView? TableView { get; internal set; }

    /// <inheritdoc/>
    protected override AutomationPeer OnCreateAutomationPeer()
    {
        return new AutomationPeers.TableViewCellAutomationPeer(this);
    }
}
