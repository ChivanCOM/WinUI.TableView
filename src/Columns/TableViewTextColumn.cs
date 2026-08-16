using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinUI.TableView;

/// <summary>
/// Represents a column in a TableView that displays text.
/// </summary>
[StyleTypedProperty(Property = nameof(ElementStyle), StyleTargetType = typeof(TextBlock))]
[StyleTypedProperty(Property = nameof(EditingElementStyle), StyleTargetType = typeof(TextBox))]
#if WINDOWS
[WinRT.GeneratedBindableCustomProperty]
#endif
public partial class TableViewTextColumn : TableViewBoundColumn
{
    /// <summary>
    /// Generates a TextBlock element for the cell.
    /// </summary>
    /// <param name="cell">The cell for which the element is generated.</param>
    /// <param name="dataItem">The data item associated with the cell.</param>
    /// <returns>A TextBlock element.</returns>
    public override FrameworkElement GenerateElement(TableViewCell cell, object? dataItem)
    {
        var textBlock = new TextBlock
        {
            Margin = new Thickness(12, 0, 12, 0),
        };

        // Written rather than bound — see RefreshElement. A column with no path to read (a converter
        // over the item itself, a source of its own) keeps the binding, because there is nothing to
        // read directly.
        if (CanReadDirectly)
        {
            textBlock.Text = TextFor(dataItem);
        }
        else
        {
            textBlock.SetBinding(TextBlock.TextProperty, Binding);
        }

        return textBlock;
    }

    /// <summary>
    /// Puts the new item's value into the cell a recycled row already has.
    ///
    /// <para>This was a binding's job and is now done by hand, because on a fast scroll the binding
    /// is most of what a cell costs. A row crossing the viewport is re-shown for every line it
    /// passes, and each re-show woke a binding expression per cell to walk a path, read a property
    /// and write a dependency property — for a value a compiled getter returns directly. Measured
    /// against a repeater showing the same rows the same way, text layout alone was about eight
    /// kilobytes a cell and the real grid spent appreciably more. The layout is unavoidable; the
    /// rest of it was not.</para>
    ///
    /// <para>The equality check earns its place as much as the reading does. Assigning a string that
    /// has not changed still writes a dependency property and dirties the text layout, and a scroll
    /// re-shows a great many rows whose columns hold the same words as the row above.</para>
    ///
    /// <para>What is given up is the binding's own subscription: a row that raises PropertyChanged no
    /// longer updates itself. <see cref="TableView"/> refreshes the cells of the row that changed
    /// instead — the same work, on the rare path rather than on every row of every scroll.</para>
    /// </summary>
    public override void RefreshElement(TableViewCell cell, object? dataItem)
    {
        if (!CanReadDirectly || cell.Content is not TextBlock textBlock)
        {
            return;
        }

        var text = TextFor(dataItem);
        if (!string.Equals(textBlock.Text, text, StringComparison.Ordinal))
        {
            TableView.DiagCellTextSets++;
            textBlock.Text = text;
        }
    }

    /// <summary>Whether the value can be had without a binding expression: there is a path to read,
    /// so the compiled getter behind <see cref="TableViewColumn.GetCellContent"/> can read it.</summary>
    private bool CanReadDirectly => !string.IsNullOrWhiteSpace(PropertyPath);

    /// <inheritdoc/>
    /// <remarks>
    /// A text column is the one kind a light row can draw itself: a string in a TextBlock, which is
    /// what this column generates anyway. It steps back where the cell around it was doing something
    /// — a style on the element, a style chosen per row, a tooltip per row — because a panel drawing
    /// text blocks has nowhere to put any of that, and silently dropping it would be worse than
    /// keeping the cell.
    /// </remarks>
    public override bool CanRenderAsText => CanReadDirectly
        && ElementStyle is null
        && ConditionalCellStyles.Count == 0
        && GetCellToolTip is null;

    /// <inheritdoc/>
    public override string GetCellText(object? dataItem) => TextFor(dataItem);

    private string TextFor(object? dataItem) => GetCellContent(dataItem)?.ToString() ?? string.Empty;

    /// <summary>
    /// Generates a TextBox element for editing the cell.
    /// </summary>
    /// <param name="cell">The cell for which the editing element is generated.</param>
    /// <param name="dataItem">The data item associated with the cell.</param>
    /// <returns>A TextBox element.</returns>
    public override FrameworkElement GenerateEditingElement(TableViewCell cell, object? dataItem)
    {
        var textBox = new TextBox();
        textBox.SetBinding(TextBox.TextProperty, Binding);
#if !WINDOWS
        textBox.DataContext = dataItem;
#endif
        return textBox;
    }

    /// <inheritdoc/>
    protected internal override object? PrepareCellForEdit(TableViewCell cell, RoutedEventArgs routedEvent)
    {
        if (cell.Content is TextBox textBox)
        {
            textBox.SelectAll();
            return textBox.Text;
        }

        return base.PrepareCellForEdit(cell, routedEvent);
    }

    /// <inheritdoc/>
    protected internal override void EndCellEditing(TableViewCell cell, object? dataItem, TableViewEditAction editAction, object? uneditedValue)
    {
        if (cell.Content is TextBox textBox)
        {
            if (editAction == TableViewEditAction.Commit)
            {
                var bindingExpression = textBox.GetBindingExpression(TextBox.TextProperty);
                bindingExpression?.UpdateSource();
            }
            else
            {
                // Cancel — revert the editor (which with UpdateSourceTrigger=
                // PropertyChanged also pushes the unedited value back to the
                // bound source, undoing every keystroke since edit began).
                textBox.Text = uneditedValue as string ?? string.Empty;
                textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            }
        }
    }
}
