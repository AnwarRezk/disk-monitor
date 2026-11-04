using Dapper;

namespace DiskMon.Core.Data;

/// <summary>Reads and writes the <c>volume_samples</c> table.</summary>
public sealed class VolumeSampleStore(Db db)
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
            """
            SELECT ts AS Ts, volume AS Volume, total_bytes AS TotalBytes, free_bytes AS FreeBytes
            FROM volume_samples WHERE volume = @volume ORDER BY ts DESC LIMIT 1
            """,
            new { volume });
    }
}
