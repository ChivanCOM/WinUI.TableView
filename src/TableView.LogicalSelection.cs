using System;
using System.Collections.Generic;
using System.Linq;

namespace WinUI.TableView;

/// <summary>
/// Logical "all selected" for huge virtualized sources — AG-Grid-style flag +
/// exclusions (their <c>selectAll</c> + <c>toggledNodes</c>). Materializing a
/// million rows into <see cref="ListViewBase.SelectedItems"/> defeats
/// virtualization and hangs, so "select all" is a flag and
/// <see cref="_selectionExclusions"/> holds the rows the user toggled off.
/// Realized rows reflect the logical state via their <c>IsSelected</c> visual
/// (bounded to the viewport); external consumers read <see cref="IsAllSelected"/>
/// / <see cref="IsItemSelected"/> / <see cref="EffectiveSelectedCount"/> instead
/// of enumerating <see cref="ListViewBase.SelectedItems"/>.
/// </summary>
partial class TableView
{
    private readonly HashSet<object> _selectionExclusions = new();

    /// <summary>True when every row is logically selected (minus
    /// <see cref="SelectionExclusions"/>). Set by select-all; never materializes
    /// the item set.</summary>
    public bool IsAllSelected { get; private set; }

    /// <summary>Rows toggled OFF while <see cref="IsAllSelected"/> (AG Grid's
    /// <c>toggledNodes</c>). Empty for an un-excluded select-all.</summary>
    public IReadOnlyCollection<object> SelectionExclusions => _selectionExclusions;

    /// <summary>Whether <paramref name="item"/> is selected, honouring logical-all
    /// + exclusions. Use this instead of <c>SelectedItems.Contains</c>.</summary>
    public bool IsItemSelected(object item) =>
        IsAllSelected ? !_selectionExclusions.Contains(item) : SelectedItems.Contains(item);

    /// <summary>Selected-row count without enumerating: logical-all is
    /// <c>Items.Count - exclusions</c>; otherwise the explicit selection.</summary>
    public int EffectiveSelectedCount =>
        IsAllSelected ? Math.Max(0, Items.Count - _selectionExclusions.Count) : SelectedItems.Count;

    /// <summary>O(1) select-all: drop any explicit selection, raise the flag,
    /// paint the realized rows. Replaces the old <c>SelectRange(0, Items.Count)</c>
    /// materialization.</summary>
    private void SelectAllLogical()
    {
        ClearItemSelection();           // bounded clear of any prior explicit selection
        _selectionExclusions.Clear();
        IsAllSelected = true;
        RepaintRealizedRows();
        RaiseLogicalSelectionChanged();
    }

    /// <summary>Clear logical-all + exclusions and un-paint realized rows.
    /// No-op when not in the logical-all state.</summary>
    private void ClearAllSelectedState()
    {
        if (!IsAllSelected && _selectionExclusions.Count == 0) return;
        IsAllSelected = false;
        _selectionExclusions.Clear();
        foreach (var row in _rows) row.IsSelected = false;
    }

    /// <summary>Ctrl-click a row while all-selected → toggle that row's exclusion
    /// (O(1)). Collapses to an empty explicit selection if everything is excluded.</summary>
    private void ToggleAllSelectionExclusion(int rowIndex)
    {
        if (!IsAllSelected) return;
        if (ItemFromRowIndex(rowIndex) is not { } item) return;

        if (!_selectionExclusions.Remove(item)) _selectionExclusions.Add(item);

        if (_selectionExclusions.Count >= Items.Count)
        {
            ClearAllSelectedState();
        }
        else
        {
            PaintRow(rowIndex);
        }
        RaiseLogicalSelectionChanged();
    }

    private object? ItemFromRowIndex(int index) =>
        index >= 0 && index < Items.Count ? Items[index] : null;

    /// <summary>Repaint every realized row's <c>IsSelected</c> from the logical
    /// state. Bounded to <c>_rows</c> (viewport).</summary>
    private void RepaintRealizedRows()
    {
        if (!IsAllSelected) return;
        foreach (var row in _rows) PaintRowCore(row);
    }

    private void PaintRow(int rowIndex)
    {
        var row = _rows.FirstOrDefault(r => r.Index == rowIndex);
        if (row is not null) PaintRowCore(row);
    }

    /// <summary>Apply the logical-all visual to a single realized row (called on
    /// realize, in PrepareContainerForItemOverride, and on toggle).</summary>
    internal void ApplyLogicalSelectionVisual(TableViewRow row)
    {
        if (IsAllSelected) PaintRowCore(row);
    }

    private void PaintRowCore(TableViewRow row)
    {
        if (ItemFromRowIndex(row.Index) is not { } item) return;
        row.IsSelected = !_selectionExclusions.Contains(item);
    }

    private void RaiseLogicalSelectionChanged()
    {
#if !WINDOWS
        InvokeSelectionChanged([], []);
#endif
    }
}
