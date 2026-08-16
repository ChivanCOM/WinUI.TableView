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
        return new ContentControl
        {
            // Content drives the DataContext inside the template; without it {Binding} in the CellTemplate
            // resolves against null and every template binding silently yields nothing.
            Content = dataItem,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            ContentTemplate = CellTemplateSelector?.SelectTemplate(dataItem) ?? CellTemplate
        };
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
            return new ContentControl
            {
                Content = dataItem, // DataContext for the editing template (see GenerateElement)
                VerticalContentAlignment = VerticalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                ContentTemplate = EditingTemplateSelector?.SelectTemplate(dataItem) ?? EditingTemplate
            };
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
            existing.Content = dataItem;
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
    public override FrameworkElement CreateCellFreeElement(object? dataItem) => new ContentControl
    {
        // Content drives the DataContext inside the template; without it {Binding} in the
        // CellTemplate resolves against null and every template binding silently yields nothing.
        Content = dataItem,
        VerticalContentAlignment = VerticalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        ContentTemplate = CellTemplateSelector?.SelectTemplate(dataItem) ?? CellTemplate
    };

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

        content.Content = dataItem;
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
