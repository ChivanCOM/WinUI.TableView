// FOBO fork addition.
//
// A row builds a cell per visible column, and a grid the width of a music library has more columns
// than fit on screen. Nineteen of twenty-three cells in every realized row are built, bound,
// measured and arranged behind the right-hand edge of the viewport, where nobody can read them.
//
// This is the part that decides which ones are worth building. The arithmetic lives in ColumnStrip,
// where it is testable without a UI; what lives here is when to ask it, what to do when the answer
// changes, and the one number the row's arrange needs so the cells that DID get built land under
// their own headers instead of at the left edge.

using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;

namespace WinUI.TableView;

public partial class TableView
{
    /// <summary>How long the rows spent bringing their cells in line with a new column range, and
    /// how many times they were asked to — the two numbers that say whether a slow horizontal drag
    /// is the cell bookkeeping or the layout that follows it.</summary>
    public static long DiagColumnSyncTicks;

    /// <inheritdoc cref="DiagColumnSyncTicks"/>
    public static long DiagColumnSyncs;

    private readonly List<double> _columnWidths = [];
    // Everything, until something says otherwise: a grid that virtualizes before it knows its
    // columns must err towards building a cell nobody reads, never towards a blank row.
    private (int Start, int End) _columnRange = (0, int.MaxValue);
    private double _columnRangeInset;
    private double _columnRangeHiddenWidth;
    private (double Offset, double Viewport, int Count, int Frozen, double Total) _columnRangeKey
        = (double.NaN, double.NaN, -1, -1, double.NaN);
    private bool _columnRangeUpdateQueued;

    /// <summary>
    /// The half-open range of visible-column indices whose cells a row builds, on top of the frozen
    /// ones it always builds. Everything outside it is left unrealized.
    ///
    /// <para>When <see cref="VirtualizeColumns"/> is off this covers every column, so the rest of
    /// the grid can read it unconditionally.</para>
    /// </summary>
    internal (int Start, int End) ColumnRange => _columnRange;

    /// <summary>The current range, for a harness that needs to know which steps of a horizontal
    /// drag crossed a column boundary and which were a plain re-arrange.</summary>
    public (int Start, int End) ColumnRangeForDiagnostics => _columnRange;

    /// <summary>
    /// How wide the scrollable columns before <see cref="ColumnRange"/> are, together. The row's
    /// cells panel holds only the realized cells and stacks them from its own left edge, so its
    /// arrange has to push it right by this much for the first realized cell to sit under its own
    /// header.
    /// </summary>
    internal double ColumnRangeInset => _columnRangeInset;

    /// <summary>
    /// How wide the columns NOT realized are, together. A row reports its full width regardless of
    /// how many cells it built, so the ScrollViewer's extent — and with it the horizontal
    /// scrollbar's range — is the same whether or not columns are being virtualized.
    /// </summary>
    internal double ColumnRangeHiddenWidth => _columnRangeHiddenWidth;

    /// <summary>Whether a row should build a cell for this column.</summary>
    /// <param name="column">The column in question.</param>
    /// <param name="visibleIndex">Its index among the visible columns.</param>
    internal bool IsColumnRealized(TableViewColumn column, int visibleIndex)
    {
        return !VirtualizeColumns
            || column.IsFrozen
            || visibleIndex < 0                                        // not in the visible list: leave it alone
            || (visibleIndex >= _columnRange.Start && visibleIndex < _columnRange.End);
    }

    /// <summary>
    /// Recomputes which columns are worth realizing, and rebuilds the realized rows' cells if the
    /// answer moved.
    /// </summary>
    /// <remarks>
    /// Rebuilding a row wholesale on every range change is the deliberately blunt first cut. A range
    /// change costs about what binding one new row costs, and horizontal scrolling is rare next to
    /// vertical — a reader crosses a column boundary a few times a session and rows every second.
    /// </remarks>
    internal void UpdateColumnRange()
    {
        if (Columns?.VisibleColumns is not { } visible)
        {
            return;
        }

        var count = visible.Count;
        var frozen = Math.Clamp(FrozenColumnCount, 0, count);

        if (!VirtualizeColumns)
        {
            ApplyColumnRange((0, count), 0d, 0d);
            return;
        }

        _columnWidths.Clear();
        for (var i = 0; i < count; i++)
        {
            _columnWidths.Add(visible[i].ActualWidth);
        }

        var total = ColumnStrip.ScrollableWidth(_columnWidths, frozen);
        var viewport = ScrollableViewportWidth(frozen);

        // Widths, viewport and offset are the whole input. Recomputing when none of them moved
        // would be harmless but this is called from every horizontal scroll notch.
        var key = (HorizontalOffset, viewport, count, frozen, total);
        if (key == _columnRangeKey)
        {
            return;
        }

        _columnRangeKey = key;

        var range = ColumnStrip.VisibleRange(_columnWidths, frozen, HorizontalOffset, viewport);
        var (inset, hidden) = ColumnStrip.Geometry(_columnWidths, frozen, range);

        ApplyColumnRange(range, inset, hidden);
    }

