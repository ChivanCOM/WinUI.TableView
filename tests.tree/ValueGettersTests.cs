using WinUI.TableView.Extensions;

namespace WinUI.TableView.TreeTests;

/// <summary>
/// The per-type compiled-getter cache that every cell's value is read through.
///
/// <para>A compiled getter casts its <c>object</c> parameter to the exact runtime type it was built
/// from, so one cached getter is only correct while every row in the grid is the same type. A
/// virtualized source breaks that by design: it hands out a placeholder sentinel for rows whose page
/// has not arrived, and the first one to reach a getter compiled against the real row type threw
/// <see cref="InvalidCastException"/> out of an automation peer — which is not a call site any host
/// can catch.</para>
/// </summary>
public class ValueGettersTests
{
    private sealed class Row
    {
        public string Title { get; init; } = "";
        public int Year { get; init; }
        public Nested? Inner { get; init; }
    }

    private sealed class Nested
    {
        public int Number { get; init; }
    }

    /// <summary>A source's stand-in for a row it has not fetched yet: shares no property with the
    /// real row, which is exactly what makes it dangerous to a getter built for one.</summary>
    private sealed class Placeholder
    {
        public override string ToString() => "";
    }

    [Fact]
    public void Reads_the_property_off_the_type_it_was_compiled_for()
    {
        var getters = new ObjectExtensions.ValueGetters();
        var row = new Row { Title = "Born In The Echoes" };

        Assert.Equal("Born In The Echoes", getters.For(row, "Title")!(row));
    }

    [Fact]
    public void A_foreign_type_after_a_real_row_does_not_throw()
    {
        var getters = new ObjectExtensions.ValueGetters();

        // The real row first, so a single-slot cache would be holding a Row-typed getter.
        var row = new Row { Title = "Come With Us" };
        Assert.Equal("Come With Us", getters.For(row, "Title")!(row));

        // The placeholder has no Title at all: no getter, and above all no cast exception.
        Assert.Null(getters.For(new Placeholder(), "Title"));
    }

    [Fact]
    public void Each_type_keeps_its_own_getter_across_an_alternating_run()
    {
        var getters = new ObjectExtensions.ValueGetters();
        var placeholder = new Placeholder();

        // A scrolling grid alternates constantly between landed rows and placeholders; every real row
        // must go on reading correctly however many placeholders came between.
        for (var i = 0; i < 50; i++)
        {
            var row = new Row { Title = $"Track {i}" };
            Assert.Equal($"Track {i}", getters.For(row, "Title")!(row));
            Assert.Null(getters.For(placeholder, "Title"));
        }
    }

    [Fact]
    public void Nested_paths_resolve_and_a_null_link_reads_as_nothing()
    {
        var getters = new ObjectExtensions.ValueGetters();

        var full = new Row { Inner = new Nested { Number = 7 } };
        Assert.Equal(7, getters.For(full, "Inner.Number")!(full));

        // Same type, so the same cached getter — it must not fall over on a null link.
        var empty = new Row();
        Assert.Null(getters.For(empty, "Inner.Number")!(empty));
    }

    /// <summary>Lifting a value-typed leaf to its nullable form for the null branch must not change
    /// what a present value reads as — a year is still an int, boxed, not a string of one.</summary>
    [Fact]
    public void A_value_typed_field_still_reads_as_its_own_value()
    {
        var getters = new ObjectExtensions.ValueGetters();
        var row = new Row { Year = 2015 };

        var value = getters.For(row, "Year")!(row);
        Assert.Equal(2015, value);
        Assert.IsType<int>(value);
    }

    [Fact]
    public void A_path_that_resolves_on_neither_type_yields_no_getter()
    {
        var getters = new ObjectExtensions.ValueGetters();

        Assert.Null(getters.For(new Row(), "NoSuchThing"));
        Assert.Null(getters.For(new Placeholder(), "NoSuchThing"));
    }
}
