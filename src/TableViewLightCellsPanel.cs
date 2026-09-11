// FOBO fork addition.
//
// A row's cells, without the cells.
//
// Everything that makes TableViewCell expensive — a templated Control, a background border, a
// selection border, a current-cell border, a content presenter, a rectangle for the rule — exists to
// support selecting a cell and editing one. A grid that is read-only and selects whole rows has
// neither, and pays for both: about a hundred and nineteen elements a row where nineteen would do,
// measured and arranged every time a row scrolls into view.
//
// So this. One panel per row per section, holding a TextBlock for every column whose display is a
// run of text, a real cell for every column whose display is not, and a single Path for all the
// vertical rules. It arranges them at offsets it computes rather than by stacking, which is what
// lets it hold only the columns on screen and still put them under their own headers.
//
// What it does NOT do is decide anything. Which columns are worth holding is still
// TableView.ColumnRange's answer, and where the panel itself sits is still the row presenter's
// arrange. This only draws what those two have already agreed on.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using Windows.Foundation;
using WinUI.TableView.Extensions;

namespace WinUI.TableView;

/// <summary>
/// Draws one section of a light row: the frozen columns or the scrollable ones.
/// </summary>
internal sealed partial class TableViewLightCellsPanel : Panel
{
    /// <summary>The gap either side of a column's text. Matches the margin
    /// <see cref="TableViewTextColumn"/> puts on the TextBlock it generates, so a grid reads the
    /// same whichever path built it.</summary>
    internal const double TextPadding = 12d;

    private readonly List<Entry> _entries = [];
    private readonly List<TextBlock> _spareText = [];
    private readonly Microsoft.UI.Xaml.Shapes.Path _rules = new() { IsHitTestVisible = false };

    private TableViewRow? _row;
    private bool _frozen;
    private double _height = double.NaN;
    private double _width;
    private (int Start, int End, int Count, double Total, double Rule) _rulesKey = (-1, -1, -1, double.NaN, double.NaN);

    /// <summary>
    /// One column this panel is holding, and whichever of the three things shows it: a TextBlock
    /// for a plain value, the column's own element for a template that needs no cell around it, or
    /// a real cell for a column that genuinely wants one.
    /// </summary>
    private struct Entry
    {
        public TableViewColumn Column;
        public LightRowStrip.Cell Place;
        public TextBlock? Text;
        public FrameworkElement? Element;
        public TableViewCell? Cell;
    }

    public TableViewLightCellsPanel()
    {
        // A panel with no brush is not hit-testable in the gaps between its children, and the gaps
        // are most of a row. The cell this replaces carried the same invisible fill from its default
        // style, for the same reason.
        Background = HitTestFill;
        ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY;

        Children.Add(_rules);

        PointerPressed += OnPointerPressed;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += OnPointerCaptureLost;
        ManipulationDelta += OnManipulationDelta;
        Tapped += OnTapped;
    }

    /// <summary>One brush for every light panel in every grid, rather than one per row.</summary>
    private static readonly SolidColorBrush HitTestFill = new(Microsoft.UI.Colors.Transparent);

    /// <summary>Ties the panel to the row it draws for.</summary>
    /// <param name="row">The row.</param>
    /// <param name="frozen"><see langword="true"/> for the frozen section, which never scrolls.</param>
    internal void Attach(TableViewRow row, bool frozen)
    {
        _row = row;
        _frozen = frozen;
    }

    /// <summary>The real cells this panel is holding, for the columns that could not be drawn as
    /// text. Empty on a grid whose every column is a plain value, which is the point.</summary>
    internal IEnumerable<TableViewCell> Cells
    {
        get
        {
            foreach (var entry in _entries)
            {
                if (entry.Cell is { } cell)
                {
                    yield return cell;
                }
            }
        }
    }

