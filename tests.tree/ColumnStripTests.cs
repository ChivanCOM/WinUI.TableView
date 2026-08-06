using WinUI.TableView;

namespace WinUI.TableView.TreeTests;

/// <summary>
/// The arithmetic behind column virtualization. A row builds a cell per visible column, so on a grid
/// with twenty columns and six on screen most of a row's work goes into cells nobody can see —
/// and which columns are worth building is nothing but geometry, which is why it is tested here
/// rather than left inside a layout pass where it cannot be.
/// </summary>
public class ColumnStripTests
{
    // Ten columns of 100 each; two frozen. The scrollable strip is therefore 800 wide.
    private static readonly double[] Widths = [100, 100, 100, 100, 100, 100, 100, 100, 100, 100];
    private const int Frozen = 2;

    [Fact]
    public void Only_the_columns_the_viewport_covers_are_in_range()
    {
        // Viewport 300 wide at the start: scrollable columns 2, 3, 4 — plus one of overscan.
        var (start, end) = ColumnStrip.VisibleRange(Widths, Frozen, horizontalOffset: 0, viewportWidth: 300);

        Assert.Equal(Frozen, start);          // never before the first scrollable one
        Assert.Equal(6, end);                 // 2,3,4 visible + 5 overscanned
        Assert.True(end - start < Widths.Length - Frozen);   // genuinely fewer than all of them
    }

    [Fact]
    public void Scrolling_moves_the_range_rather_than_growing_it()
    {
        var atStart = ColumnStrip.VisibleRange(Widths, Frozen, 0, 300, overscan: 0);
        var scrolled = ColumnStrip.VisibleRange(Widths, Frozen, 400, 300, overscan: 0);

        Assert.Equal((2, 5), atStart);
        Assert.Equal((6, 9), scrolled);
        Assert.Equal(atStart.End - atStart.Start, scrolled.End - scrolled.Start);
    }

    [Fact]
    public void Frozen_columns_are_never_virtualized_away()
    {
        // Scrolled to the far end: the range holds only the last columns, and the frozen ones are
        // simply not part of this question — they do not scroll, so they are always realized.
        var (start, end) = ColumnStrip.VisibleRange(Widths, Frozen, horizontalOffset: 800, viewportWidth: 300);

        Assert.True(start >= Frozen);
        Assert.Equal(Widths.Length, end);
    }

    [Fact]
    public void An_unmeasured_viewport_realizes_everything_rather_than_nothing()
    {
        // Before the first layout the width is not known. Answering "none visible" would leave a row
        // that draws nothing and has no reason to ever draw anything again.
        Assert.Equal((Frozen, Widths.Length), ColumnStrip.VisibleRange(Widths, Frozen, 0, double.NaN));
        Assert.Equal((Frozen, Widths.Length), ColumnStrip.VisibleRange(Widths, Frozen, 0, 0));
    }

    [Fact]
    public void Offsets_are_absolute_so_a_cell_does_not_move_when_the_range_does()
    {
        var wide = ColumnStrip.Slots(Widths, Frozen, (2, 10));
        var narrow = ColumnStrip.Slots(Widths, Frozen, (5, 8));

        // Column 5 sits at the same x whichever range it was asked about.
        Assert.Equal(300, wide.Single(s => s.Index == 5).Offset);
        Assert.Equal(300, narrow.Single(s => s.Index == 5).Offset);
        Assert.Equal(100, narrow.Single(s => s.Index == 5).Width);
    }

    [Fact]
    public void The_scrollable_extent_counts_every_column_not_only_the_realized_ones()
    {
        // The scrollbar has to describe the whole strip, or virtualizing would shrink it.
        Assert.Equal(800, ColumnStrip.ScrollableWidth(Widths, Frozen));
        Assert.Equal(1000, ColumnStrip.ScrollableWidth(Widths, 0));
    }

    [Fact]
    public void Uneven_widths_land_where_the_arithmetic_says()
    {
        double[] widths = [30, 250, 60, 400, 55];
        var slots = ColumnStrip.Slots(widths, frozenCount: 1, (1, 5));

        Assert.Equal([0, 250, 310, 710], slots.Select(s => s.Offset));
        Assert.Equal(765, ColumnStrip.ScrollableWidth(widths, 1));
    }

    [Fact]
    public void A_column_of_unknown_width_contributes_nothing_rather_than_poisoning_the_offsets()
    {
        double[] widths = [100, double.NaN, 100, 100];
        var slots = ColumnStrip.Slots(widths, frozenCount: 0, (0, 4));

        Assert.Equal([0, 100, 100, 200], slots.Select(s => s.Offset));
        Assert.All(slots, s => Assert.False(double.IsNaN(s.Offset)));
    }

    [Fact]
    public void Everything_frozen_leaves_nothing_to_virtualize()
    {
        var (start, end) = ColumnStrip.VisibleRange(Widths, frozenCount: Widths.Length, 0, 300);
        Assert.Equal(start, end);
        Assert.Empty(ColumnStrip.Slots(Widths, Widths.Length, (start, end)));
    }

    [Fact]
    public void The_skipped_columns_become_the_inset_the_realized_cells_are_pushed_by()
    {
        // Scrolled to 400: columns 6, 7, 8 are on screen, so 2..5 were skipped. A row's cells panel
        // stacks the four it built from its own left edge, and only this inset puts the first of
        // them under its own header instead of under column 2's.
        var range = ColumnStrip.VisibleRange(Widths, Frozen, horizontalOffset: 400, viewportWidth: 300, overscan: 0);
        var (inset, hidden) = ColumnStrip.Geometry(Widths, Frozen, range);

        Assert.Equal((6, 9), range);
        Assert.Equal(400, inset);                                     // columns 2,3,4,5
        Assert.Equal(inset, ColumnStrip.Slots(Widths, Frozen, range)[0].Offset);
        Assert.Equal(500, hidden);                                    // 800 wide, 300 of it realized
    }

    [Fact]
    public void The_realized_and_hidden_widths_add_back_up_to_the_whole_strip()
    {
        // What the row claims for itself is what it built plus what it did not, and that has to be
        // the same number whether or not anything was virtualized away — otherwise turning it on
        // shortens the scrollbar and the last column becomes unreachable.
        double[] widths = [30, 250, 60, 400, 55, 120];
        var full = ColumnStrip.ScrollableWidth(widths, frozenCount: 1);

        foreach (var offset in new double[] { 0, 100, 400, 700, 5000 })
        {
            var range = ColumnStrip.VisibleRange(widths, 1, offset, viewportWidth: 200);
            var (_, hidden) = ColumnStrip.Geometry(widths, 1, range);
            var realized = ColumnStrip.Slots(widths, 1, range).Sum(s => s.Width);

            Assert.Equal(full, realized + hidden);
        }
    }

    [Fact]
    public void Realizing_everything_leaves_nothing_to_inset_or_to_claim_back()
    {
        var (inset, hidden) = ColumnStrip.Geometry(Widths, Frozen, (Frozen, Widths.Length));

        Assert.Equal(0, inset);
        Assert.Equal(0, hidden);
    }

    [Fact]
    public void No_columns_at_all_is_an_empty_range_and_not_a_crash()
    {
        Assert.Equal((0d, 0d), ColumnStrip.Geometry([], 0, (0, 0)));
        Assert.Equal((0, 0), ColumnStrip.VisibleRange([], 0, 0, 300));
        Assert.Empty(ColumnStrip.Slots([], 0, (0, 0)));
        Assert.Equal(0, ColumnStrip.ScrollableWidth([], 0));
    }
}
