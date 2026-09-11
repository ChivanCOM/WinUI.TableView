// FOBO fork addition.
//
// The mark a virtual items source puts on the stand-in it returns for an index whose page has not
// arrived yet. It says one thing: this object is not a row, so nothing may bind to it by path.

namespace WinUI.TableView;

/// <summary>
/// A stand-in an items source returns for an index whose page has not been fetched. It has none of
/// the host's row properties, so it must never become a data context: a <c>{Binding Title}</c> in a
/// cell template resolves against it, finds no such property, and logs a binding error. That is one
/// line per bound element per cell per realization, written with OutputDebugString, which under a
/// debugger costs more than the scroll it is describing.
/// </summary>
/// <remarks>
/// <see cref="TableViewTemplateColumn"/> is what reads this: it passes null instead of the
/// placeholder to the cell's content and data context, and bindings against a null data context are
/// silent. Column types that read values directly (see <see cref="TableViewColumn.GetCellContent"/>)
/// need nothing — a compiled getter for a property the placeholder does not have is already null.
///
/// <para>Silent is not the same as cleared. A binding that yields nothing leaves its target holding
/// what it had, which on a recycled element is the previous row's value and on a fresh one is the
/// property's default — and Visibility's default is Visible. So the template column also COLLAPSES
/// the cell element while the item is a placeholder; see its ShowItem. Nothing in a cell template
/// should be relied on to hide itself.</para>
///
/// <para>A source whose placeholders are real instances of the host's row type (see
/// <see cref="VirtualTreeModel"/>, whose placeholders come from the host) does not implement this
/// and should not: those bind correctly, and blanking them would blank rows that have something to
/// show.</para>
/// </remarks>
public interface ITableViewPlaceholderItem
{
}