    /// <summary>
    /// Brings the panel in line with the columns it should be holding right now, and reports where
    /// its left edge sits in the strip.
    /// </summary>
    /// <remarks>
    /// The panel's own left edge lands at the summed width of the columns before the first one it
    /// holds, which is the same number the row adopts as its inset — both are walked from the same
    /// widths against the same range, so they agree by construction rather than by being copied.
    /// </remarks>
    internal void Sync()
    {
        if (_row?.TableView is not { } tableView || tableView.Columns?.VisibleColumns is not { } visible)
        {
            return;
        }

        var count = visible.Count;
        var frozenCount = Math.Clamp(tableView.FrozenColumnCount, 0, count);

        // Walked rather than read off the grid, because a row takes on a new column range in its own
        // time — the drain spreads that work over several frames — and a panel arranged against a
        // range whose columns it has not built yet puts every one it does hold a column out of place.
        var widths = new List<double>(count);
        for (var i = 0; i < count; i++)
        {
            widths.Add(visible[i].ActualWidth);
        }

        var slots = _frozen
            ? LightRowStrip.FrozenSlots(widths, frozenCount)
            : Held(tableView, visible, widths, frozenCount);

        var origin = slots.Count > 0 ? slots[0].Offset : 0d;
        var places = LightRowStrip.Lay(slots, origin, TextPadding, TextPadding, RuleThickness(tableView));

        _width = LightRowStrip.Width(places);   // the rules are keyed on it, so before the rebuild
        Rebuild(tableView, visible, places);

        InvalidateMeasure();
        InvalidateArrange();
    }

    /// <summary>The scrollable columns this panel should hold: the ones inside the grid's current
    /// range, with their offsets measured from the start of the scrollable strip.</summary>
    private static IReadOnlyList<ColumnStrip.Slot> Held(
        TableView tableView, IList<TableViewColumn> visible, IReadOnlyList<double> widths, int frozenCount)
    {
        var slots = new List<ColumnStrip.Slot>();
        var x = 0d;

        for (var i = frozenCount; i < visible.Count; i++)
        {
            var width = widths[i];
            width = double.IsNaN(width) || width < 0 ? 0 : width;

            if (tableView.IsColumnRealized(visible[i], i))
            {
                slots.Add(new ColumnStrip.Slot(i, x, width));
            }

            x += width;
        }

        return slots;
    }

