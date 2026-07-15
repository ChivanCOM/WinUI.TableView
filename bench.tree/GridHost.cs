using System.Diagnostics;
using System.Reflection;
using WinUI.TableView;

namespace TreeBench;

/// <summary>
/// Stands in for the TableView: subscribes to the model's change surface the
/// same way VirtualTreeItemsSource translates it, keeps a viewport of realized
/// rows, and measures what a user actually feels:
///
///  • placeholder residency — how long a VISIBLE row shows "…" before the real
///    track lands (the "empty rows for far too long" pain, in numbers);
///  • abandoned-empty rows — rows the user scrolled past that never filled
///    while on screen;
///  • event storms — how many notifications each interaction produced;
///  • correctness — every realized leaf is checked against an independently
///    built reference projection (index math errors surface as identity
///    mismatches, not vibes).
/// </summary>
public sealed class GridHost
{
    private readonly VirtualTreeModel _model;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    // index → timestamp when the viewport first showed a placeholder there
    private readonly Dictionary<int, double> _emptySince = new();

    // Reference projection: (group row | leaf block) runs, rebuilt on demand.
    private readonly record struct RefSegment(BenchNode? Group, bool IsLeafBlock, int Start, int Length);
    private readonly List<RefSegment> _reference = new();
    private IReadOnlyList<BenchNode> _roots = Array.Empty<BenchNode>();

    public int FirstVisible { get; private set; }
    public int ViewportRows { get; set; } = 40;

    // ── Felt metrics ────────────────────────────────────────────────
    public readonly List<double> ResidencyMs = new();
    public int AbandonedEmpty;
    public int Resets, Inserts, Removes, Replaces, LeafChanges;
    public int IdentityErrors;
    public string? FirstIdentityError;

    public GridHost(VirtualTreeModel model)
    {
        _model = model;
        model.ResetRaised += () => { Resets++; OnStructureChanged(); };
        model.RangeInserted += (i, c) => { Inserts += c; OnStructureChanged(); };
        model.RangeRemoved += (i, c) => { Removes += c; OnStructureChanged(); };
        model.ItemReplaced += OnItemReplaced;
        model.LeafPropertyChanged += (_, _) => LeafChanges++;
    }

    public void SetRoots(IReadOnlyList<BenchNode> roots)
    {
        _roots = roots;
        RebuildReference();
    }

    /// <summary>Rebuild the reference projection (call after any expand/collapse).</summary>
    public void RebuildReference()
    {
        _reference.Clear();
        var cursor = 0;
        void Walk(IReadOnlyList<BenchNode> groups)
        {
            foreach (var g in groups)
            {
                _reference.Add(new RefSegment(g, false, cursor, 1));
                cursor++;
                if (g.IsExpanded)
                {
                    Walk(g.Children);
                    if (g.TrackCount > 0)
                    {
                        _reference.Add(new RefSegment(g, true, cursor, g.TrackCount));
                        cursor += g.TrackCount;
                    }
                }
            }
        }
        Walk(_roots);
        ReferenceCount = cursor;
    }

    public int ReferenceCount { get; private set; }

    // ── Interactions ────────────────────────────────────────────────

    /// <summary>Scroll so <paramref name="firstRow"/> is the top visible row, then realize.</summary>
    public void ScrollTo(int firstRow)
    {
        firstRow = Math.Max(0, Math.Min(firstRow, _model.Count - 1));
        // Rows that scrolled out while still empty: the user saw "…" and moved on.
        foreach (var (index, _) in _emptySince.Where(kv => kv.Key < firstRow || kv.Key >= firstRow + ViewportRows).ToList())
        {
            _emptySince.Remove(index);
            AbandonedEmpty++;
        }
        FirstVisible = firstRow;
        Realize();
    }

    /// <summary>Realize every visible row — what the panel does during container prep.</summary>
    public void Realize()
    {
        var end = Math.Min(FirstVisible + ViewportRows, _model.Count);
        for (var i = FirstVisible; i < end; i++)
            RealizeIndex(i);
    }

    private void RealizeIndex(int index)
    {
        var row = _model.GetAt(index);
        if (row is BenchNode { IsPlaceholder: true } || IsForeignPlaceholder(row))
        {
            _emptySince.TryAdd(index, _clock.Elapsed.TotalMilliseconds);
            return;
        }

        if (_emptySince.Remove(index, out var since))
            ResidencyMs.Add(_clock.Elapsed.TotalMilliseconds - since);

        VerifyIdentity(index, row);
    }

    private static bool IsForeignPlaceholder(object? row) =>
        row is BenchNode b && b.Name == "…";

