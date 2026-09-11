using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace WinUI.TableView;

/// <summary>
/// Represents a column in a TableView that uses a DataTemplate for its content.
/// </summary>
#if WINDOWS
[WinRT.GeneratedBindableCustomProperty]
#endif
[ContentProperty(Name = nameof(CellTemplate))]
public partial class TableViewTemplateColumn : TableViewColumn
{
    /// <summary>
    /// Initializes a new instance of the TableViewTemplateColumn class.
    /// </summary>
    public TableViewTemplateColumn()
    {
        CanSort = false;
        CanFilter = false;
    }

    /// <summary>
    /// Generates a ContentControl for the cell based on CellTemplate and CellTemplateSelector.
    /// </summary>
    /// <param name="cell">The cell for which the element is generated.</param>
    /// <param name="dataItem">The data item associated with the cell.</param>
    /// <returns>A ContentControl element.</returns>
    public override FrameworkElement GenerateElement(TableViewCell cell, object? dataItem)
    {
        var element = new ContentControl
        {
            VerticalContentAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            ContentTemplate = CellTemplateSelector?.SelectTemplate(dataItem) ?? CellTemplate
        };

        // Content drives the DataContext inside the template; without it {Binding} in the CellTemplate
        // resolves against null and every template binding silently yields nothing. DataContext is set
        // and not inherited: see TemplateItem. The row's own DataContext is the item, placeholder
        // included, and the template would read it through this element if this element had none.
        ShowItem(element, dataItem);
        return element;
    }

    /// <summary>
    /// What the cell's template binds against: the item, or null where the source handed out a
    /// placeholder for a page that has not arrived.
    ///
    /// <para>A placeholder is not a row. It has none of the host's properties, so every path binding
    /// in the template fails to resolve against it and WinUI logs a binding error for each one — per
    /// bound element, per cell, per row realized, written with OutputDebugString. A scroll through a
    /// SQL-backed grid produced three lines a row this way. Against a null data context the same
    /// bindings are silent and leave their targets at the defaults, which is what a placeholder cell
    /// is meant to show; the real item arrives through <see cref="RefreshElement"/> when the page
    /// lands.</para>
    /// </summary>
    private static object? TemplateItem(object? dataItem)
        => dataItem is ITableViewPlaceholderItem ? null : dataItem;

