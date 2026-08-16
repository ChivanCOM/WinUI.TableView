// FOBO fork addition.
//
// Where the words and the rules go inside a light row, kept apart from the panel that draws them so
// it can be tested without a UI — the same split ColumnStrip already makes for the column range.
//
// A templated cell works its own geometry out from a border thickness, a padding and a content
// presenter's alignment. A light row has none of those: one panel arranges a run of text blocks at
// numbers it computed itself, and if those numbers drift by a pixel the columns no longer line up
// with their headers. That arithmetic is here, on its own, where a test can hold it to account.

using System;
using System.Collections.Generic;

namespace WinUI.TableView;

/// <summary>
/// The layout of one light row: for each column it holds, where its text sits and where the line
/// down its right edge sits.
/// </summary>
public static class LightRowStrip
{
    /// <summary>One column's place inside the panel that draws it.</summary>
    /// <param name="Index">Its index among the visible columns.</param>
    /// <param name="X">Its left edge, measured from the panel's own left edge.</param>
    /// <param name="Width">Its full width, rule included.</param>
    /// <param name="TextX">Where its text starts.</param>
    /// <param name="TextWidth">How much room the text has.</param>
    /// <param name="RuleX">The left edge of the line down its right side.</param>
    public readonly record struct Cell(
        int Index, double X, double Width, double TextX, double TextWidth, double RuleX);

    /// <summary>
    /// Lays out the columns a panel is holding.
    /// </summary>
    /// <param name="slots">The columns, in display order, with offsets in the strip's own
    /// coordinates — <see cref="ColumnStrip.Slots"/> for the scrollable ones.</param>
    /// <param name="origin">The offset of the panel's left edge in those same coordinates. The
    /// panel holds only the columns on screen and draws them from its own left edge, so every
    /// offset is measured from here.</param>
    /// <param name="padLeft">The gap before a column's text.</param>
    /// <param name="padRight">The gap after it.</param>
    /// <param name="ruleThickness">How wide the line down a column's right edge is. It comes out of
    /// the column's own width, exactly as the templated cell's grid gives it a column of its own.</param>
    public static IReadOnlyList<Cell> Lay(
        IReadOnlyList<ColumnStrip.Slot> slots,
        double origin,
        double padLeft,
        double padRight,
        double ruleThickness)
    {
        if (slots is null || slots.Count == 0)
        {
            return [];
        }

        var rule = Clean(ruleThickness);
        var left = Clean(padLeft);
        var right = Clean(padRight);

        var cells = new List<Cell>(slots.Count);

        for (var i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            var x = slot.Offset - origin;
            var width = Clean(slot.Width);

            // The text is inset on both sides and stops short of the rule. A column narrower than
            // its own furniture gets no room at all rather than a negative width, which would throw
            // in Arrange.
            var textWidth = Math.Max(0, width - rule - left - right);

            cells.Add(new Cell(
                slot.Index,
                x,
                width,
                x + left,
                textWidth,
                x + Math.Max(0, width - rule)));
        }

        return cells;
    }

    /// <summary>
    /// The columns before <paramref name="frozenCount"/>, which are always held: they do not scroll,
    /// so there is no range to intersect and their offsets start at zero.
    /// </summary>
    public static IReadOnlyList<ColumnStrip.Slot> FrozenSlots(IReadOnlyList<double> widths, int frozenCount)
    {
        var count = widths?.Count ?? 0;
        var frozen = Math.Clamp(frozenCount, 0, count);
        if (frozen == 0)
        {
            return [];
        }

        var slots = new List<ColumnStrip.Slot>(frozen);
        var x = 0d;

        for (var i = 0; i < frozen; i++)
        {
            var w = Clean(widths![i]);
            slots.Add(new ColumnStrip.Slot(i, x, w));
            x += w;
        }

        return slots;
    }

    /// <summary>The width a panel claims for what it is holding: up to the right edge of the last
    /// column, from the panel's own left edge.</summary>
    public static double Width(IReadOnlyList<Cell> cells)
    {
        if (cells is null || cells.Count == 0)
        {
            return 0d;
        }

        var last = cells[^1];
        return Math.Max(0, last.X + last.Width);
    }

    /// <summary>A column that has never been measured contributes nothing rather than NaN, which
    /// would poison every offset after it.</summary>
    private static double Clean(double value) => double.IsNaN(value) || value < 0 ? 0 : value;
}
