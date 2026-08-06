// FOBO fork addition.
//
// The arithmetic behind column virtualization, kept apart from the layout that uses it so it can be
// tested without a UI — the same split that keeps VirtualTreeModel and TreeGridFlattener verifiable.
//
// A row realizes a cell per VISIBLE column, where "visible" has so far meant "not hidden by the
// user". On a grid with twenty columns and six of them on screen that is most of a row's work spent
// building, binding, measuring and arranging cells nobody can see. What decides which ones are worth
// building is pure geometry: the column widths, how many are frozen, and where the horizontal
// viewport currently sits.

using System;
using System.Collections.Generic;

namespace WinUI.TableView;

/// <summary>
/// Which columns of a row intersect the horizontal viewport, and where each one sits.
///
/// <para>Frozen columns are always in view — they do not scroll — so they are never virtualized away
/// and their widths are excluded from the scrollable strip's own coordinate space.</para>
/// </summary>
public static class ColumnStrip
{
    /// <summary>One column's place in the scrollable strip.</summary>
    /// <param name="Index">Its index among the visible columns.</param>
    /// <param name="Offset">Its left edge, measured from the start of the scrollable strip.</param>
    /// <param name="Width">Its width.</param>
    public readonly record struct Slot(int Index, double Offset, double Width);

    /// <summary>
    /// The range of scrollable columns that intersects the viewport, as a half-open [start, end).
    /// Both are indices into <paramref name="widths"/>; frozen columns are not included and are
    /// always realized.
    /// </summary>
    /// <param name="widths">Every visible column's width, in display order.</param>
    /// <param name="frozenCount">How many leading columns are frozen.</param>
    /// <param name="horizontalOffset">The scrollable strip's current scroll offset.</param>
    /// <param name="viewportWidth">The width available to the scrollable strip.</param>
    /// <param name="overscan">
    /// Extra columns to keep realized either side, so a slow drag does not build a cell at the exact
    /// moment it is needed. One is usually enough — a column is wide.
    /// </param>
    public static (int Start, int End) VisibleRange(
        IReadOnlyList<double> widths,
        int frozenCount,
        double horizontalOffset,
        double viewportWidth,
        int overscan = 1)
    {
        var count = widths?.Count ?? 0;
        var frozen = Math.Clamp(frozenCount, 0, count);
        if (count == 0 || frozen >= count)
            return (frozen, frozen);

        // A viewport nobody has measured yet must not virtualize anything away: answering "none
        // visible" before the first layout would leave a row that draws nothing and never recovers.
        if (double.IsNaN(viewportWidth) || viewportWidth <= 0)
            return (frozen, count);

        var from = double.IsNaN(horizontalOffset) ? 0 : Math.Max(0, horizontalOffset);
        var to = from + viewportWidth;

        var start = -1;
        var end = count;
        var x = 0d;

        for (var i = frozen; i < count; i++)
        {
            var w = Width(widths[i]);
            var right = x + w;

            // Intersects when it starts before the viewport ends and ends after it starts. A
            // zero-width column is kept if the viewport is on top of it, so a column being resized
            // down to nothing does not blink out of existence mid-drag.
            if (start < 0 && right > from)
                start = i;
            if (x >= to)
            {
                end = i;
                break;
            }

            x = right;
        }

        if (start < 0)
            return (count, count);   // scrolled past every column

        start = Math.Max(frozen, start - overscan);
        end = Math.Min(count, end + overscan);
        return (start, end < start ? start : end);
    }

    /// <summary>
    /// Where each column in <paramref name="range"/> sits, measured from the start of the scrollable
    /// strip — what a panel needs to arrange a cell it did not lay out sequentially.
    /// </summary>
    public static IReadOnlyList<Slot> Slots(
        IReadOnlyList<double> widths, int frozenCount, (int Start, int End) range)
    {
        var count = widths?.Count ?? 0;
        var frozen = Math.Clamp(frozenCount, 0, count);
        var start = Math.Clamp(range.Start, frozen, count);
        var end = Math.Clamp(range.End, start, count);

        // Walk from the first scrollable column so the offsets are absolute, not relative to the
        // range — a cell arranged relative to the range would jump every time the range moved.
        var x = 0d;
        for (var i = frozen; i < start; i++)
            x += Width(widths![i]);

        var slots = new List<Slot>(end - start);
        for (var i = start; i < end; i++)
        {
            var w = Width(widths![i]);
            slots.Add(new Slot(i, x, w));
            x += w;
        }
        return slots;
    }

    /// <summary>
    /// What a row's layout needs once the range is known: how far right the realized cells have to
    /// be pushed, and how much width the row still has to claim for the ones it did not build.
    /// </summary>
    /// <returns>
    /// <c>Inset</c> — the summed width of the scrollable columns before the range, which is where
    /// the panel holding the realized cells starts. <c>Hidden</c> — the summed width of every
    /// column outside the range, which the row adds to its own so the horizontal scrollbar still
    /// describes the whole strip.
    /// </returns>
    public static (double Inset, double Hidden) Geometry(
        IReadOnlyList<double> widths, int frozenCount, (int Start, int End) range)
    {
        var count = widths?.Count ?? 0;
        var frozen = Math.Clamp(frozenCount, 0, count);
        var start = Math.Clamp(range.Start, frozen, count);
        var end = Math.Clamp(range.End, start, count);

        var inset = 0d;
        for (var i = frozen; i < start; i++)
            inset += Width(widths![i]);

        var realized = 0d;
        for (var i = start; i < end; i++)
            realized += Width(widths![i]);

        return (inset, Math.Max(0, ScrollableWidth(widths!, frozen) - realized));
    }

    /// <summary>The scrollable strip's full width — the extent a scrollbar is sized against, which
    /// must count every column and not only the realized ones.</summary>
    public static double ScrollableWidth(IReadOnlyList<double> widths, int frozenCount)
    {
        var count = widths?.Count ?? 0;
        var total = 0d;
        for (var i = Math.Clamp(frozenCount, 0, count); i < count; i++)
            total += Width(widths![i]);
        return total;
    }

    /// <summary>A column that has never been measured contributes nothing rather than NaN, which
    /// would poison every offset after it.</summary>
    private static double Width(double w) => double.IsNaN(w) || w < 0 ? 0 : w;
}
