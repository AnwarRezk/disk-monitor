using Dapper;
using DiskDrift.Core.Data;
using Microsoft.Data.Sqlite;

namespace DiskDrift.Core.Tests;

public sealed class MigrationTests : IDisposable
{
    private static readonly string[] MvpTables =
    [
        "probe_samples", "probes", "process_io_samples", "processes",
        "schema_version", "swing_items", "swings", "volume_samples",
    ];

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Creating_an_empty_database_applies_every_migration()
    {
        var db = Db.OpenOrCreate(_temp.File("diskdrift.db"));

        Assert.True(File.Exists(db.Path));
        Assert.Equal(Migrator.All, db.AppliedMigrations);
        using var connection = db.OpenConnection();
        Assert.Equal(Migrator.LatestVersion, Migrator.CurrentVersion(connection));
    }

    [Fact]
    public void Database_is_in_WAL_mode()
    {
        var db = Db.OpenOrCreate(_temp.File("diskdrift.db"));

        using var connection = db.OpenConnection();
        Assert.Equal("wal", connection.ExecuteScalar<string>("PRAGMA journal_mode"));
    }

    [Fact]
    public void Schema_has_every_MVP_table_and_no_commands_table()
    {
        var db = Db.OpenOrCreate(_temp.File("diskdrift.db"));

        using var connection = db.OpenConnection();
        var tables = connection.Query<string>(
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name").ToArray();
        Assert.Equal(MvpTables, tables);
        Assert.DoesNotContain("commands", tables);
    }

    [Fact]
    public void Swings_table_has_the_planned_columns()
    {
        var db = Db.OpenOrCreate(_temp.File("diskdrift.db"));

        using var connection = db.OpenConnection();
        var columns = connection.Query<string>("SELECT name FROM pragma_table_info('swings')").ToArray();
        Assert.Equal(
            ["id", "volume", "kind", "stage", "across_downtime", "ts_start", "ts_end", "probe_window_start",
             "probe_window_end", "used_delta_bytes", "attributed_bytes", "unaccounted_bytes"],
            columns);
    }

    [Fact]
    public void Reopening_an_up_to_date_database_applies_nothing_and_keeps_data()
    {
        var path = _temp.File("diskdrift.db");
        var first = Db.OpenOrCreate(path);
        using (var connection = first.OpenConnection())
            connection.Execute("INSERT INTO volume_samples (ts, volume, total_bytes, free_bytes) VALUES (1, 'C:', 100, 40)");

        var second = Db.OpenOrCreate(path);

        Assert.Empty(second.AppliedMigrations);
        using var check = second.OpenConnection();
        Assert.Equal(1, check.ExecuteScalar<int>("SELECT COUNT(*) FROM schema_version"));
        Assert.Equal(Migrator.LatestVersion, Migrator.CurrentVersion(check));
        Assert.Equal(1, check.ExecuteScalar<int>("SELECT COUNT(*) FROM volume_samples"));
    }

    [Fact]
    public void Only_migrations_newer_than_the_current_version_are_applied()
    {
        using var connection = OpenRaw(_temp.File("partial.db"));
        Migration[] v1 = [new(1, "one", "CREATE TABLE a (x INTEGER);")];
        Migration[] v2 = [.. v1, new(2, "two", "CREATE TABLE b (x INTEGER);")];

        Assert.Single(Migrator.Apply(connection, v1));
        var applied = Migrator.Apply(connection, v2);

        Assert.Equal([2], applied.Select(m => m.Version));
        Assert.Equal(2, Migrator.CurrentVersion(connection));
    }

    [Fact]
    public void A_failing_migration_rolls_back_and_leaves_the_version_unchanged()
    {
        using var connection = OpenRaw(_temp.File("failing.db"));
        Migration[] migrations =
        [
            new(1, "one", "CREATE TABLE a (x INTEGER);"),
            new(2, "broken", "CREATE TABLE b (x INTEGER); THIS IS NOT SQL;"),
        ];

        Assert.ThrowsAny<SqliteException>(() => Migrator.Apply(connection, migrations));

        Assert.Equal(1, Migrator.CurrentVersion(connection));
        Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'b'"));
    }

    [Fact]
    public void Embedded_migrations_are_numbered_from_one_without_gaps()
    {
        Assert.NotEmpty(Migrator.All);
        Assert.Equal(Enumerable.Range(1, Migrator.All.Count), Migrator.All.Select(m => m.Version));
    }

    private static SqliteConnection OpenRaw(string path)
    {
        var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        return connection;
    }
}
