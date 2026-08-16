using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Windows.Foundation;
using WinUI.TableView.Extensions;
using WinUI.TableView.Helpers;

namespace WinUI.TableView;

/// <summary>
/// Represents a row in a TableView.
/// </summary>

#if WINDOWS
[WinRT.GeneratedBindableCustomProperty]
#endif
public partial class TableViewRow : ListViewItem
{
    private const string Selection_Background = "SelectionBackground";
    private const double Selection_IndicatorHeight = 16d;
    private const string Check_Mark = "\uE73E";
    private Thickness _focusVisualMargin = new(1);
    private readonly Thickness _selectionBackgroundMargin = new(4, 2, 4, 2);
    private readonly Thickness _selectionIndicatorMargin = new(4, 0, 0, 0);
    private ListViewItemPresenter? _itemPresenter;
    private Border? _selectionBackground;
    private bool _ensureCells = true;
    private Brush? _cellPresenterBackground;
    private Brush? _cellPresenterForeground;

    /// <summary>
    /// Initializes a new instance of the TableViewRow class.
    /// </summary>
    public TableViewRow()
    {
        DefaultStyleKey = typeof(TableViewRow);

        SizeChanged += OnSizeChanged;
        Loaded += TableViewRow_Loaded;
#if WINDOWS
        ContextRequested += OnContextRequested;
        RegisterPropertyChangedCallback(IsSelectedProperty, delegate { OnIsSelectedChanged(); });
#endif
        RegisterPropertyChangedCallback(ForegroundProperty, delegate { OnForegroundChanged(); });
        RegisterPropertyChangedCallback(BackgroundProperty, delegate { OnBackgroundChanged(); });
    }

    /// <inheritdoc/>
    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size availableSize)
    {
        // Which grid the static counters are about. A scroll that only re-measures realizes nothing,
        // so the prepare path alone would leave the watch reporting whichever grid last built a row.
        if (TableView is { } owner)
        {
            TableView.DiagActiveGrid = owner;
        }

        var diagT0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var size = base.MeasureOverride(availableSize);
        TableView.DiagRowMeasureTicks += System.Diagnostics.Stopwatch.GetTimestamp() - diagT0;
        return size;
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

        // Select the row before showing the Context Menu
        if (TableView is not null && TableView.ForceRowOrCellSelectionOnContextRequested && !IsSelected)
        {
            TableView.MakeSelection(new TableViewCellSlot(Index, -1), false);
        }

        e.Handled = TableView?.ShowRowContext(this, position) is true;
    }

#if WINDOWS
    /// <summary>
    /// Handles the IsSelected property changed.
    /// </summary>
    private void OnIsSelectedChanged()
    {
        EnsureLayout();
        RowPresenter?.SetRowDetailsVisibility();
    }
#endif

    /// <summary>
    /// Handles the Foreground property changed.
    /// </summary>
    private void OnForegroundChanged()
    {
        _cellPresenterForeground = Foreground;
        EnsureAlternateColors();
    }

    /// <summary>
    /// Handles the Background property changed.
    /// </summary>
    private void OnBackgroundChanged()
    {
        _cellPresenterBackground = Background;
        EnsureAlternateColors();
    }

    /// <summary>
    /// Handles the Loaded event.
    /// </summary>
    private void TableViewRow_Loaded(object sender, RoutedEventArgs e)
    {
        _focusVisualMargin = FocusVisualMargin;

        RowPresenter?.EnsureGridLines();
        EnsureLayout();
    }

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _cellPresenterBackground = Background;
        _cellPresenterForeground = Foreground;
        _itemPresenter = GetTemplateChild("Root") as ListViewItemPresenter;
#if !WINDOWS
        RowPresenter = GetTemplateChild("RowPresenter") as TableViewRowPresenter;
        _selectionBackground = GetTemplateChild("SelectionBackground") as Border;
