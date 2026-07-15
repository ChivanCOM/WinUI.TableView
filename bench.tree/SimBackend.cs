using System.Collections.Concurrent;

namespace TreeBench;

/// <summary>
/// Stands in for the SQLite mirror: builds the artist/album skeleton and
/// serves windowed track pages with a configurable per-query latency. By
/// default queries are SERIALIZED (one at a time), like a single SQLite
/// connection — that is exactly the regime under which stale page fetches
/// queue up in front of the page the viewport is waiting for.
/// </summary>
public sealed class SimBackend
{
    private readonly SemaphoreSlim? _connection;

    public SimBackend(bool serialized = true) =>
        _connection = serialized ? new SemaphoreSlim(1, 1) : null;

    /// <summary>Per-query latency, ms. 0 = complete synchronously (in-memory store).</summary>
    public int LatencyMs { get; set; } = 15;

    /// <summary>When set, query #n fails with an exception if FailWhen(n) — DB-locked style faults.</summary>
    public Func<int, bool>? FailWhen { get; set; }

    public int Started;
    public int Completed;
    public int Failed;
    public int Cancelled;

    /// <summary>Track pages abandoned by the viewport before completion (measured by the host).</summary>
    public readonly ConcurrentDictionary<string, int> PerGroupFetches = new();

    public int InFlight => Started - Completed - Failed - Cancelled;

    public async Task<IReadOnlyList<object>> FetchLeaves(
        object? group, int offset, int limit, CancellationToken ct)
    {
        var n = Interlocked.Increment(ref Started);
        var node = (BenchNode?)group;
        var path = node?.Name ?? "<root>";
        PerGroupFetches.AddOrUpdate(path, 1, (_, v) => v + 1);

        try
        {
            if (LatencyMs > 0)
            {
                if (_connection is not null)
                {
                    await _connection.WaitAsync(ct).ConfigureAwait(false);
                    try { await Task.Delay(LatencyMs, ct).ConfigureAwait(false); }
                    finally { _connection.Release(); }
                }
                else
                {
                    await Task.Delay(LatencyMs, ct).ConfigureAwait(false);
                }
            }

            ct.ThrowIfCancellationRequested();

            if (FailWhen?.Invoke(n) == true)
            {
                Interlocked.Increment(ref Failed);
                throw new InvalidOperationException($"simulated store failure on query #{n}");
            }

            var rows = new List<object>(limit);
            var depth = node is null ? 0 : node.Depth + 1;
            for (var i = 0; i < limit; i++)
                rows.Add(new BenchNode { Name = $"{path}#{offset + i}", Depth = depth });

            Interlocked.Increment(ref Completed);
            return rows;
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref Cancelled);
            throw;
        }
    }

    public void ResetStats()
    {
        Started = Completed = Failed = Cancelled = 0;
        PerGroupFetches.Clear();
    }
}

/// <summary>Library shapes. Track counts vary deterministically per album so the
/// segment math sees non-uniform blocks (real collections are lumpy).</summary>
public static class SkeletonFactory
{
    public record Shape(string Name, int Artists, int AlbumsPerArtist, int MeanTracks)
    {
        public int Groups => Artists * (1 + AlbumsPerArtist);
        public long ApproxTracks => (long)Artists * AlbumsPerArtist * MeanTracks;
    }

    public static readonly Shape Small = new("S", 200, 5, 10);        // ~10k tracks, 1.2k groups
    public static readonly Shape Medium = new("M", 1_000, 10, 10);    // ~100k tracks, 11k groups
    public static readonly Shape Large = new("L", 4_000, 15, 16);     // ~960k tracks, 64k groups

    public static Shape Parse(string s) => s.ToUpperInvariant() switch
    {
        "S" => Small,
        "M" => Medium,
        "L" => Large,
        _ => throw new ArgumentException($"unknown scale '{s}' (use S, M or L)"),
    };

    /// <summary>Artist folders (depth 0) each holding album folders (depth 1) that
    /// carry the tracks. Same shape LibraryTreeSource builds from its GROUP BY.</summary>
    public static List<BenchNode> Build(Shape shape, out long totalTracks)
    {
        totalTracks = 0;
        var roots = new List<BenchNode>(shape.Artists);
        for (var a = 0; a < shape.Artists; a++)
        {
            var artist = new BenchNode { Name = $"artist{a:D5}", Depth = 0 };
            for (var b = 0; b < shape.AlbumsPerArtist; b++)
            {
                // Lumpy but deterministic: 50%..150% of the mean.
                var tracks = shape.MeanTracks / 2 + (a * 31 + b * 17) % (shape.MeanTracks + 1);
                var album = new BenchNode
                {
                    Name = $"artist{a:D5}/album{b:D3}",
                    Depth = 1,
                    TrackCount = tracks,
                };
                totalTracks += tracks;
                artist.Children.Add(album);
            }
            roots.Add(artist);
        }
        return roots;
    }
}
