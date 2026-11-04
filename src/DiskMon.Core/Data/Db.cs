using Dapper;
using Microsoft.Data.Sqlite;

namespace DiskMon.Core.Data;

/// <summary>
/// The DiskMon SQLite database. The Collector is its only writer: <see cref="OpenOrCreate"/> creates the
/// file if needed, switches it to WAL and applies the embedded migrations.
/// </summary>
public sealed class Db : IDbConnectionSource
{
    public const string FileName = "diskmon.db";
    public const int BusyTimeoutMs = 5000;

    private readonly string _connectionString;

    private Db(string path)
    {
        Path = path;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = BusyTimeoutMs / 1000,
        }.ToString();
    }

    public string Path { get; }

    /// <summary>Migrations applied by the call that produced this instance (empty when already up to date).</summary>
    public IReadOnlyList<Migration> AppliedMigrations { get; private set; } = [];

    public static Db OpenOrCreate(string path)
    {
        var db = new Db(System.IO.Path.GetFullPath(path));
        using var connection = db.OpenConnection();

        // journal_mode is persistent in the file, but cannot be changed inside a transaction,
        // so it is set here rather than in a migration.
        var mode = connection.ExecuteScalar<string>("PRAGMA journal_mode = WAL;");
        if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Could not switch '{db.Path}' to WAL (journal_mode is '{mode}').");

        db.AppliedMigrations = Migrator.Apply(connection);
        return db;
    }

    /// <summary>Opens a pooled read-write connection with DiskMon's per-connection pragmas.</summary>
    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        connection.Execute($"PRAGMA busy_timeout = {BusyTimeoutMs}; PRAGMA foreign_keys = ON;");
        return connection;
    }
}
