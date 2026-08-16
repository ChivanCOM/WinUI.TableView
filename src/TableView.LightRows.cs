// FOBO fork addition.
//
// When a row is allowed to be light, and what happens when that answer changes.
//
// A light row has no cells. That is the whole saving and the whole risk, because a cell is what
// carries cell selection, cell editing, the hover layer, the per-cell style and the per-row tooltip.
// The three conditions below are the ones under which none of that has anything to do: a grid that
// takes no edits, selects whole rows, and does not size a column by measuring cells that would never
// be built.
//
// Deliberately an opt-in on top of those. The conditions say a light row would be CORRECT; the
// property says a caller has looked at their grid and wants it.

using Microsoft.UI.Xaml;
using System.Diagnostics;

namespace WinUI.TableView;

public partial class TableView
{
    private bool? _areRowsLight;

    /// <summary>
    /// Whether the rows of this grid draw their columns directly instead of building a cell each.
    /// </summary>
    internal bool AreRowsLight => _areRowsLight ??= ComputeAreRowsLight();

    /// <summary>
    /// Works the answer out again, for when one of the things it is made of has moved — and rebuilds
    /// the realized rows if it came out differently, because the two paths build their own children
    /// into the same two panels and a row cannot be half of each.
    /// </summary>
    internal void InvalidateLightRows()
    {
        var before = _areRowsLight;
        _areRowsLight = null;

        if (before is { } was && was != AreRowsLight)
        {
            RebuildRows();
        }
    }

    private void RebuildRows()
    {
        foreach (var row in _rows)
        {
            row.RebuildCells();
        }
    }

    private bool ComputeAreRowsLight()
    {
        if (!UseLightRows)
        {
            return false;
        }

        // A grid that can be edited needs the editing machinery a cell carries, and one that selects
        // cells needs a cell to select. Both are the cell's whole purpose.
        if (!IsReadOnly)
        {
            return WarnLightRowsOff("the grid is not read-only");
        }

        if (SelectionUnit is not TableViewSelectionUnit.Row)
        {
            return WarnLightRowsOff("SelectionUnit is not Row");
        }

        // A style chosen per row is applied to a cell, and there is none to apply it to.
        if (ConditionalCellStyles is { Count: > 0 })
        {
            return WarnLightRowsOff("the grid has conditional cell styles");
        }

        // A column that learns its width by measuring its cells learns nothing from a row that
        // builds none — the same condition that turns column virtualization off, for the same
        // reason.
        if (Columns?.VisibleColumns is { } visible && SizedFromCellsThatWouldNotExist(visible))
        {
            return WarnLightRowsOff("a column takes its width from measuring its cells");
        }

        return true;
    }

    /// <summary>Says why the opt-in did not take, once. This is a standing condition rather than an
    /// event, and repeating it every layout pass would bury everything else.</summary>
    private bool WarnLightRowsOff(string reason)
    {
        if (!_warnedAboutLightRows)
        {
            _warnedAboutLightRows = true;
            Debug.WriteLine($"[TableView] UseLightRows is set but the rows are not light: {reason}.");
        }

        return false;
    }

    private bool _warnedAboutLightRows;

    /// <summary>
    /// Handles changes to the UseLightRows property.
    /// </summary>
    private static void OnUseLightRowsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TableView tableView)
        {
            tableView._warnedAboutLightRows = false;
            tableView._areRowsLight = null;
            tableView.RebuildRows();
        }
    }
}