    /// <summary>
    /// Takes on a new set of columns: text blocks are handed back and taken out again rather than
    /// thrown away, and a real cell is built only for a column that cannot be drawn as text.
    /// </summary>
    private void Rebuild(TableView tableView, IList<TableViewColumn> visible, IReadOnlyList<LightRowStrip.Cell> places)
    {
        // Which columns are already held, so a range that moved by one does not rebuild the other
        // eighteen. Keyed by the column itself: its index moves when a column is hidden or reordered.
        var kept = new Dictionary<TableViewColumn, Entry>(_entries.Count);
        foreach (var entry in _entries)
        {
            kept[entry.Column] = entry;
        }

        _entries.Clear();

        foreach (var place in places)
        {
            if (place.Index < 0 || place.Index >= visible.Count)
            {
                continue;
            }

            var column = visible[place.Index];

            if (kept.Remove(column, out var existing))
            {
                existing.Place = place;
                _entries.Add(existing);
                continue;
            }

            var entry = new Entry { Column = column, Place = place };

            if (column.CanRenderAsText)
            {
                entry.Text = TakeTextBlock();
                Children.Add(entry.Text);
            }
            else if (column.CanRenderWithoutCell)
            {
                entry.Element = column.CreateCellFreeElement(_row?.Content);
                Children.Add(entry.Element);
            }
            else
            {
                entry.Cell = new TableViewCell
                {
                    Row = _row,
                    Column = column,
                    TableView = tableView,
                    Index = place.Index,
                    Width = place.Width,
                };

                entry.Cell.Height = tableView.RowHeight;
                entry.Cell.MaxHeight = tableView.RowMaxHeight;
                entry.Cell.MinHeight = tableView.RowMinHeight;
                entry.Cell.EnsureStyle(_row?.Content);

                Children.Add(entry.Cell);
            }

            _entries.Add(entry);
        }

        // Whatever is left over scrolled off the side.
        foreach (var gone in kept.Values)
        {
            if (gone.Text is { } text)
            {
                Children.Remove(text);
                GiveBackTextBlock(text);
            }
            else if (gone.Element is { } element)
            {
                Children.Remove(element);
            }
            else if (gone.Cell is { } cell)
            {
                Children.Remove(cell);
            }
        }

        // Indexes and widths move without the set of columns moving at all — a column resized, or
        // one hidden from the header menu.
        for (var i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].Cell is { } cell)
            {
                cell.Index = _entries[i].Place.Index;
                cell.Width = _entries[i].Place.Width;
            }
        }

        EnsureRules(tableView);
    }

    /// <summary>
    /// Puts an item's values into the elements this panel is holding.
    /// </summary>
    internal void Show(object? item)
    {
        // The grid is not required to write the values: the columns and their elements are already
        // held, and a recycled row is re-bound before it is told which grid it belongs to. Only the
        // height and the rules need it, and both keep what they had until it is back.
        var tableView = _row?.TableView;

        if (tableView is not null)
        {
            _height = tableView.RowHeightSelector?.Invoke(item) ?? tableView.RowHeight;
        }

        foreach (var entry in _entries)
        {
            if (entry.Text is { } text)
            {
                var value = entry.Column.GetCellText(item);

                // Assigning a string that has not changed still writes a dependency property and
                // dirties the text layout, and a scroll re-shows a great many rows whose columns hold
                // the same words as the row above.
                if (!string.Equals(text.Text, value, StringComparison.Ordinal))
                {
                    TableView.DiagCellTextSets++;
                    text.Text = value;
                }
            }
            else if (entry.Element is { } element)
            {
                entry.Column.RefreshCellFreeElement(element, item);
            }
            else
            {
                entry.Cell?.RefreshElement();
                entry.Cell?.EnsureStyle(item);
            }
        }

        // A grid whose rows are not all the same height (RowHeightSelector) draws its rules to a
        // different length for this item than for the last one. Keyed, so a uniform grid — which is
        // every grid that has not asked otherwise — rebuilds nothing here.
        if (tableView is not null)
        {
            EnsureRules(tableView);
        }

        InvalidateMeasure();
    }

    /// <summary>Re-reads the grid's grid-line settings.</summary>
    internal void EnsureGridLines()
    {
        if (_row?.TableView is not { } tableView)
        {
            return;
        }

        _rulesKey = (-1, -1, -1, double.NaN, double.NaN);   // stroke and thickness are part of it
        Sync();
    }

    /// <summary>
    /// Rebuilds the one Path that draws every vertical rule in this row.
    ///
    /// <para>One element for the whole row, against one Rectangle per cell. Rebuilt when the columns
    /// or their widths move, which a vertical scroll never does — so a fling redraws the same
    /// geometry rather than building it nineteen times a row.</para>
    ///
    /// <para>A column that kept a real cell is skipped: that cell draws its own rule, and drawing a
    /// second one on top of it doubles the stroke on exactly the columns that mix with the rest.</para>
    /// </summary>
    private void EnsureRules(TableView tableView)
    {
        var thickness = RuleThickness(tableView);
        var visible = thickness > 0 && tableView.GridLinesVisibility
            is TableViewGridLinesVisibility.All or TableViewGridLinesVisibility.Vertical;

        _rules.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        if (!visible)
        {
            return;
        }

        _rules.Stroke = tableView.VerticalGridLinesStroke;
        _rules.StrokeThickness = thickness;

        // The height is part of it because the lines are drawn to the row's own height rather than
        // drawn tall and clipped: a clip is an allocation on every arrange of every row, which is the
        // shape of cost this whole path exists to remove.
        var height = RowHeight();

        var key = (_entries.Count > 0 ? _entries[0].Place.Index : -1,
                   _entries.Count > 0 ? _entries[^1].Place.Index : -1,
                   _entries.Count,
                   _width + height,
                   thickness);

        if (key == _rulesKey)
        {
            return;
        }

        _rulesKey = key;

        var group = new GeometryGroup();

        foreach (var entry in _entries)
        {
            if (entry.Cell is not null)
            {
                continue;
            }

            // Half a stroke in, so a one pixel line lands on a pixel rather than across two.
            var x = entry.Place.RuleX + (thickness / 2);
            group.Children.Add(new LineGeometry { StartPoint = new Point(x, 0), EndPoint = new Point(x, height) });
        }

        _rules.Data = group;
    }

    private static double RuleThickness(TableView tableView)
        => tableView.GridLinesVisibility is TableViewGridLinesVisibility.All or TableViewGridLinesVisibility.Vertical
            ? tableView.VerticalGridLinesStrokeThickness
            : 0d;

    private TextBlock TakeTextBlock()
    {
        if (_spareText.Count > 0)
        {
            var reused = _spareText[^1];
            _spareText.RemoveAt(_spareText.Count - 1);
            return reused;
        }

        // Font, size and colour are left to inherit from the row, which is where a cell got them
        // from too. Setting them here would take a grid's FontSize out of the picture.
        return new TextBlock
        {
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    /// <summary>How many spare text blocks a panel keeps. Enough to cover a nudge either side of the
    /// viewport; past that the reader has left those columns behind.</summary>
    private const int SpareLimit = 8;

    private void GiveBackTextBlock(TextBlock text)
    {
        if (_spareText.Count >= SpareLimit)
        {
            return;
        }

        text.Text = string.Empty;
        _spareText.Add(text);
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        var height = RowHeight();

        foreach (var entry in _entries)
        {
            var place = entry.Place;

            if (entry.Text is { } text)
            {
                // At the column's own width, never unconstrained. An unbounded measure lays the whole
                // string out to find how wide it would like to be, and nothing reads the answer: the
                // width is the column's and the text is trimmed to it.
                text.Measure(new Size(place.TextWidth, height));
            }
            else if (entry.Element is { } element)
            {
                element.Measure(new Size(ContentWidth(place), height));
            }
            else
            {
                entry.Cell?.Measure(new Size(place.Width, height));
            }
        }

        _rules.Measure(new Size(_width, height));

        return new Size(_width, height);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var height = double.IsNaN(finalSize.Height) || finalSize.Height <= 0 ? RowHeight() : finalSize.Height;

        foreach (var entry in _entries)
        {
            var place = entry.Place;

            if (entry.Text is { } text)
            {
                // Centred by hand rather than by an alignment, because the panel arranges into a rect
                // of its own choosing and a Stretch would fill it.
                var textHeight = Math.Min(text.DesiredSize.Height, height);
                text.Arrange(new Rect(place.TextX, (height - textHeight) / 2, place.TextWidth, textHeight));
            }
            else if (entry.Element is { } element)
            {
                // Up to the rule and no further, which is where the cell's own content presenter
                // stopped: its grid gave the rule a column of its own.
                element.Arrange(new Rect(place.X, 0, ContentWidth(place), height));
            }
            else
            {
                entry.Cell?.Arrange(new Rect(place.X, 0, place.Width, height));
            }
        }

        _rules.Arrange(new Rect(0, 0, _width, height));

        return new Size(_width, height);
    }

    // ---- The gesture ----------------------------------------------------------------------------
    //
    // Every pointer-driven selection in this grid runs through TableViewCell: the base ListView's own
    // selection is forced off (see TableView.UpdateBaseSelectionMode), so a click, a ctrl-click, a
    // shift-click and the rubber-band sweep all reach MakeSelection from a cell's handlers and from
    // nowhere else. A light row has no cell, so the panel that took the cells' place takes their
    // handlers too — the same tree position, the same manipulation mode, the same thresholds, so
    // whatever the ListView's own item-drag does with them it goes on doing.
    //
    // What differs is only the slot: column -1, a row rather than a cell in it.

    /// <summary>Where the pointer went down, in the drag canvas's coordinates, so a click can be told
    /// from a drag.</summary>
    private Point? _dragOrigin;

    /// <summary>How far the pointer has to travel before a press becomes a rubber-band sweep. Below
    /// it the gesture is a click: it selects the row it landed on and nothing else.</summary>
    private const double DragThreshold = 6;

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_row?.TableView is not { } tableView)
        {
            return;
        }

        // A right press opens a context menu, and is neither a click nor the start of a sweep. One
        // part of what follows actively breaks the menu: the plain-click clear throws away the very
        // rows the menu is about to act on.
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            return;
        }

        // e.KeyModifiers, never the tracked key state, on pointer paths: the latter goes stale when
        // the app is switched away mid-modifier, turning every later click into a shift-click.
        if (e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift))
        {
            return;
        }

        tableView.SelectionStartCellSlot = default;
        tableView.SelectionStartRowIndex = _row.Index;

