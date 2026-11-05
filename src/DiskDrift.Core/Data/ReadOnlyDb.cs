using Dapper;
using Microsoft.Data.Sqlite;

namespace DiskDrift.Core.Data;

/// <summary>How <see cref="ReadOnlyDb"/> opened its last connection.</summary>
public enum ReadOnlyOpenMode
{
    /// <summary>A normal read-only WAL reader: the Collector is running, so its <c>-wal</c>/<c>-shm</c> files exist.</summary>
    ReadOnly,

    /// <summary>
    /// SQLite's <c>immutable=1</c> open: no locks, no <c>-shm</c>, the WAL ignored. Used when a read-only open fails
    /// because the Collector is stopped and the Tray may not create the <c>-shm</c> file in the read-only data folder.
    /// </summary>
    Immutable,
}

/// <summary>
/// The Tray's view of the DiskDrift database. It never writes: every connection is opened <c>Mode=ReadOnly</c>, and
/// when that fails it falls back to an immutable open so the last known history still shows (PLAN §5.6).
/// Connections are not pooled, so the Tray holds no file handles between refreshes.
/// </summary>
public sealed class ReadOnlyDb : IDbConnectionSource
{
    private readonly string _readOnly;
    private readonly string _immutable;

    public ReadOnlyDb(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        _readOnly = ConnectionString(Path);
        _immutable = ConnectionString(ImmutableUri(Path));
    }

    public string Path { get; }

    /// <summary>The mode of the most recent successful <see cref="OpenConnection"/>, or null before the first.</summary>
    public ReadOnlyOpenMode? LastMode { get; private set; }

    /// <summary>
    /// Opens a read-only connection, falling back to an immutable one. Throws <see cref="SqliteException"/> when
    /// neither works (for example, the database does not exist yet).
    /// </summary>
    public SqliteConnection OpenConnection()
    {
        try
        {
            var connection = Open(_readOnly);
            LastMode = ReadOnlyOpenMode.ReadOnly;
            return connection;
        }
        catch (SqliteException)
        {
            var connection = Open(_immutable);
            LastMode = ReadOnlyOpenMode.Immutable;
            return connection;
        }
    }

    /// <summary>A <c>file:</c> URI for <paramref name="path"/> with <c>immutable=1</c>, which Microsoft.Data.Sqlite passes to SQLite as a URI filename.</summary>
    internal static string ImmutableUri(string path) => new Uri(path).AbsoluteUri + "?immutable=1";

    private static string ConnectionString(string dataSource) => new SqliteConnectionStringBuilder
    {
        DataSource = dataSource,
        Mode = SqliteOpenMode.ReadOnly,
        Pooling = false,
        DefaultTimeout = Db.BusyTimeoutMs / 1000,
    }.ToString();

    private static SqliteConnection Open(string connectionString)
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            // Opening is lazy: a WAL reader that cannot create or map the -shm file only fails on its first read.
            connection.Execute($"PRAGMA busy_timeout = {Db.BusyTimeoutMs};");
            connection.ExecuteScalar<long>("SELECT count(*) FROM sqlite_schema");
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}