    private void OnItemReplaced(int index, object row, object old)
    {
        Replaces++;
        if (index >= FirstVisible && index < FirstVisible + ViewportRows)
            RealizeIndex(index);
    }

    private void OnStructureChanged()
    {
        // Indices shifted: pending residency timers are meaningless now.
        _emptySince.Clear();
        if (FirstVisible >= _model.Count)
            FirstVisible = Math.Max(0, _model.Count - ViewportRows);
    }

    // ── Correctness ─────────────────────────────────────────────────

    private void VerifyIdentity(int index, object? row)
    {
        var seg = FindReference(index);
        if (seg is null)
        {
            Fail(index, row, "index outside reference projection");
            return;
        }

        if (!seg.Value.IsLeafBlock)
        {
            if (!ReferenceEquals(row, seg.Value.Group))
                Fail(index, row, $"expected group {seg.Value.Group?.Name}");
            return;
        }

        var offset = index - seg.Value.Start;
        var expected = $"{seg.Value.Group?.Name ?? "<root>"}#{offset}";
        if (row is not BenchNode leaf || leaf.Name != expected)
            Fail(index, row, $"expected leaf {expected}");
    }

    private RefSegment? FindReference(int index)
    {
        int lo = 0, hi = _reference.Count - 1;
        if (hi < 0 || index >= ReferenceCount) return null;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (_reference[mid].Start <= index) lo = mid;
            else hi = mid - 1;
        }
        return _reference[lo];
    }

    private void Fail(int index, object? row, string why)
    {
        IdentityErrors++;
        FirstIdentityError ??= $"index {index}: got '{row}', {why}";
    }

    /// <summary>Deep invariants over the model's private state — run at quiescence.</summary>
    public List<string> CheckInvariants(int maxPagesCached, bool expectNoInFlight = true)
    {
        var problems = new List<string>();
        var t = typeof(VirtualTreeModel);
        object F(string name) => t.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_model)!;

        var pages = (System.Collections.IDictionary)F("_pages");
        var lruNodes = (System.Collections.IDictionary)F("_pageLruNodes");
        var inFlight = (System.Collections.IEnumerable)F("_pagesInFlight");
        var segments = (System.Collections.IList)F("_segments");

        if (pages.Count != lruNodes.Count)
            problems.Add($"LRU desync: {pages.Count} pages vs {lruNodes.Count} LRU nodes");
        if (pages.Count > maxPagesCached)
            problems.Add($"cache over budget: {pages.Count} pages > {maxPagesCached}");

        var inFlightCount = inFlight.Cast<object>().Count();
        if (expectNoInFlight && inFlightCount > 0)
            problems.Add($"{inFlightCount} page fetch(es) still marked in-flight at quiescence — those rows are stuck as placeholders forever");

        // Segment geometry: contiguous, starts at 0, total == Count.
        var cursor = 0;
        foreach (var seg in segments)
        {
            var st = seg.GetType();
            var start = (int)st.GetProperty("Start")!.GetValue(seg)!;
            var length = (int)st.GetProperty("Length")!.GetValue(seg)!;
            if (start != cursor) { problems.Add($"segment gap at {cursor} (next starts {start})"); break; }
            if (length <= 0) { problems.Add($"empty segment at {start}"); break; }
            cursor = start + length;
        }
        if (cursor != _model.Count)
            problems.Add($"segments sum to {cursor} but Count is {_model.Count}");

        if (_model.Count != ReferenceCount)
            problems.Add($"model Count {_model.Count} != reference {ReferenceCount}");

        if (IdentityErrors > 0)
            problems.Add($"{IdentityErrors} identity error(s); first: {FirstIdentityError}");

        return problems;
    }

    /// <summary>Rows currently visible but empty (placeholders) right now.</summary>
    public int VisibleEmptyNow()
    {
        var empty = 0;
        var end = Math.Min(FirstVisible + ViewportRows, _model.Count);
        for (var i = FirstVisible; i < end; i++)
        {
            var row = _model.PeekAt(i);
            if (row is BenchNode { IsPlaceholder: true } || IsForeignPlaceholder(row)) empty++;
        }
        return empty;
    }

    public void ResetStats()
    {
        ResidencyMs.Clear();
        AbandonedEmpty = 0;
        Resets = Inserts = Removes = Replaces = LeafChanges = 0;
        IdentityErrors = 0;
        FirstIdentityError = null;
        _emptySince.Clear();
    }

    public static double Percentile(List<double> xs, double p)
    {
        if (xs.Count == 0) return 0;
        var sorted = xs.OrderBy(x => x).ToList();
        var idx = (int)Math.Ceiling(p / 100.0 * sorted.Count) - 1;
        return sorted[Math.Clamp(idx, 0, sorted.Count - 1)];
    }
}
