// FOBO fork addition.
//
// Pluggable items-source contract for <see cref="TableView"/>. The default
// implementation (see <see cref="CollectionView"/>) materialises every
// source item into an in-memory list and runs filter + sort on that list.
// That works well for small to medium collections but doesn't scale to
// hundreds of thousands of rows because every filter/sort change
// re-iterates the whole source.
//
// This interface lets a host application plug in an alternative source
// that fetches windowed pages on demand (e.g. SQL-backed paging) without
// forking the control further. <see cref="TableView"/> only depends on
// this interface for its data plumbing — anything that satisfies the
// contract can replace <see cref="CollectionView"/> wholesale.

using Microsoft.UI.Xaml.Data;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using Windows.Foundation;

namespace WinUI.TableView;

/// <summary>
/// The contract <see cref="TableView"/> requires of its items source. Any
/// class that satisfies this interface can replace the default in-memory
/// <see cref="CollectionView"/> — including fully virtual sources where
/// items are fetched on demand instead of being held in a list.
/// </summary>
/// <remarks>
/// <para>
/// Implementations must:
/// </para>
/// <list type="bullet">
///   <item><description>Behave as an observable, ordered collection of
///     objects. <see cref="TableView"/> wires its base
///     <see cref="Microsoft.UI.Xaml.Controls.ItemsControl.ItemsSource"/>
///     to this instance, so the
///     <see cref="ICollectionView"/> contract (which is itself a
///     superset of <see cref="IList"/>,
///     <see cref="System.Collections.Specialized.INotifyCollectionChanged"/>,
///     and the WinRT <see cref="Windows.Foundation.Collections.IObservableVector{T}"/>)
///     is non-negotiable.</description></item>
///   <item><description>Surface mutable <see cref="SortDescriptions"/>
///     and <see cref="FilterDescriptions"/> collections — column headers
///     and the filter handler add and remove descriptions on these to
///     drive sort and filter UX.</description></item>
///   <item><description>Honour <see cref="DeferRefresh"/> by batching
///     change notifications until the deferral disposes; this keeps the
///     UI from flashing when several mutations happen in quick
///     succession.</description></item>
///   <item><description>Fire <see cref="ItemPropertyChanged"/> when an
///     item raises
///     <see cref="INotifyPropertyChanged.PropertyChanged"/>, so
///     <see cref="TableView"/> can refresh conditional cell styles for
///     the affected row.</description></item>
/// </list>
/// </remarks>
public interface ITableViewItemsSource :
    ICollectionView,
    INotifyPropertyChanged
{
    /// <summary>
    /// Gets or sets the underlying source collection. Setting this should
    /// re-evaluate filters + sort and raise a vector reset so the
    /// <see cref="TableView"/> rebinds.
    /// </summary>
    IEnumerable Source { get; set; }

    /// <summary>
    /// Gets the mutable list of sort descriptions applied to the items.
    /// Adding or removing entries should trigger a re-sort, subject to
    /// any active <see cref="DeferRefresh"/>.
    /// </summary>
    IList<SortDescription> SortDescriptions { get; }

    /// <summary>
    /// Gets the mutable list of filter descriptions applied to the items.
    /// Adding or removing entries should trigger a re-filter, subject to
    /// any active <see cref="DeferRefresh"/>.
    /// </summary>
    IList<FilterDescription> FilterDescriptions { get; }

    /// <summary>
    /// Gets or sets whether item-level property changes should re-shape
    /// the view (re-filter / re-sort the affected item) live. When
    /// <see langword="false"/>, item changes are ignored until
    /// <see cref="Refresh"/> is called.
    /// </summary>
    bool AllowLiveShaping { get; set; }

    /// <summary>
    /// Begins a batch of mutations. Disposing the returned deferral
    /// triggers a single refresh covering everything that happened
    /// during the batch. Multiple overlapping deferrals stack — the
    /// refresh fires only when the last one disposes.
    /// </summary>
    Deferral DeferRefresh();

    /// <summary>Re-evaluates the entire source (filter + sort).</summary>
    void Refresh();

    /// <summary>Re-applies the current sort descriptions only.</summary>
    void RefreshSorting();

    /// <summary>Re-applies the current filter descriptions only.</summary>
    void RefreshFilter();

    /// <summary>
    /// Fires when an item already in the view raises
    /// <see cref="INotifyPropertyChanged.PropertyChanged"/>.
    /// <see cref="TableView"/> uses this to refresh conditional cell
    /// styles on the row that owns the item.
    /// </summary>
    event PropertyChangedEventHandler? ItemPropertyChanged;
}
