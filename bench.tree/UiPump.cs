using System.Collections.Concurrent;
using System.Diagnostics;

namespace TreeBench;

/// <summary>
/// A single-threaded message pump standing in for the UI thread. The bench's
/// main thread installs this as its <see cref="SynchronizationContext"/> and
/// then drains it, so every async continuation of <c>VirtualTreeModel</c>
/// (which awaits with ConfigureAwait(true)) lands back on the same thread —
/// the exact threading model the real grid runs under.
///
/// Every drained callback is timed: the longest single callback is the worst
/// "UI freeze" the scenario produced.
/// </summary>
public sealed class UiPump : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback Cb, object? State)> _queue = new();
    private readonly Thread _uiThread;

    public UiPump() => _uiThread = Thread.CurrentThread;

    public int Dispatches { get; private set; }
    public double TotalDispatchMs { get; private set; }
    public double MaxDispatchMs { get; private set; }
    public string? MaxDispatchLabel { get; private set; }

    /// <summary>Label attributed to the currently running scenario step, so the
    /// worst freeze can be traced back to the interaction that caused it.</summary>
    public string CurrentLabel { get; set; } = "";

    public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (Thread.CurrentThread == _uiThread) { Run(d, state); return; }
        throw new NotSupportedException("Cross-thread Send is not part of the model's contract.");
    }

    private void Run(SendOrPostCallback d, object? state)
    {
        var sw = Stopwatch.StartNew();
        d(state);
        sw.Stop();
        Dispatches++;
        TotalDispatchMs += sw.Elapsed.TotalMilliseconds;
        if (sw.Elapsed.TotalMilliseconds > MaxDispatchMs)
        {
            MaxDispatchMs = sw.Elapsed.TotalMilliseconds;
            MaxDispatchLabel = CurrentLabel;
        }
    }

    /// <summary>Runs <paramref name="action"/> on the pump thread, timed like a
    /// dispatched callback (a user interaction handler).</summary>
    public void Ui(Action action) => Run(_ => action(), null);

    /// <summary>Drains until the queue has been idle for <paramref name="idleMs"/>,
    /// or <paramref name="maxMs"/> elapsed. Returns false on timeout with work
    /// still arriving.</summary>
    public bool PumpUntilIdle(int idleMs = 60, int maxMs = 120_000)
    {
        var total = Stopwatch.StartNew();
        while (total.ElapsedMilliseconds < maxMs)
        {
            if (_queue.TryTake(out var item, idleMs))
                Run(item.Cb, item.State);
            else
                return true;
        }
        return false;
    }

    /// <summary>Drains for a fixed wall-clock window (a frame budget), then returns.</summary>
    public void PumpFor(int ms)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var left = ms - (int)sw.ElapsedMilliseconds;
            if (left <= 0) return;
            if (_queue.TryTake(out var item, left))
                Run(item.Cb, item.State);
        }
    }

    public void ResetStats()
    {
        Dispatches = 0;
        TotalDispatchMs = 0;
        MaxDispatchMs = 0;
        MaxDispatchLabel = null;
    }
}
