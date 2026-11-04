using Dapper;
using Microsoft.Data.Sqlite;

namespace DiskMon.Core.Data;

/// <summary>Rows removed by one <see cref="Retention.Prune"/> run.</summary>
public sealed record RetentionResult(int VolumeSamples, int ProcessIoSamples, int ProbeSamples, int Processes)
{
    public int Total => VolumeSamples + ProcessIoSamples + ProbeSamples + Processes;
}

/// <summary>
/// Deletes Volume, process I/O and Probe Samples older than a cutoff, then process rows no sample references.
/// Swings and Swing items are never touched: they are kept forever.
/// </summary>
public static class Retention
{
    public static RetentionResult Prune(SqliteConnection connection, long cutoffTs)
    {
        using var tx = connection.BeginTransaction();
        var p = new { cutoffTs };
        var result = new RetentionResult(
            VolumeSamples: connection.Execute("DELETE FROM volume_samples WHERE ts < @cutoffTs", p, tx),
            ProcessIoSamples: connection.Execute("DELETE FROM process_io_samples WHERE ts < @cutoffTs", p, tx),
            ProbeSamples: connection.Execute("DELETE FROM probe_samples WHERE ts < @cutoffTs", p, tx),
            Processes: connection.Execute(
                "DELETE FROM processes WHERE NOT EXISTS (SELECT 1 FROM process_io_samples s WHERE s.process_id = processes.id)",
                transaction: tx));
        tx.Commit();
        return result;
    }
}
