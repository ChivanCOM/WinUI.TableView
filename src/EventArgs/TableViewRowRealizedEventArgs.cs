// FOBO fork addition.
//
// A row has taken on an item and is about to be seen.

namespace WinUI.TableView;

/// <summary>
/// Provides data for the RowRealized event: a container has been prepared for an item, whether it was
/// built for it or recycled onto it.
///
/// <para>What it is for is state a host keeps OUTSIDE the item — the row's selection above all, which
/// belongs to the list rather than to the row. A host that pushes such state onto the rows it can see
/// is right until a row it could not see arrives, which in a virtualized grid is most of them: a page
/// that lands under the fold, a container recycled during a fling. Answering the question when the
/// row asks it is the only version of this that cannot be missed.</para>
/// </summary>
public partial class TableViewRowRealizedEventArgs : System.EventArgs
{
    /// <summary>
    /// Initializes a new instance of the TableViewRowRealizedEventArgs class.
    /// </summary>
    /// <param name="row">The row that was prepared.</param>
    /// <param name="item">The item it is now showing.</param>
    public TableViewRowRealizedEventArgs(TableViewRow row, object? item)
    {
        Row = row;
        Item = item;
    }

    /// <summary>
    /// Gets the TableViewRow that was prepared.
    /// </summary>
    public TableViewRow Row { get; }

    /// <summary>
    /// Gets the item the row is showing.
    /// </summary>
    public object? Item { get; }
}