#if !WINDOWS
        // Uno: a moving click becomes a manipulation and Tapped never fires, so the click path
        // cannot clear the previous selection — the press must.
        tableView.OnSelectionDragStart(_row.Index, tableView.IsPointerCtrlDown);
#endif
        CapturePointer(e.Pointer);

        // Armed, not engaged. A plain press-and-release below the threshold must never enter
        // drag-selection state; the first real movement promotes it.
        if (TransformPointToCanvas(e.GetCurrentPoint(this).Position) is { } canvasPoint)
        {
            _dragOrigin = canvasPoint;
            tableView.ArmDragSelection(canvasPoint);
        }
    }

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_row?.TableView is not { } tableView)
        {
            return;
        }

        // Tapped carries no modifiers of its own — the press and release that formed it stamped the
        // grid's own record, so that is the live state here.
        //
        // Left unhandled deliberately: the row's own OnTapped follows it with the current row and the
        // last selection unit, exactly as it followed the cell's, and a handled tap would put the
        // double-tap that plays a track at risk.
        tableView.MakeSelection(
            new TableViewCellSlot(_row.Index, -1), tableView.IsPointerShiftDown, tableView.IsPointerCtrlDown);
    }

    /// <summary>Which row the sweep is over. See <see cref="RowHitCache"/>.</summary>
    private readonly RowHitCache _rowHits = new();

    private void OnManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        if (_row?.TableView is not { } tableView)
        {
            return;
        }

        // A stale capture is not a drag. Tabbing away to another window leaves the pointer captured
        // with no release ever arriving, and the next move over the grid would carry on selecting as
        // though the button were still down. The origin is cleared whenever a gesture really ends, so
        // its absence means there is no gesture.
        if (PointerCaptures?.Count is not > 0 || _dragOrigin is null)
        {
            return;
        }

        // A click is not a drag. Uno raises a manipulation for a pointer that wobbles a pixel while
        // the button is down, and acting on those turned an ordinary click into a range selection.
        if (!HasLeftTheClick(e.Position))
        {
            return;
        }

        tableView.BeginArmedDragSelection();   // idempotent: a no-op on every delta after the first

        if (tableView.IsDragSelecting && TransformPointToCanvas(e.Position) is { } canvasPoint)
        {
            tableView.UpdateDragRectangleVisual(canvasPoint);
        }

        // When the pointer is outside the viewport this finds nothing, and the selection is carried
        // on instead by the auto-scroll tick (TableView.SelectCellAtDragPoint, which answers for a
        // light row too).
        if (_rowHits.Find(this, tableView, e.Position) is { } row && row.Index != tableView.CurrentRowIndex)
        {
            tableView.MakeSelection(new TableViewCellSlot(row.Index, -1), true, tableView.IsPointerCtrlDown);
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        // The right button's press was left alone above, so its release is too — handling it would
        // eat the event the right-tap gesture, and with it the context menu, is formed from.
        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind
            is Microsoft.UI.Input.PointerUpdateKind.RightButtonReleased)
        {
            return;
        }

        EndGesture();
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e) => EndGesture();

    /// <summary>Ends whatever gesture was in flight and releases the pointer, however it ended.</summary>
    private void EndGesture()
    {
        _row?.TableView?.EndDragSelection();
        ReleasePointerCaptures();
        _dragOrigin = null;
        _rowHits.Forget();
    }

    /// <summary>True once the pointer has moved far enough from where it went down to mean a sweep.</summary>
    private bool HasLeftTheClick(Point position)
    {
        if (_dragOrigin is not { } origin || TransformPointToCanvas(position) is not { } point)
        {
            return true;   // no origin recorded (keyboard, programmatic): behave as before
        }

        var dx = point.X - origin.X;
        var dy = point.Y - origin.Y;

        return dx * dx + dy * dy >= DragThreshold * DragThreshold;
    }

    private Point? TransformPointToCanvas(Point position)
    {
        if (_row?.TableView?.DragRectangleCanvas is not { } canvas)
        {
            return null;
        }

        try
        {
            return TransformToVisual(canvas).TransformPoint(position);
        }
        catch (ArgumentException)
        {
            return null;   // not in the visual tree, mid-recycle
        }
    }

    /// <summary>A column's width less the line down its right edge.</summary>
    private static double ContentWidth(LightRowStrip.Cell place) => Math.Max(0, place.RuleX - place.X);

    /// <summary>The row's height, as the grid states it. A light row is uniform by construction —
    /// nothing in it can grow — so there is no content to measure against.</summary>
    private double RowHeight()
    {
        var tableView = _row?.TableView;
        var height = double.IsNaN(_height) ? tableView?.RowHeight ?? double.NaN : _height;

        if (double.IsNaN(height))
        {
            return 0d;
        }

        if (tableView is not null)
        {
            if (!double.IsNaN(tableView.RowMinHeight))
            {
                height = Math.Max(height, tableView.RowMinHeight);
            }

            if (!double.IsNaN(tableView.RowMaxHeight) && !double.IsInfinity(tableView.RowMaxHeight))
            {
                height = Math.Min(height, tableView.RowMaxHeight);
            }
        }

        return Math.Max(0, height);
    }
}