#endif
    }

    /// <inheritdoc/>
    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);

        // Bound to a different item: whatever index this container used to be at, it is not
        // there now, and a parked cell still shows the old item's value.
        InvalidateIndex();
        ClearParkedCells();

        if (_ensureCells)
        {
            EnsureCells();
        }
        else
        {
            // On the light path this is one call for the whole row rather than a RefreshElement per
            // cell: the panel writes each column's value straight into the TextBlock showing it.
            RefreshCells(newContent);
        }

        RowPresenter?.InvalidateMeasure(); // The cells presenter does not measure every time.
        TableView?.EnsureAlternateRowColors();
    }

    /// <inheritdoc/>
    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        if (TableView is { IsEditing: false })
        {
            base.OnPointerPressed(e);
        }

        if (!e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift) && TableView is not null)
        {
            TableView.SelectionStartRowIndex = Index;
        }
    }

    /// <inheritdoc/>
    protected override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (!e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift) && TableView is not null)
        {
            TableView.SelectionStartCellSlot = null;
            TableView.SelectionStartRowIndex = Index;
        }
    }

    /// <inheritdoc/>
    protected override void OnTapped(TappedRoutedEventArgs e)
    {
        base.OnTapped(e);

        if (TableView?.SelectionUnit is TableViewSelectionUnit.Row or TableViewSelectionUnit.CellOrRow or TableViewSelectionUnit.CellWithRow)
        {
            TableView.CurrentRowIndex = Index;
            TableView.LastSelectionUnit = TableViewSelectionUnit.Row;
        }
    }

    /// <inheritdoc/>
    protected override void OnDoubleTapped(DoubleTappedRoutedEventArgs e)
    {
        var eventArgs = new TableViewRowDoubleTappedEventArgs(Index, this, Content);
        TableView?.OnRowDoubleTapped(eventArgs);
        e.Handled = eventArgs.Handled;

        base.OnDoubleTapped(e);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        finalSize = base.ArrangeOverride(finalSize);

        var cornerRadius = _itemPresenter?.CornerRadius ?? new();
        var left = Math.Max(cornerRadius.TopLeft, cornerRadius.BottomLeft);

        _itemPresenter?.Arrange(new Rect(-left, 0, _itemPresenter.ActualWidth + left, _itemPresenter.ActualHeight));

        return finalSize;
    }

    /// <summary>Pushes the TableView's current row heights onto this row's cells. Called when one of
    /// them actually changes, which is what the per-cell bindings used to wait around for.</summary>
    internal void ApplyRowHeights()
    {
        if (TableView is null)
        {
            return;
        }

        if (IsLight)
        {
            // The light panel takes the row's height from the grid rather than from its children,
            // so re-showing the item is what makes it read the new one.
            RowPresenter?.ShowLightCells(Content);
            return;
        }

        foreach (var cell in Cells)
        {
            cell.Height = TableView.RowHeight;
            cell.MaxHeight = TableView.RowMaxHeight;
            cell.MinHeight = TableView.RowMinHeight;
        }
    }

    /// <summary>
    /// Ensures cells are created for the row.
    /// </summary>
    internal void EnsureCells()
    {
        if (TableView is null)
        {
            return;
        }

        if (RowPresenter is not null && _ensureCells)
        {
            if (IsLight)
            {
                RowPresenter.EnsureLightCells();
                AdoptColumnGeometry();                  // which is what brings the panel in line
                RowPresenter.ShowLightCells(Content);
                _ensureCells = false;
                return;
            }

            RowPresenter.ClearCells();
            ClearParkedCells();

            AddCells(TableView.Columns.VisibleColumns);
            AdoptColumnGeometry();
            _ensureCells = false;
        }
    }

    /// <summary>Whether this row draws its columns itself rather than building a cell for each.</summary>
    internal bool IsLight => TableView?.AreRowsLight is true;

    /// <summary>Builds this light row's set of columns again and fills it, for when the columns
    /// themselves moved: one hidden, one resized, one dragged somewhere else.</summary>
    private void RefreshLightColumns()
    {
        AdoptColumnGeometry();
        RowPresenter?.ShowLightCells(Content);
    }

    /// <summary>
    /// Re-reads this row's values from the item it is showing, for when that item says one of them
    /// changed. Text columns write their value rather than bind it, so nothing else would notice.
    /// </summary>
    internal void RefreshCells(object? item)
    {
        // The presenter's own answer, not the grid's: this runs from OnContentChanged, and a row is
        // re-bound before it is told which grid it belongs to.
        if (RowPresenter is { HasLightCells: true } presenter)
        {
            presenter.ShowLightCells(item);
            return;
        }

        foreach (var cell in Cells)
        {
            cell.RefreshElement();
        }
    }

    /// <summary>
    /// Throws this row's cells away and builds the ones the current column range calls for. Called
    /// when that range moves — a horizontal scroll that crossed a column boundary.
    /// </summary>
    internal void RebuildCells()
    {
        _ensureCells = true;
        EnsureCells();
    }

    /// <summary>
    /// Handles the SizeChanged event.
    /// </summary>
    private async void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (TableView?.CurrentCellSlot?.Row == Index)
        {
            _ = await TableView.ScrollCellIntoView(TableView.CurrentCellSlot.Value);
        }
    }

    /// <summary>
    /// Handles the collection changed event for the columns.
    /// </summary>
    private void OnColumnsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (IsLight)
        {
            RefreshLightColumns();
            return;
        }

        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems?.OfType<TableViewColumn>() is IEnumerable<TableViewColumn> newItems)
        {
            AddCells(newItems.Where(x => x.Visibility == Visibility.Visible));
        }
        else if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems?.OfType<TableViewColumn>() is IEnumerable<TableViewColumn> oldItems)
        {
            RemoveCells(oldItems);
        }
        else if (e.Action == NotifyCollectionChangedAction.Move && e.NewItems?.Count > 0)
        {
            RowPresenter?.MoveCells(e.NewItems.OfType<TableViewColumn>().First(), e.NewStartingIndex);
        }
        else if (e.Action == NotifyCollectionChangedAction.Reset && RowPresenter is not null)
        {
            RowPresenter.ClearCells();
        }
    }

    /// <summary>
    /// Handles the property changed event for a column.
    /// </summary>
    private void OnColumnPropertyChanged(object? sender, TableViewColumnPropertyChangedEventArgs e)
    {
        if (IsLight)
        {
            // Every branch below reaches into a cell to change one thing about it. A light row has no
            // cell to reach into for its text columns: where the columns are and what they say is
            // worked out from the collection each time it is asked. So all of these are the same
            // answer — ask again.
            //
            // IsReadOnly is not among them: a grid that is not read-only has no light rows to begin
            // with, which is what the gate says.
            if (e.PropertyName is nameof(TableViewColumn.Visibility)
                or nameof(TableViewColumn.Order)
                or nameof(TableViewColumn.IsFrozen)
                or nameof(TableViewColumn.ActualWidth)
                or nameof(TableViewColumn.CellStyle)
                or nameof(TableViewBoundColumn.ElementStyle))
            {
                RefreshLightColumns();
            }

            return;
        }

        if (e.PropertyName is nameof(TableViewColumn.Visibility))
        {
            if (e.Column.Visibility == Visibility.Visible)
            {
                AddCells([e.Column]);
            }
            else
            {
                RemoveCells([e.Column]);
            }
        }
        else if ((e.PropertyName is nameof(TableViewColumn.Order) ||
            e.PropertyName is nameof(TableViewColumn.IsFrozen)) &&
            e.Column.Visibility is Visibility.Visible)
        {
            RemoveCells([e.Column]);
            AddCells([e.Column]);
        }
        else if (e.PropertyName is nameof(TableViewColumn.ActualWidth))
        {
            if (Cells.FirstOrDefault(x => x.Column == e.Column) is { } cell)
            {
                cell.Width = e.Column.ActualWidth;
            }
        }
        else if (e.PropertyName is nameof(TableViewColumn.IsReadOnly))
        {
            UpdateCellsState();
        }
        else if (e.PropertyName is nameof(TableViewColumn.CellStyle))
        {
            EnsureCellsStyle(e.Column);
        }
        else if (e.PropertyName is nameof(TableViewBoundColumn.ElementStyle))
        {
            EnsureElementStyle(e.Column);
        }
        else if (e.PropertyName is nameof(TableViewBoundColumn.EditingElementStyle))
        {
            EnsureEditingElementStyle(e.Column);
        }
    }

    /// <summary>
    /// Removes cells for the specified columns.
    /// </summary>
    private void RemoveCells(IEnumerable<TableViewColumn> columns)
    {
        if (RowPresenter is not null)
        {
            foreach (var column in columns)
            {
                var cell = RowPresenter.Cells.FirstOrDefault(x => x.Column == column);
                if (cell is not null)
                {
                    RowPresenter.RemoveCell(cell);
                }
            }
        }
    }

    /// <summary>
    /// Adds cells for the specified columns.
    /// </summary>
    private void AddCells(IEnumerable<TableViewColumn> columns)
    {
        if (RowPresenter is not null && TableView is not null)
        {
            // Read once, not once per cell: this is a projection that rebuilds on access, and asking
            // it for each cell's own index made realizing a row quadratic in its column count.
            var visible = TableView.Columns.VisibleColumns;

            foreach (var column in columns)
            {
                AddCell(column, visible.IndexOf(column));
            }
        }
    }

    /// <summary>
    /// Builds this row's cell for one column, if that column is one this row should be showing.
    /// </summary>
    /// <param name="column">The column to build a cell for.</param>
    /// <param name="index">Its index among the visible columns.</param>
    private void AddCell(TableViewColumn column, int index)
    {
        if (RowPresenter is null || TableView is null)
        {
            return;
        }

        // Off screen and not frozen: no cell. Every path that adds one comes through here, so a
        // column made visible or reordered while scrolled away stays unbuilt too.
        if (!TableView.IsColumnRealized(column, index))
        {
            return;
        }

        var cell = new TableViewCell
        {
            Row = this,
            Column = column,
            TableView = TableView,
            Index = index,
            Width = column.ActualWidth
        };

        // Set, not bound. These three carried a live binding each — three per cell, and a grid with
        // nineteen columns realizes nineteen cells a row, so a viewport was holding thousands of
        // bindings whose whole job was to relay a number that does not change while you scroll. The
        // TableView pushes the new value on the rare occasion one of them does (see
        // ApplyRowHeights).
        cell.Height = TableView.RowHeight;
        cell.MaxHeight = TableView.RowMaxHeight;
        cell.MinHeight = TableView.RowMinHeight;

        RowPresenter.InsertCell(cell);
    }

    /// <summary>
    /// Brings this row's cells in line with the column range as it stands now: builds the ones that
    /// have scrolled into view, drops the ones that have scrolled out, and leaves everything in
    /// between alone.
    ///
    /// <para>Crossing one column boundary changes one cell per row. Throwing the row away and
    /// rebuilding it instead — which is what this replaced — rebuilt all twenty-three of them, and a
    /// drag across the grid crosses a boundary every few pixels, so the cost of scrolling sideways
    /// became the cost of rebinding the entire viewport several dozen times.</para>
    /// </summary>
    internal void SyncCells()
    {
        if (TableView is null || RowPresenter is null)
        {
            return;
        }

        if (_ensureCells)
        {
            EnsureCells();   // nothing built yet: the from-scratch path is the cheaper one
            return;
        }

        if (IsLight)
        {
            AdoptColumnGeometry();
            RowPresenter.ShowLightCells(Content);
            return;
        }

        SyncCellsCore();
        AdoptColumnGeometry();
    }

    /// <summary>Takes on the grid's current column geometry, which is only true of this row once
    /// its cells match the range that geometry describes.</summary>
    internal void AdoptColumnGeometry()
    {
        if (TableView is null || RowPresenter is null)
        {
            return;
        }

        if (IsLight)
        {
            // A light row keeps its own record of where each column sits, because it arranges them
            // rather than stacking them. A width that moved has to reach that record before the row
            // takes on the geometry describing it, or every column lands under the wrong header.
            RowPresenter.SyncLightCells();
        }

        RowPresenter.CellsInset = TableView.ColumnRangeInset;
        RowPresenter.CellsHiddenWidth = TableView.ColumnRangeHiddenWidth;
        RowPresenter.InvalidateMeasure();
        RowPresenter.InvalidateArrange();
    }

    private void SyncCellsCore()
    {
        if (TableView is null || RowPresenter is null)
        {
            return;
        }

        List<TableViewCell>? leaving = null;
        var held = new HashSet<TableViewColumn>();

        foreach (var cell in Cells)
        {
            if (cell.Column is { } column && TableView.IsColumnRealized(column, cell.Index))
            {
                held.Add(column);
            }
            else
            {
                (leaving ??= []).Add(cell);
            }
        }

        if (leaving is not null)
        {
            foreach (var cell in leaving)
            {
                RowPresenter.RemoveCell(cell);
                Park(cell);
            }
        }

        var visible = TableView.Columns.VisibleColumns;

        for (var i = 0; i < visible.Count; i++)
        {
            var column = visible[i];
            if (!TableView.IsColumnRealized(column, i) || held.Contains(column))
            {
                continue;
            }

            if (Unpark(column) is { } parked)
            {
                parked.Index = i;
                parked.Width = column.ActualWidth;
                RowPresenter.InsertCell(parked);
            }
            else
            {
                AddCell(column, i);
            }
        }
    }

    /// <summary>
    /// Cells for columns that have scrolled out of view, kept out of the tree rather than thrown
    /// away.
    ///
    /// <para>Reading a grid sideways is not a one-way trip: people nudge it a column or two and
    /// come back. Rebuilding those cells every time was most of what made a horizontal drag cost
    /// more than the vertical one it was meant to speed up — and a cell that is out of the visual
    /// tree costs nothing to measure or arrange, which is what the virtualization was for.</para>
    /// </summary>
    private List<TableViewCell>? _parkedCells;

    /// <summary>How many. Enough to cover a nudge either side of the viewport; a reader who has
    /// gone further than that has left those columns behind.</summary>
    private const int ParkedCellLimit = 8;

    private void Park(TableViewCell cell)
    {
        if (cell.Column is null)
        {
            return;
        }

        _parkedCells ??= [];
        _parkedCells.Add(cell);

        while (_parkedCells.Count > ParkedCellLimit)
        {
            _parkedCells.RemoveAt(0);   // the one parked longest ago is the one least likely to return
        }
    }

    private TableViewCell? Unpark(TableViewColumn column)
    {
        if (_parkedCells is null)
        {
            return null;
        }

        for (var i = 0; i < _parkedCells.Count; i++)
        {
            if (_parkedCells[i].Column == column)
            {
                var cell = _parkedCells[i];
                _parkedCells.RemoveAt(i);
                return cell;
            }
        }

        return null;
    }

    /// <summary>Empties the park, for when what is in it can no longer be trusted — this row is
    /// showing a different item, or its cells are being rebuilt from scratch.</summary>
    private void ClearParkedCells() => _parkedCells?.Clear();

    /// <summary>
    /// Handles the TableView changing event.
    /// </summary>
    private void OnTableViewChanging()
    {
        if (TableView is not null)
        {
            TableView.IsReadOnlyChanged -= OnTableViewIsReadOnlyChanged;

            if (TableView.Columns is not null)
            {
                TableView.Columns.CollectionChanged -= OnColumnsCollectionChanged;
                TableView.Columns.ColumnPropertyChanged -= OnColumnPropertyChanged;
            }
        }
    }

    /// <summary>
    /// Handles the TableView changed event.
    /// </summary>
    private void OnTableViewChanged()
    {
        if (TableView is not null)
        {
            TableView.IsReadOnlyChanged += OnTableViewIsReadOnlyChanged;

            if (TableView.Columns is not null)
            {
                TableView.Columns.CollectionChanged += OnColumnsCollectionChanged;
                TableView.Columns.ColumnPropertyChanged += OnColumnPropertyChanged;
            }
        }
    }

    /// <summary>
    /// Handles the IsReadOnly property changed event for the TableView.
    /// </summary>
    private void OnTableViewIsReadOnlyChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UpdateCellsState();
    }

    /// <summary>
    /// Updates the state of the cells.
    /// </summary>
    private void UpdateCellsState()
    {
        foreach (var cell in Cells)
        {
            cell.UpdateElementState();
        }
    }

    private void EnsureElementStyle(TableViewColumn column)
    {
        foreach (var cell in Cells)
        {
            if (cell.Column == column
                && cell.Content is FrameworkElement element
                && cell.Column is TableViewBoundColumn boundColumn
                && (TableView?.IsEditing is false || TableView?.CurrentCellSlot != cell.Slot))
            {
                element.Style = boundColumn.ElementStyle;
            }
        }
    }

    private void EnsureEditingElementStyle(TableViewColumn column)
    {
        if (TableView?.IsEditing is true
            && TableView.CurrentCellSlot is not null
            && column is TableViewBoundColumn boundColumn
            && TableView.GetCellFromSlot(TableView.CurrentCellSlot.Value) is { } cell
            && cell.Column == column
            && cell.Content is FrameworkElement element)
        {
            element.Style = boundColumn.EditingElementStyle;
        }
    }

    /// <summary>
    /// Ensures the cells style is applied.
    /// </summary>
    internal void EnsureCellsStyle(TableViewColumn? column = null, object? dataItem = null)
    {
        var cells = Cells.Where(x => column is null || x.Column == column);

        foreach (var cell in cells)
        {
            cell.EnsureStyle(dataItem ?? Content);
        }
    }

    /// <summary>
    /// Applies the current cell state to the specified slot.
    /// </summary>
    internal void ApplyCurrentCellState(TableViewCellSlot slot)
    {
        CellForColumn(slot.Column)?.ApplyCurrentCellState();
    }

    /// <summary>
    /// This row's cell for a column, by the column's index among the visible columns.
    /// </summary>
    /// <remarks>
    /// Never by position in <see cref="Cells"/>. That list holds the frozen cells first and, when
    /// columns are virtualized, only the ones on screen — so column N is not at position N in either
    /// case, and indexing it either returns another column's cell or walks off the end.
    /// </remarks>
    /// <returns>The cell, or null when this row has not built one for that column — which under
    /// column virtualization is the normal state of every column off screen.</returns>
    internal TableViewCell? CellForColumn(int visibleIndex)
    {
        foreach (var cell in Cells)
        {
            if (cell.Index == visibleIndex)
            {
                return cell;
            }
        }

        return null;
    }

    /// <summary>
    /// Applies the selection state to the cells.
    /// </summary>
    internal void ApplyCellsSelectionState()
    {
        foreach (var cell in Cells)
        {
            cell.ApplySelectionState();
        }
    }

    /// <summary>
    /// Ensures the layout of the row.
    /// </summary>
    internal void EnsureLayout()
    {
        var cornerRadius = _itemPresenter?.CornerRadius ?? new();
        var left = Math.Max(cornerRadius.TopLeft, cornerRadius.BottomLeft) / 2;
        var detailsHeight = RowPresenter?.GetDetailsContentHeight() ?? 0d;
#if WINDOWS
        var selectionIndicator = _itemPresenter?.FindDescendants()
                                                .OfType<Border>()
                                                .FirstOrDefault(x => x is { Width: 3 });

        var cellsHeight = ActualHeight - detailsHeight;
        var selectionIndicatorHeight = Math.Max(Selection_IndicatorHeight, cellsHeight - 40);

        if (selectionIndicator is not null)
        {
            selectionIndicator.MaxHeight = selectionIndicatorHeight;
            selectionIndicator.Margin = new Thickness(
                _selectionIndicatorMargin.Left + left,
                _selectionIndicatorMargin.Top,
                _selectionIndicatorMargin.Right,
                _selectionIndicatorMargin.Bottom);
        }

        if (TableView is ListView { SelectionMode: ListViewSelectionMode.Multiple })
        {
            var fontIcon = this.FindDescendant<FontIcon>(x => x.Glyph == Check_Mark);
            selectionIndicator = fontIcon?.Parent as Border;
        }

        if (TableView is ListView { SelectionMode: ListViewSelectionMode.Multiple })
        {
            var fontIcon = this.FindDescendant<FontIcon>(x => x.Glyph == Check_Mark);
            selectionIndicator = fontIcon?.Parent as Border;
        }


        _selectionBackground ??= _itemPresenter?.FindDescendants()
                                                .OfType<Border>()
                                                .FirstOrDefault(x => x.Name is not Selection_Background && x.Margin == _selectionBackgroundMargin);

        FocusVisualMargin = new Thickness(
            _focusVisualMargin.Left + left,
            _focusVisualMargin.Top,
            _focusVisualMargin.Right,
            _focusVisualMargin.Bottom + GetHorizontalGridlineHeight());

        EnsureSelectionIndicatorPosition(detailsHeight, selectionIndicator);
#endif
        if (_selectionBackground is not null)
        {
            _selectionBackground.Name = Selection_Background;
            _selectionBackground.Margin = new Thickness(
                _selectionBackgroundMargin.Left + left,
                _selectionBackgroundMargin.Top,
                _selectionBackgroundMargin.Right,
                _selectionBackgroundMargin.Bottom + GetHorizontalGridlineHeight() + detailsHeight);
        }
    }

    /// <summary>
    /// Ensures the position of the selection indicator.
    /// </summary>
    private async void EnsureSelectionIndicatorPosition(double detailsHeight, Border? selectionIndicator)
    {
        await Task.Yield(); // let the animations and visual state changes complete

        if (selectionIndicator is not null)
        {
            // Assign a TranslateTransform for animation
            var translateTransform = new TranslateTransform();
            selectionIndicator.RenderTransform = translateTransform;

            var toValue = RowPresenter?.IsDetailsPanelVisible ?? false ? Math.Round(-detailsHeight / 2) : 0; // move up or down

            var animation = new DoubleAnimation
            {
                To = toValue,
                Duration = new Duration(TimeSpan.Zero)
            };

            var storyboard = new Storyboard();
            Storyboard.SetTarget(animation, translateTransform);
            Storyboard.SetTargetProperty(animation, "Y"); // vertical movement
            storyboard.Children.Add(animation);

            storyboard.Begin();
        }
    }

    /// <summary>
    /// Ensures alternate colors are applied to the row.
    /// </summary>
    internal void EnsureAlternateColors()
    {
        if (TableView is null || RowPresenter is null) return;

        RowPresenter.Background =
            Index % 2 == 1 && TableView.AlternateRowBackground is not null ? TableView.AlternateRowBackground : _cellPresenterBackground;

        RowPresenter.Foreground =
            Index % 2 == 1 && TableView.AlternateRowForeground is not null ? TableView.AlternateRowForeground : _cellPresenterForeground;
    }

    internal void UpdateSelectCheckMarkOpacity()
    {
        var fontIcon = this.FindDescendant<FontIcon>(x => x.Glyph == Check_Mark);

        if (fontIcon?.Parent is Border border)
        {
            border.Opacity = TableView?.IsEditing is true ? 0.3 : 1;
        }
    }

    /// <summary>
    /// Gets the height of the horizontal gridlines.
    /// </summary>
    private double GetHorizontalGridlineHeight()
    {
        return TableView?.GridLinesVisibility is TableViewGridLinesVisibility.All or TableViewGridLinesVisibility.Horizontal
            ? TableView.HorizontalGridLinesStrokeThickness : 0d;
    }

    /// <summary>
    /// Gets the list of cells in the row.
    /// </summary>
    public IReadOnlyList<TableViewCell> Cells => RowPresenter?.Cells ?? [];

    /// <summary>
    /// Gets the index of the row.
    /// </summary>
    public int Index
    {
        get
        {
            if (TableView is not { } tableView)
            {
                return -1;
            }

            // Resolving this walks the panel, and a row is asked for its index constantly: by
            // every cell that builds a Slot to answer IsSelected or IsCurrent, by the alternate
            // colouring, by the selection painter. A viewport of nineteen rows was doing it
            // fifteen hundred times per scroll hop for an answer that changes exactly twice —
            // when the row is bound to a different item, and when the collection shifts under it.
            // Both of those invalidate; nothing else has to ask again.
            if (_indexGeneration != tableView.RowIndexGeneration)
            {
                TableView.DiagRowIndexLookups++;
                _index = tableView.IndexFromContainer(this);
                _indexGeneration = tableView.RowIndexGeneration;
            }

            return _index;
        }
    }

    private int _index = -1;
    private int _indexGeneration;

    /// <summary>Forgets the cached <see cref="Index"/>, for when this row alone moved — it was
    /// recycled onto another item. A shift under the whole viewport bumps
    /// <see cref="TableView.RowIndexGeneration"/> instead.</summary>
    internal void InvalidateIndex() => _indexGeneration = 0;

    /// <summary>
    /// Gets or sets the TableView associated with the row.
    /// </summary>
    public TableView? TableView
    {
        get;
        internal set
        {
            if (field != value)
            {
                OnTableViewChanging();
                field = value;
                OnTableViewChanged();
            }
        }
    }

    /// <inheritdoc/>
    public TableViewRowPresenter? RowPresenter
#if WINDOWS
       => ContentTemplateRoot as TableViewRowPresenter;
#else
    { get; private set; }
#endif

    /// <inheritdoc/>
    protected override AutomationPeer OnCreateAutomationPeer()
    {
        return new AutomationPeers.TableViewRowAutomationPeer(this);
    }
}
