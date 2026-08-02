using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using System;
#if !WINDOWS
using WinUI.TableView.Extensions;
#endif

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
    private const double DefaultIndentPerLevel = 16d;

    public TableViewTreeColumn()
    {
        IsReadOnly = true;
    }

    /// <summary>Pixels of indent each level of depth adds. A deep tree in a narrow column spends
    /// all its width on indent, so a consumer can trade indent for room to read the names in.</summary>
    public double IndentPerLevel { get; set; } = DefaultIndentPerLevel;

    /// <summary>Binding for the optional per-row icon glyph (string).</summary>
    public Binding? GlyphBinding { get; set; }

    /// <summary>Binding for the optional icon foreground (Brush).</summary>
    public Binding? GlyphForegroundBinding { get; set; }

    /// <summary>Font used for the icon glyph (e.g. a Font Awesome family).</summary>
    public FontFamily? GlyphFontFamily { get; set; }

    /// <summary>Optional bool binding; while it reads true the glyph is hidden and
    /// <see cref="GlyphOverrideTemplate"/> is shown in its place (e.g. a now-playing
    /// equaliser on the current row).</summary>
    public Binding? GlyphOverrideBinding { get; set; }

    /// <summary>Content shown in the glyph's slot while <see cref="GlyphOverrideBinding"/>
    /// is true. Its DataContext is the row item, so it can bind the row's own state.</summary>
    public DataTemplate? GlyphOverrideTemplate { get; set; }

    /// <summary>Optional bool binding; while it reads true, <see cref="IconTemplate"/> is shown in
    /// the glyph's slot (e.g. album art on a group row). Independent of the glyph override: the icon
    /// overlays the same slot, so a row that shows it should bind its glyph to an empty string.</summary>
    public Binding? IconVisibleBinding { get; set; }

    /// <summary>Content shown in the glyph's slot while <see cref="IconVisibleBinding"/> is true.
    /// Its DataContext is the row item, so it can bind the row's own state (a cover, a kind glyph).</summary>
    public DataTemplate? IconTemplate { get; set; }

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
            ConverterParameter = IndentPerLevel,
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
#if !WINDOWS
                // Uno-Skia leaves a stale gap after the flattener removes/inserts rows;
                // ask the owning TableView to re-realize the panel (no-op on WinUI).
                (s as FrameworkElement)?.FindAscendant<TableView>()?.RefreshAfterTreeToggle();
#endif
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

            var hasOverride = GlyphOverrideBinding is not null && GlyphOverrideTemplate is not null;
            var hasIcon = IconVisibleBinding is not null && IconTemplate is not null;

            if (hasOverride || hasIcon)
            {
                // Everything shares one slot, overlaid in a Grid: the override flag hides the glyph
                // and shows the override (a now-playing animation); the icon flag lays its template
                // over the same spot (a group row's art) — those rows bind their glyph to an empty
                // string, so the layers never fight.
                var slot = new Grid { VerticalAlignment = VerticalAlignment.Center };

                if (hasOverride)
                {
                    glyph.SetBinding(UIElement.VisibilityProperty, new Binding
                    {
                        Path = GlyphOverrideBinding!.Path,
                        Converter = OverrideVisibilityConverter.WhenFalse,
                    });

                    var over = new ContentControl
                    {
                        ContentTemplate = GlyphOverrideTemplate,
                        VerticalAlignment = VerticalAlignment.Center,
                        IsTabStop = false,
                    };
                    over.SetBinding(ContentControl.ContentProperty, new Binding()); // the row item
                    over.SetBinding(UIElement.VisibilityProperty, new Binding
                    {
                        Path = GlyphOverrideBinding.Path,
                        Converter = OverrideVisibilityConverter.WhenTrue,
                    });
                    slot.Children.Add(glyph);
                    slot.Children.Add(over);
                }
                else
                {
                    slot.Children.Add(glyph);
                }

                if (hasIcon)
                {
                    var icon = new ContentControl
                    {
                        ContentTemplate = IconTemplate,
                        VerticalAlignment = VerticalAlignment.Center,
                        IsTabStop = false,
                    };
                    icon.SetBinding(ContentControl.ContentProperty, new Binding()); // the row item
                    icon.SetBinding(UIElement.VisibilityProperty, new Binding
                    {
                        Path = IconVisibleBinding!.Path,
                        Converter = OverrideVisibilityConverter.WhenTrue,
                    });
                    slot.Children.Add(icon);
                }

                panel.Children.Add(slot);
            }
            else
            {
                panel.Children.Add(glyph);
            }
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
        {
            var perLevel = parameter is double p ? p : DefaultIndentPerLevel;
            return new Thickness(4 + perLevel * (value is int depth ? depth : 0), 0, 12, 0);
        }

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

    /// <summary>bool → Visibility for the glyph-override slot. <see cref="WhenTrue"/> shows the
    /// override on the true row; <see cref="WhenFalse"/> shows the glyph on every other.</summary>
    private sealed partial class OverrideVisibilityConverter : IValueConverter
    {
        public static readonly OverrideVisibilityConverter WhenTrue = new(false);
        public static readonly OverrideVisibilityConverter WhenFalse = new(true);

        private readonly bool _invert;
        private OverrideVisibilityConverter(bool invert) => _invert = invert;

        public object Convert(object value, Type targetType, object parameter, string language)
            => (value is true) != _invert ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }
}
