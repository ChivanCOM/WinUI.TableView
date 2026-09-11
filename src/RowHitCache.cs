// FOBO fork addition.
//
// Which row is under a sweeping pointer, remembered between moves.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using Windows.Foundation;
using WinUI.TableView.Extensions;

namespace WinUI.TableView;

/// <summary>
/// The row under a point, with the last answer kept so consecutive moves are free.
///
/// <para>A rubber-band sweep hit-tests on every manipulation delta, and a hit test is a walk of the
/// ScrollViewer's subtree — the hot cost of dragging across a big viewport. What makes caching work
/// is choosing the right unit. A ROW spans the full width of the grid, so a pointer moving along it
/// answers from the cache and only a move into another row pays for a walk. A CELL is one column
/// wide, so on a grid of a dozen columns a sweep missed the cache on almost every move and walked the
/// subtree each time; that is what made the import queue's sweep drag behind the pointer while the
/// library's, which hit-tests rows, kept up.</para>
///
/// <para>The scroll offsets are part of the key: the same point is a different row once the view has
/// moved. <see cref="Forget"/> at the end of a gesture, because containers recycle between them.</para>
/// </summary>
internal sealed class RowHitCache
{
    private TableViewRow? _row;
    private Rect _bounds;
    private double _verticalOffset;
    private double _horizontalOffset;

    /// <summary>Drops the remembered row. Call when a gesture ends.</summary>
    public void Forget() => _row = null;

    /// <summary>
    /// The row under <paramref name="position"/>, which is in <paramref name="from"/>'s coordinates.
    /// </summary>
    public TableViewRow? Find(FrameworkElement from, TableView tableView, Point position)
    {
        if (tableView.FindDescendant<ScrollViewer>() is not { } scrollViewer)
        {
            return null;
        }

        try
        {
            var point = from.TransformToVisual(null).TransformPoint(position);

            if (_row is { IsLoaded: true } cached
                && ReferenceEquals(cached.TableView, tableView)
                && _verticalOffset == scrollViewer.VerticalOffset
                && _horizontalOffset == tableView.HorizontalOffset
                && _bounds.Contains(point))
            {
                return cached;
            }

            var row = TableView.RowFromHitTest(point, scrollViewer);

            if (row is not null)
            {
                _row = row;
                _bounds = row.TransformToVisual(null)
                             .TransformBounds(new Rect(0, 0, row.ActualWidth, row.ActualHeight));
                _verticalOffset = scrollViewer.VerticalOffset;
                _horizontalOffset = tableView.HorizontalOffset;
            }

            return row;
        }
        catch (ArgumentException)
        {
            return null;   // element not in the visual tree during container recycling
        }
    }
}
