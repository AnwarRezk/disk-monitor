namespace DiskMon.Core.Probes;

/// <summary>
/// Runs at most one operation at a time: a caller arriving while one is in flight gets that same task instead of
/// starting another. Used so that two quick "Probe now" clicks, or a click during the scheduled run, cause one run.
/// </summary>
public sealed class SingleFlight<T>
{
    private readonly Lock _lock = new();
    private Task<T>? _current;

    /// <summary>The run in flight, or null when idle.</summary>
    public Task<T>? Current
    {
        get
        {
            lock (_lock)
                return _current is { IsCompleted: false } ? _current : null;
        }
    }

    /// <summary>Joins the run in flight, or starts <paramref name="operation"/> on the thread pool.</summary>
    public Task<T> RunAsync(Func<Task<T>> operation)
    {
        lock (_lock)
        {
            if (_current is { IsCompleted: false })
                return _current;
            _current = Task.Run(operation);
            return _current;
        }
    }
}
