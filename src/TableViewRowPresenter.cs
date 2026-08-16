using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Foundation;
using WinUI.TableView.Extensions;

namespace WinUI.TableView;

/// <summary>
/// Represents a control that presents visuals for the <see cref="WinUI.TableView.TableViewRow"/>.
/// </summary>
[TemplateVisualState(Name = VisualStates.StateDetailsVisible, GroupName = VisualStates.GroupRowDetails)]
[TemplateVisualState(Name = VisualStates.StateDetailsCollapsed, GroupName = VisualStates.GroupRowDetails)]
[TemplateVisualState(Name = VisualStates.StateDetailsButtonVisible, GroupName = VisualStates.GroupRowDetailsButton)]
[TemplateVisualState(Name = VisualStates.StateDetailsButtonCollapsed, GroupName = VisualStates.GroupRowDetailsButton)]
public partial class TableViewRowPresenter : Control
{
    private TableViewRowHeader? _rowHeader;
    private Panel? _rootPanel;
    private StackPanel? _scrollableCellsPanel;
    private StackPanel? _frozenCellsPanel;
    private Rectangle? _v_gridLine;
    private Rectangle? _h_gridLine;
    private Panel? _detailsPanel;
    private ContentPresenter? _detailsPresenter;
    private ToggleButton? _detailsToggleButton;
    private ListViewItemPresenter? _itemPresenter;
    private long? _detailsPanelVisibilityCallbackToken;

    /// <summary>
    /// Initializes a new instance of the <see cref="TableViewRowPresenter"/> class.
    /// </summary>
    public TableViewRowPresenter()
    {
        DefaultStyleKey = typeof(TableViewRowPresenter);
    }

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _detailsToggleButton?.Tapped -= OnDetailsToggleButtonTapped;

        _detailsPanel?.SizeChanged -= OnDetailsPanelSizeChanged;

        if (_detailsPanelVisibilityCallbackToken is long token)
        {
            _detailsPanel?.UnregisterPropertyChangedCallback(VisibilityProperty, token);
            _detailsPanelVisibilityCallbackToken = null;
        }

        _rowHeader = GetTemplateChild("RowHeader") as TableViewRowHeader;
        _rootPanel = GetTemplateChild("RootPanel") as Panel;
        _scrollableCellsPanel = GetTemplateChild("ScrollableCellsPanel") as StackPanel;
        _frozenCellsPanel = GetTemplateChild("FrozenCellsPanel") as StackPanel;
        InvalidateCells();   // new panels, so whatever was cached belonged to the old ones
        _v_gridLine = GetTemplateChild("VerticalGridLine") as Rectangle;
        _h_gridLine = GetTemplateChild("HorizontalGridLine") as Rectangle;
        _detailsPanel = GetTemplateChild("DetailsPanel") as Panel;
        _detailsPresenter = GetTemplateChild("DetailsPresenter") as ContentPresenter;
        _detailsToggleButton = GetTemplateChild("DetailsToggleButton") as ToggleButton;

        _itemPresenter = this.FindAscendant<ListViewItemPresenter>();
        TableViewRow = this.FindAscendant<TableViewRow>();
        TableView = TableViewRow?.TableView;
        _rowHeader?.TableView = TableView;
        _rowHeader?.TableViewRow = TableViewRow;

        _detailsToggleButton?.Tapped += OnDetailsToggleButtonTapped;

        if (_detailsPanel is not null)
        {
            _detailsPanel.SizeChanged += OnDetailsPanelSizeChanged;
            _detailsPanelVisibilityCallbackToken =
                _detailsPanel.RegisterPropertyChangedCallback(VisibilityProperty, OnDetailsPanelVisibilityChanged);
        }