    /// <summary>
    /// Takes the new range, and rebuilds what it invalidates. The rows only rebuild when the SET of
    /// realized columns changed; the inset moving on its own is an arrange, not a rebuild.
    /// </summary>
    private void ApplyColumnRange((int Start, int End) range, double inset, double hiddenWidth)
    {
        var rangeChanged = range != _columnRange;
        var geometryChanged = inset != _columnRangeInset || hiddenWidth != _columnRangeHiddenWidth;

        if (!rangeChanged && !geometryChanged)
        {
            return;
        }

        _columnRange = range;
        _columnRangeInset = inset;
        _columnRangeHiddenWidth = hiddenWidth;

        if (!rangeChanged)
        {
            // The set of columns is the same; only where they sit moved. Every row can take that on
            // straight away — it is an arrange, not a rebuild.
            foreach (var row in _rows)
            {
                row.AdoptColumnGeometry();
            }

            return;
        }

        // Every row has cells to build. Doing all of them in the frame that also has to draw the
        // scroll is one dropped frame per column boundary crossed, and a drag across the grid
        // crosses one every few pixels — so the rows are worked through a few per frame, and a row
        // that has not had its turn yet keeps arranging against the range it does hold. It shows the
        // column it had rather than the one it is about to get, for a frame or two, which is what
        // the overscan is there to cover.
        _rowsAwaitingColumnSync.Clear();
        _rowsAwaitingColumnSync.AddRange(_rows);
        DrainColumnSyncQueue();
    }

    private readonly List<TableViewRow> _rowsAwaitingColumnSync = [];
    private bool _columnSyncDrainQueued;

    /// <summary>
    /// Works through the rows that still have cells to build for the current range, no more than
    /// <see cref="MaxColumnSyncRowsPerFrame"/> of them at a time, and asks for another turn if any
    /// are left.
    /// </summary>
    private void DrainColumnSyncQueue()
    {
        var diagT0 = System.Diagnostics.Stopwatch.GetTimestamp();

        var budget = MaxColumnSyncRowsPerFrame > 0 ? MaxColumnSyncRowsPerFrame : int.MaxValue;
        var done = 0;

        while (done < budget && _rowsAwaitingColumnSync.Count > 0)
        {
            var row = _rowsAwaitingColumnSync[^1];
            _rowsAwaitingColumnSync.RemoveAt(_rowsAwaitingColumnSync.Count - 1);

            // A row recycled out of the viewport since the range moved has nothing to catch up on.
            if (_rows.Contains(row))
            {
                row.SyncCells();
                done++;
            }
        }

        DiagColumnSyncTicks += System.Diagnostics.Stopwatch.GetTimestamp() - diagT0;
        DiagColumnSyncs++;

        if (_rowsAwaitingColumnSync.Count > 0 && !_columnSyncDrainQueued)
        {
            _columnSyncDrainQueued = true;
            DispatcherQueue.TryEnqueue(() =>
            {
                _columnSyncDrainQueued = false;
                DrainColumnSyncQueue();
            });
        }
    }

    /// <summary>
    /// The width the scrollable columns have to play with: the viewport, less the row-header gutter
    /// and less the frozen columns, which sit on top of it and never scroll.
    /// </summary>
    /// <returns>NaN when nothing has been measured yet, which ColumnStrip reads as "realize
    /// everything" — a row that virtualized against an unknown viewport would draw nothing and have
    /// no reason to ever try again.</returns>
    private double ScrollableViewportWidth(int frozen)
    {
        if (_scrollViewer is not { ViewportWidth: > 0 } scrollViewer)
        {
            return double.NaN;
        }

        var width = scrollViewer.ViewportWidth - CellsHorizontalOffset;
        for (var i = 0; i < frozen && i < _columnWidths.Count; i++)
        {
            var w = _columnWidths[i];
            width -= double.IsNaN(w) || w < 0 ? 0 : w;
        }

        return width > 0 ? width : double.NaN;
    }

    /// <summary>
    /// Asks for a recompute once the current layout pass is over — for the callers that ARE the
    /// layout pass (header widths settling, the grid being resized), where rebuilding a row's cells
    /// on the spot would be re-entrant.
    /// </summary>
    internal void QueueColumnRangeUpdate()
    {
        if (_columnRangeUpdateQueued || !VirtualizeColumns)
        {
            return;
        }

        _columnRangeUpdateQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _columnRangeUpdateQueued = false;
            UpdateColumnRange();
        });
    }

    /// <summary>
    /// Handles changes to the VirtualizeColumns property.
    /// </summary>
    private static void OnVirtualizeColumnsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TableView tableView)
        {
            // Turning it off has to put every column back, and the key would otherwise say nothing
            // moved.
            tableView._columnRangeKey = (double.NaN, double.NaN, -1, -1, double.NaN);
            tableView.UpdateColumnRange();

            foreach (var row in tableView._rows)
            {
                row.RebuildCells();
            }
        }
    }
}
