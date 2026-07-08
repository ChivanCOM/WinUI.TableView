namespace WinUI.TableView;

/// <summary>
/// Contract for items displayed hierarchically in a <see cref="TableView"/> via
/// <see cref="TableViewTreeColumn"/> + <see cref="TreeGridFlattener{T}"/>.
///
/// The TableView itself stays flat: the flattener projects the tree into the
/// bound collection (depth-first, respecting <see cref="IsExpanded"/>), and the
/// tree column renders the indent + expander chevron.
/// </summary>
public interface ITreeGridRow
{
    /// <summary>Zero-based depth in the tree (roots are 0). Drives the indent.</summary>
    int Depth { get; }

    /// <summary>True when the row has children (shows the expander chevron).</summary>
    bool HasChildren { get; }

    /// <summary>
    /// Expand state. Setting it must raise INotifyPropertyChanged so the
    /// flattener (and the chevron glyph) react.
    /// </summary>
    bool IsExpanded { get; set; }
}
