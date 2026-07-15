using System.Collections.Specialized;
using System.Diagnostics;
using WinUI.TableView;

namespace TreeBench;

/// <summary>
/// The OTHER tree consumer: the in-memory <see cref="TreeGridFlattener{T}"/>
/// (Grooves' review/split dialogs — thousands of rows, all resident). Measures
/// the flat-list surgery a toggle performs, which is where an O(n·k)
/// RemoveRange/InsertRange would bite.
/// </summary>
public static class FlattenerScenarios
{
    public static Scenarios.Result Run(int folders = 500, int childrenPerFolder = 100)
    {
        var m = new List<(string, string)>();
        var problems = new List<string>();

        var roots = new List<BenchNode>(folders);
        for (var f = 0; f < folders; f++)
        {
            var folder = new BenchNode { Name = $"folder{f:D4}", Depth = 0 };
            for (var c = 0; c < childrenPerFolder; c++)
                folder.Children.Add(new BenchNode { Name = $"folder{f:D4}/file{c:D4}", Depth = 1 });
            roots.Add(folder);
        }
        var total = folders * (1 + childrenPerFolder);

        var flattener = new TreeGridFlattener<BenchNode>(n => n.Children);
        var notifications = 0;
        flattener.Flat.CollectionChanged += (_, e) => notifications++;

        var sw = Stopwatch.StartNew();
        flattener.SetRoots(roots);
        m.Add(($"SetRoots {total:N0} rows ms", $"{sw.Elapsed.TotalMilliseconds:F1}"));
        if (notifications != 1) problems.Add($"SetRoots raised {notifications} notifications, expected 1");
        if (flattener.Flat.Count != total) problems.Add($"flat count {flattener.Flat.Count} != {total}");

        // Collapse a folder near the FRONT of a big flat list — worst case for
        // shift-per-RemoveAt costs.
        notifications = 0;
        sw.Restart();
        roots[1].IsExpanded = false;
        m.Add(("collapse front folder ms", $"{sw.Elapsed.TotalMilliseconds:F1}"));
        if (notifications != 1) problems.Add($"collapse raised {notifications} notifications, expected 1");

        notifications = 0;
        sw.Restart();
        roots[1].IsExpanded = true;
        m.Add(("expand front folder ms", $"{sw.Elapsed.TotalMilliseconds:F1}"));
        if (notifications != 1) problems.Add($"expand raised {notifications} notifications, expected 1");

        // Collapse-all storm (review dialog "collapse everything" button).
        sw.Restart();
        foreach (var r in roots) r.IsExpanded = false;
        m.Add(($"collapse-all {folders} folders ms", $"{sw.Elapsed.TotalMilliseconds:F0}"));

        sw.Restart();
        foreach (var r in roots) r.IsExpanded = true;
        m.Add(($"expand-all {folders} folders ms", $"{sw.Elapsed.TotalMilliseconds:F0}"));

        if (flattener.Flat.Count != total)
            problems.Add($"after storm: flat count {flattener.Flat.Count} != {total}");

        // Reference check: order must be exactly folder, its files, next folder…
        var idx = 0;
        foreach (var f in roots)
        {
            if (!ReferenceEquals(flattener.Flat[idx++], f)) { problems.Add($"order broken at {idx - 1}"); break; }
            foreach (var c in f.Children)
                if (!ReferenceEquals(flattener.Flat[idx++], c)) { problems.Add($"order broken at {idx - 1}"); break; }
        }

        return new Scenarios.Result($"flattener {folders}×{childrenPerFolder}", m, problems);
    }
}
