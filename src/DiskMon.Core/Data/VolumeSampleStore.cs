using Dapper;

namespace DiskMon.Core.Data;

/// <summary>
/// Reads and writes the <c>volume_samples</c> table. The Collector hands it its <see cref="Db"/>; the Tray hands it
/// a <see cref="ReadOnlyDb"/>, on which only the reads work.
/// </summary>
public sealed class VolumeSampleStore(IDbConnectionSource db)
{
    /// <summary>Inserts one tick's samples in a single transaction.</summary>
    public void Insert(IReadOnlyCollection<VolumeSample> samples)
    {
        if (samples.Count == 0)
            return;

        using var connection = db.OpenConnection();
        using var transaction = connection.BeginTransaction();
        connection.Execute(
            "INSERT INTO volume_samples (ts, volume, total_bytes, free_bytes) VALUES (@Ts, @Volume, @TotalBytes, @FreeBytes)",
            samples,
            transaction);
        transaction.Commit();
    }

    /// <summary>The newest sample for <paramref name="volume"/>, or null if there is none.</summary>
    public VolumeSample? Latest(string volume)
    {
        using var connection = db.OpenConnection();
        return connection.QuerySingleOrDefault<VolumeSample>(
            $"{Select} WHERE volume = @volume ORDER BY ts DESC LIMIT 1",
            new { volume });
    }

    /// <summary>The newest sample for <paramref name="volume"/> taken at or before <paramref name="ts"/>, or null.</summary>
    public VolumeSample? AtOrBefore(string volume, long ts)
    {
        using var connection = db.OpenConnection();
        return connection.QuerySingleOrDefault<VolumeSample>(
            $"{Select} WHERE volume = @volume AND ts <= @ts ORDER BY ts DESC LIMIT 1",
            new { volume, ts });
    }

    /// <summary>The oldest sample for <paramref name="volume"/>, or null if there is none.</summary>
    public VolumeSample? Oldest(string volume)
    {
        using var connection = db.OpenConnection();
        return connection.QuerySingleOrDefault<VolumeSample>(
            $"{Select} WHERE volume = @volume ORDER BY ts ASC LIMIT 1",
            new { volume });
    }

    private const string Select =
        "SELECT ts AS Ts, volume AS Volume, total_bytes AS TotalBytes, free_bytes AS FreeBytes FROM volume_samples";
}
