using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;
using System.Linq;
using System.Threading.Tasks;

namespace WinUI.TableView.Tests;

/// <summary>
/// Cover for the fork's per-row cost reductions — the changes that removed work from realizing a row.
///
/// <para>These exist because that work was removed on a machine that cannot run WinUI: the fork's
/// cross-platform suite (tests.tree) is deliberately UI-free, so nothing there can say whether a cell
/// still ends up the right height once its binding is gone. Each test below pins the BEHAVIOUR the
/// optimisation had to preserve, not the optimisation itself.</para>
/// </summary>
[TestClass]
public class ForkPerformanceChangeTests
{
    private sealed class Item
    {
        public string? Name { get; set; }
        public string? Other { get; set; }
    }

    private static TableViewTextColumn Column(string header, string path) => new()
    {
        Header = header,
        Binding = new Binding { Path = new PropertyPath(path) },
    };

    private static async Task<TableView> LoadAsync(TableView tableView)
    {
        tableView.ItemsSource = new[]
        {
            new Item { Name = "A", Other = "1" },
            new Item { Name = "B", Other = "2" },
        };
        await UnitTestApp.Current.MainWindow.LoadTestContentAsync(tableView);
        return tableView;
    }

    private static TableViewRow? FirstRow(TableView tableView)
        => tableView.FindDescendant<TableViewRow>();

    // ── row metrics reach the cells without a binding per cell ────────────────────────────────────
    //
    // Every cell used to carry three live bindings relaying RowHeight / RowMinHeight / RowMaxHeight.
    // A nineteen-column grid therefore held fifty-seven bindings per row, all waiting for a number
    // that does not change while you scroll. They are now set directly and pushed on change; what
    // must still be true is that a cell is the height it is supposed to be, and follows when that
    // changes.

    [UITestMethod]
    public async Task Cells_take_the_row_metrics_when_they_are_created()
    {
        var tableView = new TableView { RowHeight = 33, RowMinHeight = 22, RowMaxHeight = 44 };
        tableView.Columns.Add(Column("Name", nameof(Item.Name)));
        tableView.Columns.Add(Column("Other", nameof(Item.Other)));
        await LoadAsync(tableView);

        var row = FirstRow(tableView);
        Assert.IsNotNull(row, "no row was realized");
        Assert.IsTrue(row!.Cells.Count > 0, "the row realized no cells");

        foreach (var cell in row.Cells)
        {
            Assert.AreEqual(33d, cell.Height, "cell height");
            Assert.AreEqual(22d, cell.MinHeight, "cell min height");
            Assert.AreEqual(44d, cell.MaxHeight, "cell max height");
        }
    }

    [UITestMethod]
    public async Task Cells_follow_the_row_height_when_it_changes_afterwards()
    {
        var tableView = new TableView { RowHeight = 33, RowMinHeight = 22, RowMaxHeight = 44 };
        tableView.Columns.Add(Column("Name", nameof(Item.Name)));
        await LoadAsync(tableView);

        // This is the case the bindings used to cover, and the reason the change needed a push.
        tableView.RowHeight = 55;
        tableView.RowMinHeight = 50;
        tableView.RowMaxHeight = 60;
        tableView.UpdateLayout();

        var row = FirstRow(tableView);
        Assert.IsNotNull(row);
        foreach (var cell in row!.Cells)
        {
            Assert.AreEqual(55d, cell.Height, "cell height after change");
            Assert.AreEqual(50d, cell.MinHeight, "cell min height after change");
            Assert.AreEqual(60d, cell.MaxHeight, "cell max height after change");
        }
    }

    // ── the visible-columns projection is cached, and the cache is not stale ──────────────────────
    //
    // It used to materialise a new list on every read, and a realizing row read it once per cell to
    // find that cell's own index. Caching it is only safe if every route that can change it drops
    // the cache — so each of those routes gets a test.

    [UITestMethod]
    public void Hiding_a_column_updates_the_visible_columns()
    {
        var tableView = new TableView();
        var a = Column("A", nameof(Item.Name));
        var b = Column("B", nameof(Item.Other));
        tableView.Columns.Add(a);
        tableView.Columns.Add(b);

        Assert.AreEqual(2, tableView.Columns.VisibleColumns.Count);

        b.Visibility = Visibility.Collapsed;
        CollectionAssert.AreEqual(new[] { a }, tableView.Columns.VisibleColumns.ToArray());

        b.Visibility = Visibility.Visible;
        Assert.AreEqual(2, tableView.Columns.VisibleColumns.Count);
    }

    [UITestMethod]
    public void Reordering_columns_updates_the_visible_columns()
    {
        var tableView = new TableView();
        var a = Column("A", nameof(Item.Name));
        var b = Column("B", nameof(Item.Other));
        tableView.Columns.Add(a);
        tableView.Columns.Add(b);

        Assert.AreEqual(a, tableView.Columns.VisibleColumns[0]);

        a.Order = 5;
        b.Order = 1;

        Assert.AreEqual(b, tableView.Columns.VisibleColumns[0], "order change did not re-sort");
        Assert.AreEqual(a, tableView.Columns.VisibleColumns[1]);
    }

