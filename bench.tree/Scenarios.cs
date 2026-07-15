using System.Diagnostics;
using WinUI.TableView;

namespace TreeBench;

/// <summary>
/// One scenario per user interaction with the library tree grid. Each returns
/// a result block; Program prints them as a report. Every scenario ends with
/// the deep invariant check — a scenario can pass its timings and still fail
/// the run if it corrupted the model.
/// </summary>
public static class Scenarios
{
    public sealed record Result(string Name, List<(string Metric, string Value)> Metrics, List<string> Problems)
    {
        public bool Failed => Problems.Count > 0;
    }

    public sealed class Rig
    {
        public required UiPump Pump { get; init; }
        public required SimBackend Backend { get; init; }
        public required VirtualTreeModel Model { get; init; }
        public required GridHost Host { get; init; }
        public required List<BenchNode> Roots { get; init; }
        public required SkeletonFactory.Shape Shape { get; init; }
        public required int PageSize { get; init; }
        public required int MaxPages { get; init; }
        public long TotalTracks { get; init; }
    }

    /// <summary>Fresh model + host + backend on the current (pump) thread.</summary>
    public static Rig Build(SkeletonFactory.Shape shape, UiPump pump, int latencyMs = 15,
        bool serialized = true, int pageSize = 200, int maxPages = 64)
    {
        var backend = new SimBackend(serialized) { LatencyMs = latencyMs };
        var roots = SkeletonFactory.Build(shape, out var tracks);

        var placeholders = new Dictionary<object, BenchNode>();
        var rootPlaceholder = new BenchNode { Name = "…", IsPlaceholder = true };

        var model = new VirtualTreeModel(
            childrenOf: n => ((BenchNode)n).Children,
            leafCountOf: n => n is BenchNode b ? b.TrackCount : 0,
            fetchLeaves: backend.FetchLeaves,
            placeholderOf: g =>
            {
                if (g is null) return rootPlaceholder;
                if (!placeholders.TryGetValue(g, out var p))
                    placeholders[g] = p = new BenchNode
                    {
                        Name = "…", IsPlaceholder = true, Depth = ((BenchNode)g).Depth + 1
                    };
                return p;
            },
            pageSize: pageSize,
            maxPagesCached: maxPages);

        var host = new GridHost(model);
        host.SetRoots(roots);
        return new Rig
        {
            Pump = pump, Backend = backend, Model = model, Host = host,
            Roots = roots, Shape = shape, PageSize = pageSize, MaxPages = maxPages,
            TotalTracks = tracks,
        };
    }

    private static Result Finish(Rig rig, string name, List<(string, string)> metrics, bool expectNoInFlight = true)
    {
        rig.Pump.CurrentLabel = $"{name}/quiesce";
        var idle = rig.Pump.PumpUntilIdle();
        var problems = rig.Host.CheckInvariants(rig.MaxPages, expectNoInFlight);
        if (!idle) problems.Add("pump never went idle (work still arriving after 120s)");
        if (rig.Host.ResidencyMs.Count > 0)
        {
            metrics.Add(("visible-empty p50 ms", $"{GridHost.Percentile(rig.Host.ResidencyMs, 50):F0}"));
            metrics.Add(("visible-empty p95 ms", $"{GridHost.Percentile(rig.Host.ResidencyMs, 95):F0}"));
            metrics.Add(("visible-empty max ms", $"{rig.Host.ResidencyMs.Max():F0}"));
        }
        metrics.Add(("abandoned-empty rows", rig.Host.AbandonedEmpty.ToString()));
        metrics.Add(("events R/I/D/repl", $"{rig.Host.Resets}/{rig.Host.Inserts}/{rig.Host.Removes}/{rig.Host.Replaces}"));
        metrics.Add(("fetches ok/cxl/fail", $"{rig.Backend.Completed}/{rig.Backend.Cancelled}/{rig.Backend.Failed}"));
        metrics.Add(("worst UI block ms", $"{rig.Pump.MaxDispatchMs:F1} ({rig.Pump.MaxDispatchLabel})"));
        return new Result(name, metrics, problems);
    }

    // ── 1. Open the library ─────────────────────────────────────────

