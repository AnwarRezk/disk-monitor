namespace DiskMon.Core.Analysis;

/// <summary>
/// Identifies one process instance. The start time (UTC Unix ms) tells a reused PID apart from the process
/// that held it before, so a new process never inherits another one's counters.
/// </summary>
public readonly record struct ProcessKey(int Pid, long StartTimeMs);

/// <summary>A process's cumulative I/O counters at one moment, as read from the OS.</summary>
public sealed record ProcessIoSnapshot(ProcessKey Key, string Name, string? ExePath, ulong ReadBytes, ulong WriteBytes);

/// <summary>Bytes a process wrote and read during one interval: one Top Writer row.</summary>
public sealed record ProcessIoDelta(string Name, string? ExePath, long WriteBytes, long ReadBytes);

/// <summary>
/// Turns successive counter snapshots into the interval's Top Writers. Pure: the caller supplies the snapshots
/// and the time; nothing here touches the OS or the database.
/// </summary>
/// <remarks>
/// A process seen for the first time counts in full only if it started after the previous snapshot (all of its
/// I/O happened inside the interval); otherwise its counters are only a baseline for the next interval. The very
/// first snapshot is a baseline for everything. Counters that went backwards are treated as a new baseline.
/// </remarks>
public sealed class TopWriterTracker
{
    private Dictionary<ProcessKey, (ulong Read, ulong Write)> _previous = [];
    private long? _previousTs;

    /// <summary>
    /// Records <paramref name="snapshots"/> taken at <paramref name="ts"/> (UTC Unix ms) and returns the top
    /// <paramref name="count"/> processes by bytes written since the previous call, keeping only those that wrote
    /// at least <paramref name="minWriteBytes"/> (and always more than zero).
    /// </summary>
    public IReadOnlyList<ProcessIoDelta> Next(long ts, IEnumerable<ProcessIoSnapshot> snapshots, int count, long minWriteBytes)
    {
        var current = new Dictionary<ProcessKey, (ulong Read, ulong Write)>();
        var deltas = new List<ProcessIoDelta>();

        foreach (var s in snapshots)
        {
            if (!current.TryAdd(s.Key, (s.ReadBytes, s.WriteBytes)))
                continue;

            ulong write, read;
            if (_previous.TryGetValue(s.Key, out var prev))
            {
                if (s.WriteBytes < prev.Write || s.ReadBytes < prev.Read)
                    continue;
                write = s.WriteBytes - prev.Write;
                read = s.ReadBytes - prev.Read;
            }
            else if (_previousTs is { } previousTs && s.Key.StartTimeMs >= previousTs)
            {
                write = s.WriteBytes;
                read = s.ReadBytes;
            }
            else
            {
                continue;
            }

            if (write == 0 || write < (ulong)Math.Max(0, minWriteBytes))
                continue;
            deltas.Add(new ProcessIoDelta(s.Name, s.ExePath, Clamp(write), Clamp(read)));
        }

        _previous = current;
        _previousTs = ts;

        return deltas
            .OrderByDescending(d => d.WriteBytes)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(0, count))
            .ToList();
    }

    private static long Clamp(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;
}
