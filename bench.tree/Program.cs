using TreeBench;

// TreeBench — interaction + benchmark harness for the WinUI.TableView tree kit.
//
//   dotnet run -c Release -- all --scale L
//   dotnet run -c Release -- wheel-sweep thumb-jumps --scale L --latency 15
//   dotnet run -c Release -- fault-injection --scale S
//
// Options:
//   --scale S|M|L        library size (S≈10k, M≈100k, L≈1M tracks). Default M.
//   --latency <ms>       per-query backend latency. Default 15.
//   --parallel-db        backend answers queries concurrently (default: serialized,
//                        like a single SQLite connection).
//   --pagesize <n>       leaf page size. Default 200 (Grooves' setting).
//   --maxpages <n>       LRU page budget. Default 64 (Grooves' setting).

var names = new List<string>();
var scale = "M";
var latency = 15;
var serialized = true;
var pageSize = 200;
var maxPages = 64;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--scale": scale = args[++i]; break;
        case "--latency": latency = int.Parse(args[++i]); break;
        case "--parallel-db": serialized = false; break;
        case "--pagesize": pageSize = int.Parse(args[++i]); break;
        case "--maxpages": maxPages = int.Parse(args[++i]); break;
        default: names.Add(args[i]); break;
    }
}

var all = new (string Name, Func<Scenarios.Rig, Scenarios.Result> Run)[]
{
    ("cold-load", Scenarios.ColdLoad),
    ("wheel-sweep", r => Scenarios.WheelSweep(r)),
    ("thumb-jumps", r => Scenarios.ThumbJumps(r)),
    ("expand-collapse", Scenarios.ExpandCollapse),
    ("search-storm", Scenarios.SearchStorm),
    ("collapse-midfetch", Scenarios.CollapseMidFetch),
    ("fault-injection", Scenarios.FaultInjection),
    ("lru-thrash", Scenarios.LruThrash),
    ("selection-ops", Scenarios.SelectionOps),
    ("leaf-update-storm", Scenarios.LeafUpdateStorm),
};

if (names.Count == 0 || names.Contains("all"))
    names = all.Select(s => s.Name).Append("flattener").ToList();

var shape = SkeletonFactory.Parse(scale);
Console.WriteLine($"scale {shape.Name}: {shape.Artists:N0} artists × {shape.AlbumsPerArtist} albums, " +
                  $"~{shape.ApproxTracks:N0} tracks, {shape.Groups:N0} group rows");
Console.WriteLine($"backend: {latency}ms/query, {(serialized ? "serialized (SQLite-like)" : "parallel")}; " +
                  $"pages {pageSize} rows × {maxPages} cached");
Console.WriteLine();

var pump = new UiPump();
SynchronizationContext.SetSynchronizationContext(pump);

var failed = 0;
foreach (var name in names)
{
    Scenarios.Result result;
    if (name == "flattener")
    {
        result = FlattenerScenarios.Run();
    }
    else
    {
        var scenario = all.FirstOrDefault(s => s.Name == name);
        if (scenario.Run is null)
        {
            Console.Error.WriteLine($"unknown scenario '{name}'");
            failed++;
            continue;
        }
        var rig = Scenarios.Build(shape, pump, latency, serialized, pageSize, maxPages);
        pump.ResetStats();
        result = scenario.Run(rig);
    }

    Console.WriteLine($"── {result.Name} {(result.Failed ? "✗ FAILED" : "✓")}");
    foreach (var (metric, value) in result.Metrics)
        Console.WriteLine($"   {metric,-38} {value}");
    foreach (var p in result.Problems)
        Console.WriteLine($"   !! {p}");
    Console.WriteLine();
    if (result.Failed) failed++;
}

return failed;