        TableViewRow?.EnsureCells();
        EnsureGridLines();
        SetRowHeaderBindings();
        SetRowHeaderVisibility();
        SetRowHeaderTemplate();
        SetRowHeaderWidth();
        SetRowDetailsVisibility();
        SetRowDetailsTemplate();
    }

    /// <summary>
    /// Handles size changes in the row details panel.
    /// </summary>
    private void OnDetailsPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        TableViewRow?.EnsureLayout();
    }

    /// <summary>
    /// Handles visibility changes in the row details panel.
    /// </summary>
    private void OnDetailsPanelVisibilityChanged(DependencyObject sender, DependencyProperty dp)
    {
        TableViewRow?.EnsureLayout();
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        // The row header does not measure every time — but a collapsed one has nothing to measure,
        // and asking anyway put a forced re-measure on every row of every pass in the common case
        // where the grid shows no row headers at all.
        if (_rowHeader is { Visibility: Visibility.Visible })
        {
            _rowHeader.InvalidateMeasure();
        }

        var size = base.MeasureOverride(availableSize);

        // A row that built only the cells on screen is narrower than the grid it belongs to, and the
        // ScrollViewer's extent — which is what gives the horizontal scrollbar its range — is the
        // widest thing inside it. Report the width the row WOULD have, so scrolling right still
        // reaches the last column.
        if (CellsHiddenWidth > 0)
        {
            size.Width += CellsHiddenWidth;
        }

        return size;
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        TableView.DiagRowArranges++;
        var diagT0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var size = ArrangeCore(finalSize);
        TableView.DiagRowArrangeTicks += System.Diagnostics.Stopwatch.GetTimestamp() - diagT0;
        return size;
    }

    private Size ArrangeCore(Size finalSize)
    {
        finalSize = base.ArrangeOverride(finalSize);

        if (TableView is not null)
        {
            var cornerRadius = _itemPresenter?.CornerRadius ?? new CornerRadius(0);
            var isMultiSelection = TableView is ListView { SelectionMode: ListViewSelectionMode.Multiple };
            var left = isMultiSelection ? 44 : Math.Max(cornerRadius.TopLeft, cornerRadius.BottomLeft);
            var xScroll = -TableView.HorizontalOffset;
            var xClip = TableView.HorizontalOffset;

            // These two Arrange calls re-arrange every cell in the row, twice per pass, and a
            // RenderTransform would move the same laid-out subtree for nothing. It was tried, and
            // it is reverted: cells went blank in the real grid in a way this repository's harness
            // could not reproduce — the content was present, sized and visible by every measure the
            // harness can take, and still did not paint. Arrange is what the row's geometry is
            // built on; until the difference is understood, it stays.
            _rootPanel?.Arrange(new(left, 0, Math.Max(0, _rootPanel.ActualWidth), _rootPanel.ActualHeight));

            if (_detailsPanel?.Visibility is Visibility.Visible && _v_gridLine is not null)
            {
                var x = _v_gridLine.ActualOffset.X + _v_gridLine.ActualWidth;
                x += TableView.AreRowDetailsFrozen ? 0 : xScroll;
                var y = _scrollableCellsPanel?.ActualHeight ?? _v_gridLine.ActualOffset.Y;
                var width = _detailsPanel.ActualWidth;
                var height = _detailsPanel.ActualHeight;
                _detailsPanel.Arrange(new(x, y, width, height));
                _detailsPanel.Clip = x >= _v_gridLine.ActualOffset.X + _v_gridLine.ActualWidth ? null :
                    new RectangleGeometry
                    {
                        Rect = new(xClip, 0, Math.Max(0, _detailsPanel.ActualWidth - xClip), _detailsPanel.ActualHeight)
                    };
            }

            if (_scrollableCellsPanel?.ActualWidth > 0 && _frozenCellsPanel is not null)
            {
                var frozenRight = _frozenCellsPanel.ActualOffset.X + _frozenCellsPanel.ActualWidth;
                xScroll += frozenRight;

                // The panel holds only the realized cells and stacks them from its own left edge, so
                // the columns that were skipped are an inset: without it the first realized cell
                // would sit under the first scrollable header instead of under its own.
                xScroll += CellsInset;
                xClip = Math.Max(0, xClip - CellsInset);

                _scrollableCellsPanel.Arrange(new(xScroll, 0, _scrollableCellsPanel.ActualWidth, _scrollableCellsPanel.ActualHeight));

                // Assigned on every arrange, deliberately. Both cheaper versions of this were
                // tried — reusing one geometry and moving its Rect, and skipping the assignment
                // when the rectangle had not changed — and both produced a grid whose every cell
                // was empty: an early arrange, before these panels have a width, computes a clip
                // of zero width, and nothing that leaves the property alone afterwards ever undoes
                // it. Every element inside then reports itself present, visible and correctly
                // sized, and paints nothing. The allocation is the price of the clip taking.
                _scrollableCellsPanel.Clip = xScroll >= frozenRight ? null :
                    new RectangleGeometry
                    {
                        Rect = new(xClip, 0, Math.Max(0, _scrollableCellsPanel.ActualWidth - xClip), _scrollableCellsPanel.ActualHeight)
                    };
            }


            if (_v_gridLine is not null && TableView is not null)
            {
                // Every realized row publishes the same grid-wide number, and computing it means a
                // TransformToVisual walk. The answer only moves when this row's own geometry does,
                // so the walk happens then and not nineteen times a pass for an unchanged value.
                var key = (_v_gridLine.ActualOffset.X, _v_gridLine.Visibility, ActualWidth, left);
                if (_gridLineOffsetKey != key)
                {
                    _gridLineOffsetKey = key;

                    var transform = _v_gridLine.TransformToVisual(this);
                    var relativePosition = transform.TransformPoint(new Point(0, 0));
                    var offset = _v_gridLine.Visibility is Visibility.Visible ? relativePosition.X : 0d;
                    offset -= Math.Max(cornerRadius.TopLeft, cornerRadius.BottomLeft);

                    TableView.SetValue(TableView.CellsHorizontalOffsetProperty, Math.Max(0, offset));
                }
            }
        }

        return finalSize;
    }

    private (double, Visibility, double, double) _gridLineOffsetKey = (double.NaN, default, double.NaN, double.NaN);

    /// <summary>
    /// Sets the DataTemplate for the row header.
    /// </summary>
    internal void SetRowHeaderTemplate()
    {
        if (_rowHeader is not null && TableView is not null)
        {
            _rowHeader.ContentTemplate =
                TableView.RowHeaderTemplateSelector?.SelectTemplate(TableViewRow?.Content)
                ?? TableView.RowHeaderTemplate;
        }

        SetRowHeaderVisibility();
    }

    /// <summary>
    /// Sets the visibility of the row details based on the <see cref="TableView.RowDetailsVisibilityMode"/>.
    /// </summary>
    internal void SetRowDetailsVisibility()
    {
        EnsureGridLines();

        var mode = TableView?.RowDetailsVisibilityMode;
        var hasTemplate = TableView?.RowDetailsTemplate is not null || TableView?.RowDetailsTemplateSelector is not null;

        if (!hasTemplate)
        {
            VisualStates.GoToState(this, false, VisualStates.StateDetailsCollapsed);
            VisualStates.GoToState(this, false, VisualStates.StateDetailsButtonCollapsed);
        }
        else if (mode is TableViewRowDetailsVisibilityMode.Visible)
        {
            VisualStates.GoToState(this, false, VisualStates.StateDetailsVisible);
            VisualStates.GoToState(this, false, VisualStates.StateDetailsButtonCollapsed);
        }
        else if (mode is TableViewRowDetailsVisibilityMode.VisibleWhenSelected)
        {
            var state = (TableViewRow?.IsSelected ?? false) ? VisualStates.StateDetailsVisible : VisualStates.StateDetailsCollapsed;
            VisualStates.GoToState(this, false, state);
            VisualStates.GoToState(this, false, VisualStates.StateDetailsButtonCollapsed);
        }
        else if (mode is TableViewRowDetailsVisibilityMode.VisibleWhenExpanded)
        {
            VisualStates.GoToState(this, false, VisualStates.StateDetailsButtonVisible);
        }
        else
        {
            VisualStates.GoToState(this, false, VisualStates.StateDetailsCollapsed);
            VisualStates.GoToState(this, false, VisualStates.StateDetailsButtonCollapsed);
        }
    }

    /// <summary>
    /// Handles the Tapped event of the details toggle button.
    /// </summary>
    private void OnDetailsToggleButtonTapped(object sender, TappedRoutedEventArgs e)
    {
        ToggleDetailsPane(TableViewRow?.Content, _detailsToggleButton!.IsChecked ?? false);
    }

    /// <summary>
    /// Toggles the visibility of the details pane.
    /// </summary>
    private void ToggleDetailsPane(object? content, bool isVisible)
    {
        if (TableView is null || content is null) return;

        TableView.DetailsPaneStates.AddOrUpdate(content, isVisible);
        var state = isVisible ? VisualStates.StateDetailsVisible : VisualStates.StateDetailsCollapsed;
        VisualStates.GoToState(this, false, state);
    }

    /// <summary>
    /// Ensures that the details pane visibility is synchronized for the specified item when row.
    /// </summary>
    internal void ApplyDetailsPaneState(object? item)
    {
        if (TableView?.RowDetailsVisibilityMode is TableViewRowDetailsVisibilityMode.VisibleWhenExpanded &&
            _detailsToggleButton is not null && TableView is not null && item is not null)
        {
            var isChecked = TableView.DetailsPaneStates.TryGetValue(item, out var value) && value.Value;
            _detailsToggleButton!.IsChecked = isChecked;
            ToggleDetailsPane(item, isChecked);
        }
    }

    /// <summary>
    /// Sets the DataTemplate for the row details.
    /// </summary>
    internal void SetRowDetailsTemplate()
    {
        if (_detailsPresenter is not null && TableView is not null)
        {
            _detailsPresenter.ContentTemplate =
                TableView.RowDetailsTemplateSelector?.SelectTemplate(TableViewRow?.Content)
                ?? TableView.RowDetailsTemplate;
        }
    }

    /// <summary>
    /// Sets the widths of the row header column.
    /// </summary>
    internal void SetRowHeaderWidth()
    {
        if (_rowHeader is not null && TableView is not null)
        {
            var headerWidth = TableView.RowHeaderWidth is double.NaN ? TableView.RowHeaderActualWidth : TableView.RowHeaderWidth;

            _rowHeader.Width = headerWidth;
            _rowHeader.MinWidth = TableView.RowHeaderMinWidth;
            _rowHeader.MaxWidth = TableView.RowHeaderMaxWidth;

            _rowHeader?.InvalidateMeasure();
            _rowHeader?.InvalidateArrange();
        }
    }

    /// <summary>
    /// Sets the visibility of the row header based on the TableView settings.
    /// </summary>
    internal void SetRowHeaderVisibility()
    {
        if (_rowHeader is not null && TableView is not null)
        {
            var areHeadersVisible = TableView.HeadersVisibility is TableViewHeadersVisibility.All or TableViewHeadersVisibility.Rows;
            var isMultiSelection = TableView is ListView { SelectionMode: ListViewSelectionMode.Multiple };
            var isDetailsToggleButtonVisible = TableView.RowDetailsVisibilityMode is TableViewRowDetailsVisibilityMode.VisibleWhenExpanded
                                               && (TableView.RowDetailsTemplate is not null || TableView.RowDetailsTemplateSelector is not null);

            if (areHeadersVisible && !isMultiSelection &&
               (!isDetailsToggleButtonVisible || TableView.RowHeaderTemplate is not null || TableView.RowHeaderTemplateSelector is not null))
            {
                _rowHeader.Visibility = Visibility.Visible;
                SetRowHeaderWidth();
            }
            else
            {
                _rowHeader.Visibility = Visibility.Collapsed;
            }

            EnsureGridLines();
        }
    }

    internal void SetRowHeaderBindings()
    {
        _rowHeader?.SetBinding(HeightProperty, new Binding
        {
            Path = new PropertyPath($"{nameof(TableViewRowHeader.TableView)}.{nameof(TableView.RowHeight)}"),
            RelativeSource = new RelativeSource { Mode = RelativeSourceMode.Self }
        });

        _rowHeader?.SetBinding(MaxHeightProperty, new Binding
        {
            Path = new PropertyPath($"{nameof(TableViewRowHeader.TableView)}.{nameof(TableView.RowMaxHeight)}"),
            RelativeSource = new RelativeSource { Mode = RelativeSourceMode.Self }
        });

        _rowHeader?.SetBinding(MinHeightProperty, new Binding
        {
            Path = new PropertyPath($"{nameof(TableViewRowHeader.TableView)}.{nameof(TableView.RowMinHeight)}"),
            RelativeSource = new RelativeSource { Mode = RelativeSourceMode.Self }
        });
    }

    /// <summary>
    /// Ensures grid lines are applied to the cells.
    /// </summary>
    internal void EnsureGridLines()
    {
        if (TableView is null) return;

        if (_h_gridLine is not null)
        {
            _h_gridLine.Fill = TableView.HorizontalGridLinesStroke;
            _h_gridLine.Height = TableView.HorizontalGridLinesStrokeThickness;
            _h_gridLine.Visibility = TableView.GridLinesVisibility is TableViewGridLinesVisibility.All or TableViewGridLinesVisibility.Horizontal
                                     ? Visibility.Visible : Visibility.Collapsed;

            if (_v_gridLine is not null)
            {
                var vGridLinesVisibility = TableView.HeaderGridLinesVisibility is TableViewGridLinesVisibility.All or TableViewGridLinesVisibility.Vertical
                                           || TableView.GridLinesVisibility is TableViewGridLinesVisibility.All or TableViewGridLinesVisibility.Vertical;
                var areHeadersVisible = TableView.HeadersVisibility is TableViewHeadersVisibility.All or TableViewHeadersVisibility.Rows;
                var isMultiSelection = TableView is ListView { SelectionMode: ListViewSelectionMode.Multiple };
                var isDetailsToggleButtonVisible = TableView.RowDetailsVisibilityMode is TableViewRowDetailsVisibilityMode.VisibleWhenExpanded
                                                    && (TableView.RowDetailsTemplate is not null || TableView.RowDetailsTemplateSelector is not null);

                _v_gridLine.Fill = TableView.GridLinesVisibility is TableViewGridLinesVisibility.All or TableViewGridLinesVisibility.Vertical
                                   ? TableView.VerticalGridLinesStroke : new SolidColorBrush(Colors.Transparent);
                _v_gridLine.Width = TableView.VerticalGridLinesStrokeThickness;
                _v_gridLine.Visibility = vGridLinesVisibility && (areHeadersVisible || isMultiSelection || isDetailsToggleButtonVisible) ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        foreach (var cell in Cells)
        {
            cell.EnsureGridLines();
        }

        _lightFrozen?.EnsureGridLines();
        _lightScrollable?.EnsureGridLines();
    }

    internal double GetDetailsContentHeight()
    {
        return _detailsPanel?.Visibility is Visibility.Visible ? _detailsPanel.ActualHeight : 0d;
    }

    /// <summary>
    /// Inserts a cell at the specified index.
    /// </summary>
    /// <param name="cell">The cell to insert.</param>
    public void InsertCell(TableViewCell cell)
    {
        if (TableView is null || cell is not { Column: { } column }) return;

        TableView.DiagInsertCellScans++;

        // Where this cell goes in its panel is how many columns of the SAME kind come before its
        // own. Counting them is one pass; the two filtered lists this used to build were rebuilt
        // for every cell of every row, which made realizing a row quadratic in its column count
        // all over again — the same shape the cell's own index lookup had.
        var panel = column.IsFrozen ? _frozenCellsPanel : _scrollableCellsPanel;
        if (panel is null)
        {
            return;
        }

        var visible = TableView.Columns.VisibleColumns;
        var order = visible.IndexOf(column);

        // Counted against the cells that are actually IN the panel, not against every column that
        // comes before this one. Under column virtualization those are not the same number, and
        // clamping the second one to the child count put a re-realized cell on the wrong side of
        // its neighbour whenever a range grew leftwards.
        var index = 0;
        for (var i = 0; i < panel.Children.Count; i++)
        {
            if (panel.Children[i] is TableViewCell { } sibling && sibling.Index < order)
            {
                index++;
            }
        }

        panel.Children.Insert(Math.Clamp(index, 0, panel.Children.Count), cell);
        InvalidateCells();

        cell.EnsureStyle(TableViewRow?.Content);
    }

    /// <summary>
    /// Removes a cell from the presenter.
    /// </summary>
    /// <param name="cell">The cell to remove.</param>
    public void RemoveCell(TableViewCell cell)
    {
        if (_frozenCellsPanel?.Children.Remove(cell) is true
            || _scrollableCellsPanel?.Children.Remove(cell) is true)
        {
            InvalidateCells();
        }
    }

    /// <summary>
    /// Moves the cell associated with the specified column to a new index.
    /// </summary>
    /// <param name="column">The column associated with the cell to move.</param>
    /// <param name="newIndex">The new index to move the cell to.</param>
    internal void MoveCells(TableViewColumn column, int newIndex)
    {
        if (Cells.FirstOrDefault(h => h.Column == column) is { } cell)
        {
            RemoveCell(cell);
            InsertCell(cell);
        }

        if (newIndex >= 0 && newIndex < TableView?.FrozenColumnCount &&
           _frozenCellsPanel?.Children.OfType<TableViewCell>().LastOrDefault() is { } frozenCell)
        {
            RemoveCell(frozenCell);
            InsertCell(frozenCell);
        }

        UpdateCellIndexes();
    }

    /// <summary>
    /// Updates the indexes of all cells in the presenter.
    /// </summary>
    private void UpdateCellIndexes()
    {
        if (TableView is null) return;

        foreach (var cell in Cells)
        {
            if (cell.Column is not null)
            {
                var index = TableView.Columns.VisibleColumns.IndexOf(cell.Column);
                if (cell.Index != index)
                    cell.Index = index;
            }
        }
    }

    /// <summary>
    /// The geometry of the cell set this row is holding RIGHT NOW: how far its cells panel is
    /// pushed right by the columns it skipped, and how much width it claims for the columns it did
    /// not build.
    ///
    /// <para>Per row, not read from the grid, because the rows do not all take on a new column range
    /// in the same frame — the work of doing so is spread over several. A row that arranged against
    /// a range whose cells it had not built yet would put every cell it does hold a column out of
    /// place.</para>
    /// </summary>
    internal double CellsInset { get; set; }

    /// <inheritdoc cref="CellsInset"/>
    internal double CellsHiddenWidth { get; set; }

    /// <summary>
    /// Clears all cells from the presenter.
    /// </summary>
    public void ClearCells()
    {
        _frozenCellsPanel?.Children.Clear();
        _scrollableCellsPanel?.Children.Clear();
        _lightFrozen = null;
        _lightScrollable = null;
        InvalidateCells();
    }

    /// <summary>
    /// The two panels a light row draws through, one per section, in place of the stacks of cells.
    ///
    /// <para>They sit INSIDE the same two stack panels the cells used to, as the single child of
    /// each. Everything the presenter's arrange does — the horizontal offset, the inset for the
    /// columns skipped, the clip that stops the scrollable half sliding under the frozen one — is
    /// then unchanged and unduplicated, which is the point: the light path is a different way of
    /// filling a row, not a different way of placing one.</para>
    /// </summary>
    private TableViewLightCellsPanel? _lightFrozen;

    /// <inheritdoc cref="_lightFrozen"/>
    private TableViewLightCellsPanel? _lightScrollable;

    /// <summary>Builds the light panels, if this row is drawing that way and has not got them yet.</summary>
    internal void EnsureLightCells()
    {
        if (_lightScrollable is not null || TableViewRow is not { } row)
        {
            return;
        }

        // Takes out whatever was there: the dummy TextBlock the template ships with, or a set of
        // cells from before the grid was switched over.
        ClearCells();

        _lightFrozen = new TableViewLightCellsPanel();
        _lightFrozen.Attach(row, frozen: true);
        _frozenCellsPanel?.Children.Add(_lightFrozen);

        _lightScrollable = new TableViewLightCellsPanel();
        _lightScrollable.Attach(row, frozen: false);
        _scrollableCellsPanel?.Children.Add(_lightScrollable);
    }

    /// <summary>Brings the light panels in line with the columns on screen.</summary>
    internal void SyncLightCells()
    {
        _lightFrozen?.Sync();
        _lightScrollable?.Sync();
        InvalidateCells();   // a template column may have come or gone with the range
    }

    /// <summary>Puts an item's values into the light panels.</summary>
    internal void ShowLightCells(object? item)
    {
        _lightFrozen?.Show(item);
        _lightScrollable?.Show(item);
    }

    /// <summary>
    /// Gets the list of cells in the presenter, frozen ones first.
    ///
    /// <para>Cached. This used to materialise a fresh list on every read, and it is read the way
    /// a field is: to apply a style, a height, a selection state, to find one cell by its column.
    /// The set only changes when a cell is added, removed or reordered, so that is when it is
    /// rebuilt.</para>
    /// </summary>
    public IReadOnlyList<TableViewCell> Cells => _cells ??= BuildCells();

    private IReadOnlyList<TableViewCell>? _cells;

    private IReadOnlyList<TableViewCell> BuildCells()
    {
        TableView.DiagCellListBuilds++;

        // A light row holds a cell only for the columns it could not draw as text — a template
        // column, the tree column. Everything that walks this list therefore acts on those and
        // no-ops on the rest, which is what makes a mixed row work.
        if (_lightScrollable is not null)
        {
            return [.. _lightFrozen?.Cells ?? [], .. _lightScrollable.Cells];
        }

        return
        [.. _frozenCellsPanel?.Children.OfType<TableViewCell>() ?? [],
         .. _scrollableCellsPanel?.Children.OfType<TableViewCell>() ?? []];
    }

    /// <summary>Drops the cached cell list, for when the cells themselves changed.</summary>
    private void InvalidateCells() => _cells = null;

    /// <summary>
    /// Gets or sets the TableViewRow associated with the presenter.
    /// </summary>
    public TableViewRow? TableViewRow { get; private set; }

    /// <summary>
    /// Gets or sets the TableView associated with the presenter.
    /// </summary>
    public TableView? TableView { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the row details panel is currently visible.
    /// </summary>
    internal bool IsDetailsPanelVisible => _detailsPanel?.Visibility is Visibility.Visible;

    /// <summary>
    /// Gets the realized row header element.
    /// </summary>
    internal TableViewRowHeader? RowHeader => _rowHeader;

    /// <summary>
    /// Programmatically shows or hides the details pane.
    /// Only takes effect when <see cref="TableView.RowDetailsVisibilityMode"/> is
    /// <see cref="TableViewRowDetailsVisibilityMode.VisibleWhenExpanded"/>.
    /// </summary>
    /// <param name="visible"><see langword="true"/> to expand; <see langword="false"/> to collapse.</param>
    internal void ShowDetailPane(bool visible)
    {
        if (TableView?.RowDetailsVisibilityMode is TableViewRowDetailsVisibilityMode.VisibleWhenExpanded)
        {
            if (_detailsToggleButton is not null)
            {
                _detailsToggleButton.IsChecked = visible;
            }

            ToggleDetailsPane(TableViewRow?.Content, visible);
        }
    }
}