    public static Result ColdLoad(Rig rig)
    {
        var m = new List<(string, string)>();
        rig.Pump.CurrentLabel = "cold-load/SetRoots";

        var sw = Stopwatch.StartNew();
        rig.Pump.Ui(() => rig.Model.SetRoots(rig.Roots));
        m.Add(("SetRoots ms", $"{sw.Elapsed.TotalMilliseconds:F1}"));
        m.Add(("rows total", rig.Model.Count.ToString("N0")));

        sw.Restart();
        rig.Pump.Ui(() => rig.Host.ScrollTo(0));
        m.Add(("first realize ms", $"{sw.Elapsed.TotalMilliseconds:F1}"));

        // Wait for the first viewport to be fully real.
        sw.Restart();
        while (rig.Host.VisibleEmptyNow() > 0 && sw.ElapsedMilliseconds < 30_000)
            rig.Pump.PumpFor(5);
        m.Add(("viewport full after ms", $"{sw.Elapsed.TotalMilliseconds:F0}"));

        return Finish(rig, "cold-load", m);
    }

    // ── 2. Mouse-wheel sweep ────────────────────────────────────────

    public static Result WheelSweep(Rig rig, int viewports = 120, int msPerStep = 30)
    {
        rig.Pump.Ui(() => { rig.Model.SetRoots(rig.Roots); rig.Host.ScrollTo(0); });
        rig.Pump.PumpUntilIdle();
        rig.Host.ResetStats(); rig.Pump.ResetStats(); rig.Backend.ResetStats();

        var m = new List<(string, string)>();
        var step = rig.Host.ViewportRows / 2;              // half a viewport per wheel notch
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < viewports * 2; i++)
        {
            rig.Pump.CurrentLabel = $"wheel-sweep/step{i}";
            rig.Pump.Ui(() => rig.Host.ScrollTo(rig.Host.FirstVisible + step));
            rig.Pump.PumpFor(msPerStep);                   // frame budget between notches
        }
        m.Add(("sweep wall ms", $"{sw.Elapsed.TotalMilliseconds:F0}"));
        m.Add(("rows swept", (viewports * 2 * step).ToString("N0")));
        return Finish(rig, "wheel-sweep", m);
    }

    // ── 3. Scrollbar thumb drag (far jumps) ─────────────────────────

    public static Result ThumbJumps(Rig rig, int jumps = 30)
    {
        rig.Pump.Ui(() => { rig.Model.SetRoots(rig.Roots); rig.Host.ScrollTo(0); });
        rig.Pump.PumpUntilIdle();
        rig.Host.ResetStats(); rig.Pump.ResetStats(); rig.Backend.ResetStats();

        var m = new List<(string, string)>();
        var rnd = new Random(42);
        var landings = new List<double>();

        for (var i = 0; i < jumps; i++)
        {
            var target = rnd.Next(0, Math.Max(1, rig.Model.Count - rig.Host.ViewportRows));
            rig.Pump.CurrentLabel = $"thumb-jump/{i}→{target}";

            // A thumb drag passes THROUGH rows on the way — the panel realizes
            // several intermediate viewports, each kicking off page fetches the
            // user will never wait for.
            var from = rig.Host.FirstVisible;
            for (var k = 1; k <= 4; k++)
            {
                var mid = from + (target - from) * k / 5;
                rig.Pump.Ui(() => rig.Host.ScrollTo(mid));
                rig.Pump.PumpFor(8);
            }

            rig.Pump.Ui(() => rig.Host.ScrollTo(target));
            var sw = Stopwatch.StartNew();
            while (rig.Host.VisibleEmptyNow() > 0 && sw.ElapsedMilliseconds < 30_000)
                rig.Pump.PumpFor(5);
            landings.Add(sw.Elapsed.TotalMilliseconds);
        }

        m.Add(("landing fill p50 ms", $"{GridHost.Percentile(landings, 50):F0}"));
        m.Add(("landing fill p95 ms", $"{GridHost.Percentile(landings, 95):F0}"));
        m.Add(("landing fill max ms", $"{landings.Max():F0}"));
        m.Add(("fetches started", rig.Backend.Started.ToString()));
        return Finish(rig, "thumb-jumps", m);
    }

    // ── 4. Expand / collapse ────────────────────────────────────────

    public static Result ExpandCollapse(Rig rig)
    {
        rig.Pump.Ui(() => { rig.Model.SetRoots(rig.Roots); rig.Host.ScrollTo(0); });
        rig.Pump.PumpUntilIdle();
        rig.Host.ResetStats(); rig.Pump.ResetStats(); rig.Backend.ResetStats();

        var m = new List<(string, string)>();
        var artist = rig.Roots[rig.Roots.Count / 2];

        double Toggle(BenchNode node, bool expand, string label)
        {
            rig.Pump.CurrentLabel = $"expand-collapse/{label}";
            var sw = Stopwatch.StartNew();
            rig.Pump.Ui(() => node.IsExpanded = expand);
            var ms = sw.Elapsed.TotalMilliseconds;
            rig.Host.RebuildReference();
            rig.Pump.Ui(() => rig.Host.Realize());
            return ms;
        }

        m.Add(("collapse artist ms", $"{Toggle(artist, false, "collapse-artist"):F2}"));
        m.Add(("expand artist ms", $"{Toggle(artist, true, "expand-artist"):F2}"));

        // Collapse-all → expand-all storm (the toolbar buttons).
        var sw2 = Stopwatch.StartNew();
        rig.Pump.CurrentLabel = "expand-collapse/collapse-all";
        rig.Pump.Ui(() => { foreach (var r in rig.Roots) r.IsExpanded = false; });
        m.Add(("collapse-all ms", $"{sw2.Elapsed.TotalMilliseconds:F0}"));
        rig.Host.RebuildReference();

        sw2.Restart();
        rig.Pump.CurrentLabel = "expand-collapse/expand-all";
        rig.Pump.Ui(() => { foreach (var r in rig.Roots) r.IsExpanded = true; });
        m.Add(("expand-all ms", $"{sw2.Elapsed.TotalMilliseconds:F0}"));
        rig.Host.RebuildReference();

        // Random toggle storm, viewport following along.
        var rnd = new Random(7);
        sw2.Restart();
        for (var i = 0; i < 200; i++)
        {
            var a = rig.Roots[rnd.Next(rig.Roots.Count)];
            var node = rnd.Next(2) == 0 || a.Children.Count == 0 ? a : a.Children[rnd.Next(a.Children.Count)];
            rig.Pump.CurrentLabel = $"expand-collapse/storm{i}";
            rig.Pump.Ui(() => node.IsExpanded = !node.IsExpanded);
            if (i % 20 == 0) rig.Pump.PumpFor(2);
        }
        m.Add(("200-toggle storm ms", $"{sw2.Elapsed.TotalMilliseconds:F0}"));
        rig.Host.RebuildReference();
        rig.Pump.Ui(() => rig.Host.Realize());

        return Finish(rig, "expand-collapse", m);
    }

    // ── 5. Search-as-you-type (skeleton swap per keystroke) ─────────

    public static Result SearchStorm(Rig rig)
    {
        rig.Pump.Ui(() => { rig.Model.SetRoots(rig.Roots); rig.Host.ScrollTo(0); });
        rig.Pump.PumpFor(30);   // fetches in flight when the first keystroke lands

        rig.Host.ResetStats(); rig.Pump.ResetStats();

        var m = new List<(string, string)>();
        var swapTimes = new List<double>();

        // 8 keystrokes, each narrowing the skeleton (like SQL pushdown does).
        for (var k = 1; k <= 8; k++)
        {
            var keep = Math.Max(1, rig.Roots.Count >> k);
            var narrowed = rig.Roots.Take(keep).ToList();
            rig.Pump.CurrentLabel = $"search-storm/key{k}";
            var sw = Stopwatch.StartNew();
            rig.Pump.Ui(() =>
            {
                rig.Host.SetRoots(narrowed);
                rig.Model.SetRoots(narrowed);
                rig.Host.ScrollTo(0);
            });
            swapTimes.Add(sw.Elapsed.TotalMilliseconds);
            rig.Pump.PumpFor(80);  // typing cadence
        }

        // Clear the search box: back to the full library.
        rig.Pump.CurrentLabel = "search-storm/clear";
        var swc = Stopwatch.StartNew();
        rig.Pump.Ui(() =>
        {
            rig.Host.SetRoots(rig.Roots);
            rig.Model.SetRoots(rig.Roots);
            rig.Host.ScrollTo(0);
        });
        swapTimes.Add(swc.Elapsed.TotalMilliseconds);

        m.Add(("skeleton swap max ms", $"{swapTimes.Max():F1}"));
        m.Add(("skeleton swap avg ms", $"{swapTimes.Average():F1}"));
        return Finish(rig, "search-storm", m);
    }

    // ── 6. Collapse while a fetch is in flight ──────────────────────

    public static Result CollapseMidFetch(Rig rig)
    {
        rig.Pump.Ui(() => { rig.Model.SetRoots(rig.Roots); rig.Host.ScrollTo(0); });
        rig.Pump.PumpUntilIdle();
        rig.Host.ResetStats(); rig.Pump.ResetStats(); rig.Backend.ResetStats();

        var m = new List<(string, string)>();
        var artist = rig.Roots[0];

        for (var round = 0; round < 20; round++)
        {
            rig.Pump.CurrentLabel = $"collapse-midfetch/{round}";
            rig.Pump.Ui(() =>
            {
                artist.IsExpanded = true;
                rig.Host.RebuildReference();
                rig.Host.ScrollTo(1);          // look at the first album's tracks → fetch starts
            });
            rig.Pump.PumpFor(rig.Backend.LatencyMs / 3);   // fetch still in flight…
            rig.Pump.Ui(() =>
            {
                artist.IsExpanded = false;      // …and the user collapses the artist
                rig.Host.RebuildReference();
                rig.Host.ScrollTo(0);
            });
            rig.Pump.PumpFor(rig.Backend.LatencyMs);
        }

        rig.Pump.Ui(() => { artist.IsExpanded = true; rig.Host.RebuildReference(); rig.Host.ScrollTo(1); });
        var sw = Stopwatch.StartNew();
        while (rig.Host.VisibleEmptyNow() > 0 && sw.ElapsedMilliseconds < 30_000)
            rig.Pump.PumpFor(5);
        m.Add(("re-expand fill ms", $"{sw.Elapsed.TotalMilliseconds:F0}"));

        return Finish(rig, "collapse-midfetch", m);
    }

    // ── 7. Backend faults (DB locked / transient error) ─────────────

    public static Result FaultInjection(Rig rig)
    {
        // Every 3rd query fails.
        rig.Backend.FailWhen = n => n % 3 == 0;

        rig.Pump.Ui(() => { rig.Model.SetRoots(rig.Roots); rig.Host.ScrollTo(0); });
        rig.Pump.PumpUntilIdle();

        var m = new List<(string, string)>();

        // The user keeps looking at the same viewport. Does it EVER fill?
        var sw = Stopwatch.StartNew();
        while (rig.Host.VisibleEmptyNow() > 0 && sw.ElapsedMilliseconds < 5_000)
        {
            rig.Pump.Ui(() => rig.Host.Realize());   // panel re-preps containers periodically
            rig.Pump.PumpFor(50);
        }
        var stuck = rig.Host.VisibleEmptyNow();
        m.Add(("still-empty rows after 5s of retries", stuck.ToString()));
        m.Add(("failed fetches", rig.Backend.Failed.ToString()));

        rig.Backend.FailWhen = null;
        var problems = new List<string>();
        if (stuck > 0)
            problems.Add($"{stuck} visible rows stuck as placeholders FOREVER after one transient backend failure " +
                          "(failed page never leaves _pagesInFlight, so it is never refetched)");

        var result = Finish(rig, "fault-injection", m, expectNoInFlight: false);
        result.Problems.AddRange(problems);
        return result;
    }

    // ── 8. LRU thrash (long listening session) ──────────────────────

    public static Result LruThrash(Rig rig)
    {
        rig.Pump.Ui(() => { rig.Model.SetRoots(rig.Roots); rig.Host.ScrollTo(0); });
        rig.Pump.PumpUntilIdle();
        rig.Host.ResetStats(); rig.Pump.ResetStats(); rig.Backend.ResetStats();

        var m = new List<(string, string)>();
        var rnd = new Random(11);

        // Visit far more pages than the cache holds, twice, with re-visits.
        var mem0 = GC.GetTotalMemory(forceFullCollection: true);
        for (var i = 0; i < rig.MaxPages * 4; i++)
        {
            var target = rnd.Next(0, Math.Max(1, rig.Model.Count - rig.Host.ViewportRows));
            rig.Pump.CurrentLabel = $"lru-thrash/{i}";
            rig.Pump.Ui(() => rig.Host.ScrollTo(target));
            rig.Pump.PumpUntilIdle(idleMs: 30);
        }
        var mem1 = GC.GetTotalMemory(forceFullCollection: true);

        m.Add(("pages fetched", rig.Backend.Completed.ToString()));
        m.Add(("cached leaves now", rig.Model.CachedLeaves().Count().ToString("N0")));
        m.Add(("managed heap delta MB", $"{(mem1 - mem0) / 1024.0 / 1024.0:F1}"));
        return Finish(rig, "lru-thrash", m);
    }

    // ── 9. Selection hot paths ──────────────────────────────────────

    public static Result SelectionOps(Rig rig)
    {
        rig.Pump.Ui(() => { rig.Model.SetRoots(rig.Roots); rig.Host.ScrollTo(rig.Model.Count / 2); });
        rig.Pump.PumpUntilIdle();
        rig.Host.ResetStats(); rig.Pump.ResetStats();

        var m = new List<(string, string)>();

        // Click / ctrl-click / marching drag-select all funnel into IndexOf +
        // GetAt over the anchor row. IsItemSelected checks run per realized row.
        var visible = Enumerable.Range(rig.Host.FirstVisible, rig.Host.ViewportRows)
            .Select(i => rig.Model.GetAt(i)).Where(r => r is not null).ToList();
        rig.Pump.PumpUntilIdle();

        var sw = Stopwatch.StartNew();
        var found = 0;
        for (var rep = 0; rep < 200; rep++)
            foreach (var row in visible)
                if (rig.Model.IndexOf(row) >= 0) found++;
        m.Add(($"IndexOf ×{200 * visible.Count} (cached leaves) ms", $"{sw.Elapsed.TotalMilliseconds:F1}"));

        sw.Restart();
        for (var rep = 0; rep < 200; rep++)
            rig.Model.IndexOf(rig.Roots[^1]);
        m.Add(("IndexOf ×200 (last group) ms", $"{sw.Elapsed.TotalMilliseconds:F1}"));

        // Shift-click a 10k range: the grid walks GetAt over the range on some paths.
        sw.Restart();
        var start = rig.Host.FirstVisible;
        for (var i = start; i < Math.Min(start + 10_000, rig.Model.Count); i++)
            rig.Model.PeekAt(i);
        m.Add(("PeekAt ×10k range ms", $"{sw.Elapsed.TotalMilliseconds:F1}"));

        return Finish(rig, "selection-ops", m);
    }

    // ── 10. Now-playing glyph churn ─────────────────────────────────

    public static Result LeafUpdateStorm(Rig rig)
    {
        rig.Pump.Ui(() => { rig.Model.SetRoots(rig.Roots); rig.Host.ScrollTo(0); });
        rig.Pump.PumpUntilIdle();
        rig.Host.ResetStats(); rig.Pump.ResetStats();

        var m = new List<(string, string)>();
        var cached = rig.Model.CachedLeaves().OfType<BenchNode>().ToList();

        var sw = Stopwatch.StartNew();
        rig.Pump.CurrentLabel = "leaf-update-storm";
        rig.Pump.Ui(() =>
        {
            for (var rep = 0; rep < 20; rep++)
                foreach (var leaf in cached)
                    leaf.Touch();
        });
        m.Add(($"relay {cached.Count * 20:N0} changes ms", $"{sw.Elapsed.TotalMilliseconds:F1}"));
        m.Add(("relayed", rig.Host.LeafChanges.ToString("N0")));
        return Finish(rig, "leaf-update-storm", m);
    }
}
