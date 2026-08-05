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

        return base.MeasureOverride(availableSize);
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

            // Shifted, not re-arranged. Both of these moves are a horizontal slide of an element
            // that base.ArrangeOverride has already laid out at the right size — but calling
            // Arrange again with a different origin makes the panel arrange all of its children
            // again, so every row was arranging its whole set of cells twice on every pass. A
            // translation moves the same laid-out subtree and leaves the children alone; it also
            // participates in hit-testing and in TransformToVisual, which is what the grid-line
            // offset below and the drag-selection hit test read.
            Shift(_rootPanel, left);

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

                Shift(_scrollableCellsPanel, xScroll);
                Clip(_scrollableCellsPanel, ref _scrollableClip, xScroll >= frozenRight ? null :
                    new Rect(xClip, 0, Math.Max(0, _scrollableCellsPanel.ActualWidth - xClip), _scrollableCellsPanel.ActualHeight));
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
    private RectangleGeometry? _scrollableClip;

    /// <summary>Slides an already-arranged element so its left edge lands on <paramref name="x"/>,
    /// without asking it — or its children — to lay out again.</summary>
    private static void Shift(FrameworkElement? element, double x)
    {
        if (element is null)
        {
            return;
        }

        var delta = x - element.ActualOffset.X;

        if (element.RenderTransform is not TranslateTransform translate)
        {
            if (delta == 0)
            {
                return;   // nothing to correct, and no transform worth allocating
            }

            element.RenderTransform = translate = new TranslateTransform();
        }

        // ActualOffset is where base.ArrangeOverride put it and is unaffected by the transform, so
        // this stays a correction from the laid-out position rather than accumulating.
        if (translate.X != delta)
        {
            translate.X = delta;
        }
    }

    /// <summary>Clips an element to <paramref name="rect"/>, reusing the geometry rather than
    /// allocating one per row per arrange. Null removes the clip.</summary>
    private static void Clip(FrameworkElement element, ref RectangleGeometry? geometry, Rect? rect)
    {
        if (rect is not { } r)
        {
            if (element.Clip is not null)
            {
                element.Clip = null;
            }
            return;
        }

        geometry ??= new RectangleGeometry();
        if (geometry.Rect != r)
        {
            geometry.Rect = r;
        }

        if (!ReferenceEquals(element.Clip, geometry))
        {
            element.Clip = geometry;
        }
    }

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
        var index = 0;
        for (var i = 0; i < visible.Count && visible[i] != column; i++)
        {
            if (visible[i].IsFrozen == column.IsFrozen)
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
    /// Clears all cells from the presenter.
    /// </summary>
    public void ClearCells()
    {
        _frozenCellsPanel?.Children.Clear();
        _scrollableCellsPanel?.Children.Clear();
        InvalidateCells();
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