    [UITestMethod]
    public void Adding_and_removing_columns_updates_the_visible_columns()
    {
        var tableView = new TableView();
        var a = Column("A", nameof(Item.Name));
        tableView.Columns.Add(a);
        Assert.AreEqual(1, tableView.Columns.VisibleColumns.Count);

        var b = Column("B", nameof(Item.Other));
        tableView.Columns.Add(b);
        Assert.AreEqual(2, tableView.Columns.VisibleColumns.Count, "add did not invalidate");

        tableView.Columns.Remove(a);
        CollectionAssert.AreEqual(new[] { b }, tableView.Columns.VisibleColumns.ToArray());

        tableView.Columns.Clear();
        Assert.AreEqual(0, tableView.Columns.VisibleColumns.Count, "clear did not invalidate");
    }

    // ── frozen columns, after the quadratic sweep was rewritten ───────────────────────────────────

    [UITestMethod]
    public void The_leading_visible_columns_are_the_frozen_ones()
    {
        var tableView = new TableView();
        var a = Column("A", nameof(Item.Name));
        var b = Column("B", nameof(Item.Other));
        var c = Column("C", nameof(Item.Name));
        tableView.Columns.Add(a);
        tableView.Columns.Add(b);
        tableView.Columns.Add(c);

        // Setting the count is what the app does, and it is what runs the sweep.
        tableView.FrozenColumnCount = 2;

        Assert.IsTrue(a.IsFrozen, "first column should be frozen");
        Assert.IsTrue(b.IsFrozen, "second column should be frozen");
        Assert.IsFalse(c.IsFrozen, "third column should not be frozen");
    }

    [UITestMethod]
    public void A_hidden_column_is_never_frozen_and_does_not_use_up_a_frozen_slot()
    {
        var tableView = new TableView();
        var hidden = Column("hidden", nameof(Item.Name));
        hidden.Visibility = Visibility.Collapsed;
        var a = Column("A", nameof(Item.Name));
        var b = Column("B", nameof(Item.Other));
        var c = Column("C", nameof(Item.Name));
        tableView.Columns.Add(hidden);
        tableView.Columns.Add(a);
        tableView.Columns.Add(b);
        tableView.Columns.Add(c);

        tableView.FrozenColumnCount = 2;

        Assert.IsFalse(hidden.IsFrozen, "a hidden column must not be frozen");
        Assert.IsTrue(a.IsFrozen);
        Assert.IsTrue(b.IsFrozen);
        Assert.IsFalse(c.IsFrozen);
    }

    // ── the tree column's new tooltip binding ─────────────────────────────────────────────────────

    [UITestMethod]
    public async Task The_tree_columns_glyph_carries_the_tooltip_it_is_bound_to()
    {
        // A mark that stands for a state is half a message until it can say which state — this is
        // what the import queue's red question mark uses to name the fields that are missing.
        var tableView = new TableView();
        tableView.Columns.Add(new TableViewTreeColumn
        {
            Header = "Name",
            Binding = new Binding { Path = new PropertyPath(nameof(Item.Name)) },
            GlyphBinding = new Binding { Path = new PropertyPath(nameof(Item.Other)) },
            GlyphToolTipBinding = new Binding { Path = new PropertyPath(nameof(Item.Name)) },
        });
        await LoadAsync(tableView);

        var row = FirstRow(tableView);
        Assert.IsNotNull(row);

        var cell = row!.Cells.FirstOrDefault();
        Assert.IsNotNull(cell, "the tree column realized no cell");

        // The glyph is the TextBlock showing the glyph binding's value.
        var glyph = cell!.FindDescendant<TextBlock>(t => t.Text == "1");
        Assert.IsNotNull(glyph, "the glyph did not render its bound value");
        Assert.AreEqual("A", ToolTipService.GetToolTip(glyph) as string,
            "the glyph did not take the tooltip it was bound to");
    }

    [UITestMethod]
    public async Task A_tree_column_without_a_tooltip_binding_sets_no_tooltip()
    {
        var tableView = new TableView();
        tableView.Columns.Add(new TableViewTreeColumn
        {
            Header = "Name",
            Binding = new Binding { Path = new PropertyPath(nameof(Item.Name)) },
            GlyphBinding = new Binding { Path = new PropertyPath(nameof(Item.Other)) },
        });
        await LoadAsync(tableView);

        var cell = FirstRow(tableView)?.Cells.FirstOrDefault();
        Assert.IsNotNull(cell);

        var glyph = cell!.FindDescendant<TextBlock>(t => t.Text == "1");
        Assert.IsNotNull(glyph);
        Assert.IsNull(ToolTipService.GetToolTip(glyph), "a tooltip appeared that nothing asked for");
    }
}