    /// <summary>
    /// Points a cell's element at its item, and HIDES the element when there is no item.
    ///
    /// <para>Hiding it is the half that was missing. A placeholder binds against null, and a binding
    /// against null is silent — but silent does not mean it clears its target: the target keeps what it
    /// had, which on a fresh element is the property's default and on a recycled one is the previous
    /// row's value. <see cref="UIElement.Visibility"/> defaults to Visible, so a template whose parts
    /// are SHOWN by a binding drew them on placeholder rows that had nothing behind them at all: a
    /// now-playing animation on a row that was not playing, a drag handle on a row that was not a file.
    /// Both were reported from the app before this was found.</para>
    ///
    /// <para>Collapsing the element says the one thing a placeholder cell means — there is nothing here
    /// yet — and says it without depending on any binding in the template. It costs one property write
    /// per cell, and the real item turns it back on when the page lands.</para>
    /// </summary>
    private static void ShowItem(ContentControl element, object? dataItem)
    {
        var item = TemplateItem(dataItem);

        element.Content = element.DataContext = item;
        element.Visibility = item is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Generates a ContentControl for editing the cell based on EditingTemplate and EditingTemplateSelector. 
    /// If EditingTemplate or EditingTemplateSelector is not set, GenerateElement is used instead to generate the editing element.
    /// </summary>
    /// <param name="cell">The cell for which the editing element is generated.</param>
    /// <param name="dataItem">The data item associated with the cell.</param>
    /// <returns>A ContentControl element.</returns>
    public override FrameworkElement GenerateEditingElement(TableViewCell cell, object? dataItem)
    {
        if (EditingTemplate is not null || EditingTemplateSelector is not null)
        {
            var element = new ContentControl
            {
                VerticalContentAlignment = VerticalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                ContentTemplate = EditingTemplateSelector?.SelectTemplate(dataItem) ?? EditingTemplate
            };

            ShowItem(element, dataItem);   // the editing template's data context (see GenerateElement)
            return element;
        }

        return GenerateElement(cell, dataItem);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Rebinds the existing element where it can. A row is recycled precisely so its visuals
    /// survive and only the data behind them changes — and every other column type honours that,
    /// because a bound column's bindings simply re-resolve. This one used to build a fresh
    /// <see cref="ContentControl"/> and inflate the whole <see cref="CellTemplate"/> again for
    /// every one of its cells on every row that scrolled into view, which is the cost recycling
    /// exists to avoid, paid on the scroll thread. Only a genuinely different template — which is
    /// to say a <see cref="CellTemplateSelector"/> that chose another one for this item — still
    /// needs the element rebuilt.
    /// </remarks>
    public override void RefreshElement(TableViewCell cell, object? dataItem)
    {
        var template = CellTemplateSelector?.SelectTemplate(dataItem) ?? CellTemplate;

        if (cell.Content is ContentControl existing && Equals(existing.ContentTemplate, template))
        {
            ShowItem(existing, dataItem);
            return;
        }

        cell.Content = GenerateElement(cell, dataItem);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The cell around a template column does nothing this column needs: the template already draws
    /// whatever the column shows, and the cell exists to be selected, hovered and edited. On a light
    /// row it is none of those, so the panel hosts the ContentControl directly. It steps back where
    /// the cell WAS doing something — a style chosen per row, a tooltip per row.
    /// </remarks>
    public override bool CanRenderWithoutCell => ConditionalCellStyles.Count == 0 && GetCellToolTip is null;

    /// <inheritdoc/>
    public override FrameworkElement CreateCellFreeElement(object? dataItem)
    {
        var element = new ContentControl
        {
            VerticalContentAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            ContentTemplate = CellTemplateSelector?.SelectTemplate(dataItem) ?? CellTemplate
        };

        // On the light path this element hangs off the row directly, and the row's DataContext is the
        // item, placeholder included — so this element carries its own (see GenerateElement).
        ShowItem(element, dataItem);
        return element;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Rebinds rather than rebuilds, for the reason <see cref="RefreshElement"/> gives: a row is
    /// recycled precisely so its visuals survive and only the data behind them changes. Only a
    /// genuinely different template — a selector that chose another one for this item — is worth
    /// inflating again.
    /// </remarks>
    public override void RefreshCellFreeElement(FrameworkElement element, object? dataItem)
    {
        if (element is not ContentControl content)
        {
            return;
        }

        var template = CellTemplateSelector?.SelectTemplate(dataItem) ?? CellTemplate;

        if (!Equals(content.ContentTemplate, template))
        {
            content.ContentTemplate = template;
        }

        ShowItem(content, dataItem);
    }

    /// <summary>
    /// Gets or sets the DataTemplate for the cell content.
    /// </summary>
    public DataTemplate? CellTemplate
    {
        get => (DataTemplate?)GetValue(CellTemplateProperty);
        set => SetValue(CellTemplateProperty, value);
    }

    /// <summary>
    /// Gets or sets the DataTemplateSelector for the cell content.
    /// </summary>
    public DataTemplateSelector? CellTemplateSelector
    {
        get => (DataTemplateSelector?)GetValue(CellTemplateSelectorProperty);
        set => SetValue(CellTemplateSelectorProperty, value);
    }

    /// <summary>
    /// Gets or sets the DataTemplate for the editing cell content.
    /// </summary>
    public DataTemplate? EditingTemplate
    {
        get => (DataTemplate?)GetValue(EditingTemplateProperty);
        set => SetValue(EditingTemplateProperty, value);
    }

    /// <summary>
    /// Gets or sets the DataTemplateSelector for the editing cell content.
    /// </summary>
    public DataTemplateSelector? EditingTemplateSelector
    {
        get => (DataTemplateSelector?)GetValue(EditingTemplateSelectorProperty);
        set => SetValue(EditingTemplateSelectorProperty, value);
    }

    /// <summary>
    /// Identifies the CellTemplate dependency property.
    /// </summary>
    public static readonly DependencyProperty CellTemplateProperty = DependencyProperty.Register(nameof(CellTemplate), typeof(DataTemplate), typeof(TableViewTemplateColumn), new PropertyMetadata(default));

    /// <summary>
    /// Identifies the CellTemplateSelector dependency property.
    /// </summary>
    public static readonly DependencyProperty CellTemplateSelectorProperty = DependencyProperty.Register(nameof(CellTemplateSelector), typeof(DataTemplateSelector), typeof(TableViewTemplateColumn), new PropertyMetadata(default));

    /// <summary>
    /// Identifies the EditingTemplate dependency property.
    /// </summary>
    public static readonly DependencyProperty EditingTemplateProperty = DependencyProperty.Register(nameof(EditingTemplate), typeof(DataTemplate), typeof(TableViewTemplateColumn), new PropertyMetadata(default));

    /// <summary>
    /// Identifies the EditingTemplateSelector dependency property.
    /// </summary>
    public static readonly DependencyProperty EditingTemplateSelectorProperty = DependencyProperty.Register(nameof(EditingTemplateSelector), typeof(DataTemplateSelector), typeof(TableViewTemplateColumn), new PropertyMetadata(default));
}
