using System.ComponentModel;
using WinUI.TableView;

namespace TreeBench;

/// <summary>
/// Bench counterpart of Grooves' ReviewNode: one object per folder (group) or
/// track (leaf). Groups carry Children + a virtual track count; leaves carry
/// nothing but a name. IsExpanded raises INPC so the model reacts, same as the
/// real app.
/// </summary>
public sealed class BenchNode : ITreeGridRow, INotifyPropertyChanged
{
    public required string Name { get; init; }
    public int Depth { get; init; }
    public List<BenchNode> Children { get; } = new();
    public int TrackCount { get; set; }
    public bool IsPlaceholder { get; init; }

    public bool HasChildren => Children.Count > 0 || TrackCount > 0;

    private bool _isExpanded = true;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    /// <summary>Fires a non-structural change (e.g. the play-glyph flips) — exercises the leaf relay.</summary>
    public void Touch() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Glyph"));

    public event PropertyChangedEventHandler? PropertyChanged;

    public override string ToString() => Name;
}
