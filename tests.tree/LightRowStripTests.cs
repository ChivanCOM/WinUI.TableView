using WinUI.TableView;

namespace WinUI.TableView.TreeTests;

/// <summary>
/// Where the words and the rules go inside a light row.
///
/// <para>A templated cell works this out from a border thickness, a padding and a content
/// presenter's alignment, all of which the framework applies for it. A light row has none of those:
/// one panel arranges a run of text blocks at numbers it computed itself, and a pixel of drift there
/// is a column that no longer lines up with its header. So the numbers are held to account here,
/// where no layout pass is needed to ask.</para>
/// </summary>
public class LightRowStripTests
{
    // Ten columns of 100 each; two frozen. The scrollable strip is therefore 800 wide.
    private static readonly double[] Widths = [100, 100, 100, 100, 100, 100, 100, 100, 100, 100];
    private const int Frozen = 2;
    private const double Pad = 12;
    private const double Rule = 1;

    [Fact]
    public void Text_is_inset_either_side_and_stops_short_of_the_rule()
    {
        var slots = new[] { new ColumnStrip.Slot(0, 0, 100) };

        var cell = LightRowStrip.Lay(slots, origin: 0, Pad, Pad, Rule)[0];

        Assert.Equal(0, cell.X);
        Assert.Equal(100, cell.Width);
        Assert.Equal(Pad, cell.TextX);
        Assert.Equal(100 - Rule - Pad - Pad, cell.TextWidth);
        Assert.Equal(100 - Rule, cell.RuleX);
    }

    [Fact]
    public void A_column_narrower_than_its_own_furniture_gets_no_text_room_rather_than_a_negative_one()
    {
        // Arrange would throw on a negative width, and a column can genuinely be dragged this narrow.
        var slots = new[] { new ColumnStrip.Slot(0, 0, 8) };

        var cell = LightRowStrip.Lay(slots, origin: 0, Pad, Pad, Rule)[0];

        Assert.Equal(0, cell.TextWidth);
        Assert.True(cell.RuleX >= 0);
    }

    [Fact]
    public void Offsets_are_measured_from_the_panel_rather_than_from_the_strip()
    {
        // The panel holds only the columns on screen and draws them from its own left edge; the
        // arrange pushes it right by the origin. An offset left absolute would put every column that
        // far out again.
        var range = (Start: 5, End: 8);
        var slots = ColumnStrip.Slots(Widths, Frozen, range);
        var (inset, _) = ColumnStrip.Geometry(Widths, Frozen, range);

        var cells = LightRowStrip.Lay(slots, inset, Pad, Pad, Rule);

        Assert.Equal(0, cells[0].X);
        Assert.Equal(100, cells[1].X);
        Assert.Equal(200, cells[2].X);
    }

    [Fact]
    public void The_first_column_held_is_where_the_panel_starts()
    {
        // The row's inset and the panel's own origin are two walks over the same widths against the
        // same range. If they ever disagree the columns slide out from under their headers, so the
        // one is checked against the other.
        var range = (Start: 5, End: 8);
        var slots = ColumnStrip.Slots(Widths, Frozen, range);
        var (inset, _) = ColumnStrip.Geometry(Widths, Frozen, range);

        Assert.Equal(inset, slots[0].Offset);
    }

    [Fact]
    public void A_panel_claims_the_width_of_what_it_is_holding_and_no_more()
    {
        var slots = ColumnStrip.Slots(Widths, Frozen, (5, 8));

        var width = LightRowStrip.Width(LightRowStrip.Lay(slots, slots[0].Offset, Pad, Pad, Rule));

        Assert.Equal(300, width);   // three columns of a hundred, not the strip's own eight hundred
    }

    [Fact]
    public void An_empty_panel_lays_out_nothing_and_claims_nothing()
    {
        var cells = LightRowStrip.Lay([], origin: 0, Pad, Pad, Rule);

        Assert.Empty(cells);
        Assert.Equal(0, LightRowStrip.Width(cells));
    }

    [Fact]
    public void Frozen_columns_start_at_zero_and_run_in_order()
    {
        var slots = LightRowStrip.FrozenSlots(Widths, Frozen);

        Assert.Equal(2, slots.Count);
        Assert.Equal(0, slots[0].Offset);
        Assert.Equal(100, slots[1].Offset);
    }

    [Fact]
    public void A_grid_with_nothing_frozen_has_no_frozen_section()
    {
        Assert.Empty(LightRowStrip.FrozenSlots(Widths, 0));
    }

    [Fact]
    public void A_column_that_has_never_been_measured_does_not_poison_the_offsets_after_it()
    {
        double[] widths = [100, double.NaN, 100];

        var slots = LightRowStrip.FrozenSlots(widths, 3);
        var cells = LightRowStrip.Lay(slots, origin: 0, Pad, Pad, Rule);

        Assert.Equal(100, cells[1].X);
        Assert.Equal(100, cells[2].X);   // not NaN, which every later arrange would have inherited
        Assert.Equal(200, LightRowStrip.Width(cells));
    }

    [Fact]
    public void A_grid_with_no_rules_gives_the_whole_column_to_its_text()
    {
        var slots = new[] { new ColumnStrip.Slot(0, 0, 100) };

        var cell = LightRowStrip.Lay(slots, origin: 0, Pad, Pad, ruleThickness: 0)[0];

        Assert.Equal(100 - Pad - Pad, cell.TextWidth);
        Assert.Equal(100, cell.RuleX);
    }
}
