// FOBO fork addition.
//
// Which rows the reader can actually see, and which of them are showing a given item.
//
// There are two records of that in this control and only one of them is the truth. `_rows` is a log
// of container PREPARES: added in PrepareContainerForItemOverride, removed in
// ClearContainerForItemOverride. The framework's item-to-container map is written by the same
// prepare. Both therefore describe the containers that got here the ordinary way — and on Uno a
// container can get onto the screen two other ways:
//
//   * a landing page PATCHES a row in place instead of preparing it (PatchRealizedRows), which is
//     what keeps a fling from re-realizing the viewport;
//   * the layouter's large-scroll recovery RECYCLES a container onto another item without preparing
//     it, which takes it out of `_rows` and leaves it there (see ReseatPanelRows).
//
// So a row can be on screen, showing the right item, and be in neither record. Everything keyed by
// the item then misses it: its cells are never re-read and its selection is never painted. That is
// this pane's oldest recurring fault — a mass edit whose new values appear on some rows and not
// others, and a page of rows arriving unwashed under a selection nobody changed.
//
// The panel's own children are the fact that holds whenever it is asked, which is why the reanchor
// capture and the re-seat already read it rather than `_rows`. Every item-keyed question goes
// through here for the same reason.

using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;

namespace WinUI.TableView;

public partial class TableView
{
    /// <summary>
    /// Every row on screen right now, however it got there.
    ///
    /// <para>The panel's children first, because that is the answer that does not depend on how the
    /// container was filled. Then anything in the prepare log that the panel is not holding — a row
    /// prepared before the panel has placed it, and whatever a host has realized off-panel.</para>
    ///
    /// <para>A viewport of rows, walked lazily, and only reached on the rare paths: an item saying
    /// one of its values changed, and a host painting its selection.</para>
    /// </summary>
    internal IEnumerable<TableViewRow> RowsOnScreen()
    {
        var panel = ItemsPanelRoot;

        if (panel is not null)
        {
            foreach (var child in panel.Children)
            {
                if (child is TableViewRow row)
                {
                    yield return row;
                }
            }
        }

        foreach (var row in _rows)
        {
            // Not already yielded above. A container the panel is holding is a child of it; anything
            // else is a prepare the panel has not placed. Compared rather than kept in a set: the
            // worst a wrong answer here can do is hand the same row over twice, and both callers do
            // idempotent work with it.
            if (panel is null || !ReferenceEquals(row.Parent, panel))
            {
                yield return row;
            }
        }
    }

    /// <summary>
    /// Every row showing <paramref name="item"/>. Usually one; never assumed to be.
    ///
    /// <para>Two containers can hold the same item object for a moment — a host that REUSES its row
    /// objects across a rebuild (the import queue does, so the ticks survive one) can have a
    /// recovery-recycled container still on the old one while a fresh container shows it in its new
    /// place. Answering with the first of them left the other showing the values it had before the
    /// edit, for as long as the reader stayed on that screen.</para>
    /// </summary>
    internal IEnumerable<TableViewRow> RowsShowing(object item)
    {
        foreach (var row in RowsOnScreen())
        {
            if (ReferenceEquals(row.Content, item))
            {
                yield return row;
            }
        }
    }

    /// <summary>
    /// The realized row showing <paramref name="item"/>, or null.
    ///
    /// <para>For the callers that want one row. Anything that has to reach ALL of them — re-reading
    /// an edited row's cells, painting the selection — goes through <see cref="RowsShowing"/> or
    /// <see cref="PaintRowSelection"/> instead.</para>
    ///
    /// <para>The framework's item-to-container map is still asked last, for the rows this grid does
    /// not track.</para>
    /// </summary>
    public TableViewRow? RowShowing(object? item)
    {
        if (item is null)
        {
            return null;
        }

        foreach (var row in RowsShowing(item))
        {
            return row;
        }

        return ContainerFromItem(item) as TableViewRow;
    }

    /// <summary>
    /// Paints every row on screen from <paramref name="selected"/>, which is asked about the item the
    /// row is showing.
    ///
    /// <para>For a host whose selection is a fact about the DATA — a tick in a store, a flag on the
    /// row — rather than a set of objects the list is holding. One pass over the viewport, and it
    /// does not go through the item-to-container map.</para>
    /// </summary>
    public void PaintRowSelection(Func<object?, bool> selected)
    {
        foreach (var row in RowsOnScreen())
        {
            var wanted = selected(row.Content);
            if (row.IsSelected != wanted)
            {
                row.IsSelected = wanted;
            }
        }
    }
}
