// FOBO fork addition.
//
// Companion to SqlBackedItemsSource. The default ColumnFilterHandler
// powers its per-column filter flyout by enumerating every item in the
// source — which is exactly the operation a SQL-backed virtual list
// must avoid (it would page through every row from disk just to build
// the dropdown). This handler delegates to a host-supplied callback
// that issues a SELECT DISTINCT against the same store, so the flyout
// shows real values without breaking virtualization.
//
// The handler is intentionally synchronous against the callback — the
// flyout opens in the same UI tick as the click, and IColumnFilterHandler
// is sync. Indexed columns return values fast enough (sub-100 ms at 1M
// rows on SQLite) that a brief inline block is acceptable. Async + a
// loading state is a possible future enhancement; today the upstream
// flyout has no async hook either.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace WinUI.TableView;

/// <summary>
/// SQL-backed column filter handler. Replace the default
/// <see cref="ColumnFilterHandler"/> with one of these when the
/// <see cref="TableView"/>'s items source is virtualised — the
/// distinct-values lookup runs against the same store, not by
/// iterating the in-memory item list.
/// </summary>
/// <remarks>
/// <para>
/// <b>Wiring:</b> the host typically pairs this with a
/// <see cref="SqlBackedItemsSource"/>. Set the table's
/// <see cref="TableView.FilterHandler"/> before assigning ItemsSource:
/// </para>
/// <code>
/// tableView.FilterHandler = new SqlBackedColumnFilterHandler(
///     tableView,
///     distinctAsync:    GetDistinctValuesFromStore,
///     onFilterChanged:  () =&gt; sqlSource.Refresh());
/// tableView.ItemsSource = sqlSource;
/// </code>
/// <para>
/// <b>Filter state:</b> the handler stores the user's selected values
/// in <see cref="SelectedValues"/>; the host reads from this dictionary
/// when building the SQL WHERE clause for paging queries. The handler
/// does not own the SQL filter translation — that's the host's job —
/// because only the host knows how its store represents fields.
/// </para>
/// </remarks>
public class SqlBackedColumnFilterHandler : IColumnFilterHandler
{
    /// <summary>
    /// Returns up to <paramref name="limit"/> distinct values for
    /// <paramref name="column"/>, narrowed to entries whose textual
    /// representation starts with <paramref name="searchText"/> when
    /// non-empty (the type-ahead box inside the flyout). Implementations
    /// typically build a <c>SELECT DISTINCT col FROM t WHERE …</c>
    /// against their store; including the other columns' filter state
    /// is recommended so the user sees only values still reachable.
    /// </summary>
    public delegate IList<object?> DistinctAsyncFn(
        TableViewColumn column, string? searchText, int limit, CancellationToken ct);

    private readonly TableView          _tableView;
    private readonly DistinctAsyncFn    _distinctAsync;
    private readonly Action             _onFilterChanged;
    private readonly int                _maxItemsPerFlyout;

    /// <summary>
    /// Initializes a new SQL-backed handler.
    /// </summary>
    /// <param name="tableView">The owning <see cref="TableView"/>.</param>
    /// <param name="distinctAsync">
    /// Host-supplied callback that returns distinct values for a column.
    /// Called inline from <see cref="GetFilterItems"/>; the implementation
    /// should be fast (indexed-column DISTINCT against the underlying
    /// store).
    /// </param>
    /// <param name="onFilterChanged">
    /// Invoked after the handler mutates filter state — typically the
    /// host calls <c>SqlBackedItemsSource.Refresh</c> here so the grid
    /// re-fetches with the new WHERE clause.
    /// </param>
    /// <param name="maxItemsPerFlyout">
    /// Upper bound passed to <paramref name="distinctAsync"/>. 1000 is
    /// the same kind of cap Excel uses; high-cardinality columns get
    /// truncated, which is what the flyout's type-ahead box exists to
    /// drill into.
    /// </param>
    public SqlBackedColumnFilterHandler(
        TableView          tableView,
        DistinctAsyncFn    distinctAsync,
        Action             onFilterChanged,
        int                maxItemsPerFlyout = 1000)
    {
        _tableView         = tableView ?? throw new ArgumentNullException(nameof(tableView));
        _distinctAsync     = distinctAsync ?? throw new ArgumentNullException(nameof(distinctAsync));
        _onFilterChanged   = onFilterChanged ?? throw new ArgumentNullException(nameof(onFilterChanged));
        _maxItemsPerFlyout = maxItemsPerFlyout;
    }

    /// <inheritdoc/>
    public IDictionary<TableViewColumn, ICollection<object?>> SelectedValues { get; }
        = new Dictionary<TableViewColumn, ICollection<object?>>();

    /// <inheritdoc/>
    public virtual IList<TableViewFilterItem> GetFilterItems(TableViewColumn column, string? searchText)
    {
        // Synchronous DISTINCT lookup. Acceptable here because indexed
        // columns return in tens of ms, and the flyout would otherwise
        // need an async-ready binding surface that the upstream control
        // doesn't provide today.
        var values = _distinctAsync(column, searchText, _maxItemsPerFlyout, CancellationToken.None);
        var selected = SelectedValues.TryGetValue(column, out var sel) ? sel : null;
        var isFiltered = column.IsFiltered;

        var items = new List<TableViewFilterItem>(values.Count);
        foreach (var value in values)
        {
            // When the column isn't filtered yet, every item ticks on by
            // default (matches how Excel paints a freshly-opened flyout).
            // When already filtered, mirror the user's prior selection so
            // re-opening the flyout shows the current state.
            var isSelected = !isFiltered || (selected?.Contains(value) ?? false);
            items.Add(new TableViewFilterItem(isSelected, value, count: 0));
        }
        return items;
    }

    /// <inheritdoc/>
    public virtual void ApplyFilter(TableViewColumn column)
    {
        // No CollectionView FilterDescription is added here — the SQL
        // store is what actually filters; the column header just needs
        // IsFiltered=true to paint its funnel indicator.
        column.IsFiltered = true;
        _tableView.DeselectAll();
        _onFilterChanged();
    }

    /// <inheritdoc/>
    public virtual void ClearFilter(TableViewColumn? column)
    {
        if (column is null)
        {
            // Clear-everything: passed when the user hits the global
            // "clear filters" button or a fresh ItemsSource is set.
            SelectedValues.Clear();
            foreach (var col in _tableView.Columns)
            {
                if (col is not null) col.IsFiltered = false;
            }
        }
        else
        {
            SelectedValues.Remove(column);
            column.IsFiltered = false;
        }
        _onFilterChanged();
    }

    /// <inheritdoc/>
    public virtual bool Filter(TableViewColumn column, object? item)
    {
        // The SQL store is authoritative — every row that reaches the
        // grid has already passed the WHERE clause. Returning true
        // means "don't filter this out client-side", which is what we
        // want: there's no client-side filter pass.
        return true;
    }
}
