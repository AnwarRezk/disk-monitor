using Dapper;
using DiskDrift.Core.Data;
using Microsoft.Data.Sqlite;

namespace DiskDrift.Core.Tests;

public sealed class ReadOnlyDbTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Reads_while_the_writer_is_live_without_falling_back()
    {
        var path = _temp.File("diskdrift.db");
        var writer = Db.OpenOrCreate(path);
        new VolumeSampleStore(writer).Insert([new VolumeSample(1_000, "C:", 500, 200)]);
        using var held = writer.OpenConnection(); // keeps -wal/-shm alive, like the running Collector

        var reader = new ReadOnlyDb(path);

        Assert.Equal(new VolumeSample(1_000, "C:", 500, 200), new VolumeSampleStore(reader).Latest("C:"));
        Assert.Equal(ReadOnlyOpenMode.ReadOnly, reader.LastMode);
    }

    [Fact]
    public void Sees_new_samples_from_the_live_writer()
    {
        var path = _temp.File("diskdrift.db");
        var writer = Db.OpenOrCreate(path);
        var writerStore = new VolumeSampleStore(writer);
        using var held = writer.OpenConnection();
        var readerStore = new VolumeSampleStore(new ReadOnlyDb(path));

        writerStore.Insert([new VolumeSample(1_000, "C:", 500, 200)]);
        Assert.Equal(200, readerStore.Latest("C:")!.FreeBytes);

        writerStore.Insert([new VolumeSample(31_000, "C:", 500, 150)]);
        Assert.Equal(150, readerStore.Latest("C:")!.FreeBytes);
    }

    [Fact]
    public void Never_writes()
    {
        var path = _temp.File("diskdrift.db");
        Db.OpenOrCreate(path);
        var reader = new ReadOnlyDb(path);

        using var connection = reader.OpenConnection();
        var ex = Assert.Throws<SqliteException>(() =>
            connection.Execute("INSERT INTO volume_samples (ts, volume, total_bytes, free_bytes) VALUES (1, 'C:', 1, 1)"));
        Assert.Equal(8, ex.SqliteErrorCode); // SQLITE_READONLY
    }

    [Fact]
    public void Immutable_open_reads_a_closed_database_through_Microsoft_Data_Sqlite()
    {
        var path = _temp.File("disk mon.db"); // a space, to prove the file: URI is escaped correctly
        var writer = Db.OpenOrCreate(path);
        new VolumeSampleStore(writer).Insert([new VolumeSample(1_000, "C:", 500, 200)]);
        SqliteConnection.ClearAllPools(); // the Collector stopped: last close checkpoints and deletes -wal/-shm

        var builder = new SqliteConnectionStringBuilder { DataSource = ReadOnlyDb.ImmutableUri(path), Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();

        Assert.Equal(200, connection.ExecuteScalar<long>("SELECT free_bytes FROM volume_samples"));
        Assert.False(File.Exists(path + "-shm"), "An immutable reader must not create the -shm file.");
        Assert.False(File.Exists(path + "-wal"), "An immutable reader must not create the -wal file.");
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void Falls_back_to_immutable_when_the_read_only_open_fails()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() || Environment.UserName == "root",
            "Needs a POSIX directory the test user cannot write; the Windows equivalent is verified manually (see PR).");
        var dir = _temp.File("locked");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "diskdrift.db");
        new VolumeSampleStore(Db.OpenOrCreate(path)).Insert([new VolumeSample(1_000, "C:", 500, 200)]);
        SqliteConnection.ClearAllPools();
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var reader = new ReadOnlyDb(path);
            Assert.Equal(200, new VolumeSampleStore(reader).Latest("C:")!.FreeBytes);
            Assert.Equal(ReadOnlyOpenMode.Immutable, reader.LastMode);
        }
        finally
        {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void A_missing_database_throws_and_is_not_created()
    {
        var path = _temp.File("missing.db");

        Assert.Throws<SqliteException>(() => new ReadOnlyDb(path).OpenConnection());
        Assert.False(File.Exists(path));
    }
}
