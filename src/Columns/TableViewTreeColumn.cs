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
/// Everything in the generated cell is binding-driven off the cell's DataContext —
/// containers are recycled across items, so nothing may be captured per item
/// (a pinned DataContext or a captured row reference goes stale on recycle).
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
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // Indent follows the (recycled) DataContext, not the item the cell was created for.
        panel.SetBinding(FrameworkElement.MarginProperty, new Binding
        {
            Path = new PropertyPath(nameof(ITreeGridRow.Depth)),
            Converter = DepthToIndentConverter.Instance,
        });

        var chevronText = new TextBlock
        {
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center,
        };
        chevronText.SetBinding(TextBlock.TextProperty, new Binding
        {
            Path = new PropertyPath(nameof(ITreeGridRow.IsExpanded)),
            Converter = ChevronGlyphConverter.Instance,
        });

        // A real Button: TableViewCell handles pointer events for selection/edit, which
        // swallows Tapped on plain elements — Click still gets through. The handler reads
        // the DataContext at click time so recycling can't leave it pointing at an old row.
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
        chevron.SetBinding(UIElement.OpacityProperty, new Binding
        {
            Path = new PropertyPath(nameof(ITreeGridRow.HasChildren)),
            Converter = HasChildrenToOpacityConverter.Instance,
        });
        chevron.SetBinding(Control.IsEnabledProperty, new Binding
        {
            Path = new PropertyPath(nameof(ITreeGridRow.HasChildren)),
        });
        chevron.Click += (s, _) =>
        {
            if ((s as FrameworkElement)?.DataContext is ITreeGridRow row && row.HasChildren)
            {
                row.IsExpanded = !row.IsExpanded;
            }
        };
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

        return panel;
    }

    /// <inheritdoc/>
    public override FrameworkElement GenerateEditingElement(TableViewCell cell, object? dataItem)
        => GenerateElement(cell, dataItem); // never editable

    private sealed partial class ChevronGlyphConverter : IValueConverter
    {
        public static readonly ChevronGlyphConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, string language)
            => value is true ? "▾" : "▸";

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }

    private sealed partial class DepthToIndentConverter : IValueConverter
    {
        public static readonly DepthToIndentConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, string language)
            => new Thickness(4 + IndentPerLevel * (value is int depth ? depth : 0), 0, 12, 0);

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }

    private sealed partial class HasChildrenToOpacityConverter : IValueConverter
    {
        public static readonly HasChildrenToOpacityConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, string language)
            => value is true ? 1d : 0d;

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }
}
