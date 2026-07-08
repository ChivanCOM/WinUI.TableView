using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using System;

namespace WinUI.TableView;

/// <summary>
/// A read-only column that renders the hierarchy of <see cref="ITreeGridRow"/> items:
/// depth-based indent, an expander chevron for rows with children, an optional glyph,
/// and the bound text. Pair the TableView's ItemsSource with
/// <see cref="TreeGridFlattener{T}"/> so toggling the chevron shows/hides descendants.
///
/// Optional glyph: set <see cref="GlyphBinding"/> (and <see cref="GlyphFontFamily"/>)
/// to render an icon between the chevron and the text;
/// <see cref="GlyphForegroundBinding"/> colors it per row.
/// </summary>
public partial class TableViewTreeColumn : TableViewBoundColumn
{
    private const double IndentPerLevel = 16d;

    public TableViewTreeColumn()
    {
        IsReadOnly = true;
    }

    /// <summary>Binding for the optional per-row icon glyph (string).</summary>
    public Binding? GlyphBinding { get; set; }

    /// <summary>Binding for the optional icon foreground (Brush).</summary>
    public Binding? GlyphForegroundBinding { get; set; }

    /// <summary>Font used for the icon glyph (e.g. a Font Awesome family).</summary>
    public FontFamily? GlyphFontFamily { get; set; }

    /// <inheritdoc/>
    public override FrameworkElement GenerateElement(TableViewCell cell, object? dataItem)
    {
        var row = dataItem as ITreeGridRow;

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4 + IndentPerLevel * (row?.Depth ?? 0), 0, 12, 0),
        };

        // A real Button: TableViewCell handles pointer events for selection/edit, which
        // swallows Tapped on plain elements — Click still gets through.
        var chevronText = new TextBlock
        {
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center,
        };
        var chevron = new Button
        {
            Content = chevronText,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(2, 0, 2, 0),
            MinWidth = 0,
            MinHeight = 0,
            Width = 18,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsTabStop = false,
        };
        if (row?.HasChildren == true)
        {
            chevronText.SetBinding(TextBlock.TextProperty, new Binding
            {
                Path = new PropertyPath(nameof(ITreeGridRow.IsExpanded)),
                Converter = ChevronGlyphConverter.Instance,
            });
            chevron.Click += (_, _) => row.IsExpanded = !row.IsExpanded;
        }
        else
        {
            chevron.IsHitTestVisible = false;
        }
        panel.Children.Add(chevron);

        if (GlyphBinding is not null)
        {
            var glyph = new TextBlock
            {
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (GlyphFontFamily is not null)
                glyph.FontFamily = GlyphFontFamily;
            glyph.SetBinding(TextBlock.TextProperty, GlyphBinding);
            if (GlyphForegroundBinding is not null)
                glyph.SetBinding(TextBlock.ForegroundProperty, GlyphForegroundBinding);
            panel.Children.Add(glyph);
        }

        var text = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        text.SetBinding(TextBlock.TextProperty, Binding);
        panel.Children.Add(text);

#if !WINDOWS
        panel.DataContext = dataItem;
#endif
        return panel;
    }

    /// <inheritdoc/>
    public override FrameworkElement GenerateEditingElement(TableViewCell cell, object? dataItem)
        => GenerateElement(cell, dataItem); // never editable

    private sealed partial class ChevronGlyphConverter : IValueConverter
    {
        public static readonly ChevronGlyphConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, string language)
            => value is true ? "▾" : "▸"; // ▾ / ▸

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }
}
