using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;

namespace DiskDrift.Core.Data;

/// <summary>
/// Applies the numbered SQL migrations embedded in this assembly, each in its own transaction.
/// The current version lives in the single-row <c>schema_version</c> table. Re-running is a no-op.
/// </summary>
public static partial class Migrator
{
    private const string ResourcePrefix = "DiskDrift.Migrations.";

    private static readonly Lazy<IReadOnlyList<Migration>> Embedded = new(() => Load(typeof(Migrator).Assembly));

    /// <summary>All embedded migrations, ordered by version.</summary>
    public static IReadOnlyList<Migration> All => Embedded.Value;

    public static int LatestVersion => All.Count == 0 ? 0 : All[^1].Version;

    /// <summary>Brings the database up to <see cref="LatestVersion"/>. Returns the migrations it applied.</summary>
    public static IReadOnlyList<Migration> Apply(SqliteConnection connection) => Apply(connection, All);

    internal static IReadOnlyList<Migration> Apply(SqliteConnection connection, IReadOnlyList<Migration> migrations)
    {
        connection.Execute("CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL)");
        var current = CurrentVersion(connection);

        var applied = new List<Migration>();
        foreach (var migration in migrations.Where(m => m.Version > current))
        {
            using var tx = connection.BeginTransaction();
            connection.Execute(migration.Sql, transaction: tx);
            connection.Execute("DELETE FROM schema_version", transaction: tx);
            connection.Execute("INSERT INTO schema_version (version) VALUES (@v)", new { v = migration.Version }, tx);
            tx.Commit();
            applied.Add(migration);
        }

        return applied;
    }

    public static int CurrentVersion(SqliteConnection connection) =>
        connection.ExecuteScalar<int?>("SELECT MAX(version) FROM schema_version") ?? 0;

    internal static IReadOnlyList<Migration> Load(Assembly assembly)
    {
        var migrations = new List<Migration>();
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
        {
            var fileName = resource[ResourcePrefix.Length..];
            var match = FileNamePattern().Match(fileName);
            if (!match.Success)
                throw new InvalidOperationException($"Migration '{fileName}' must be named NNNN_description.sql.");

            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            migrations.Add(new Migration(
                int.Parse(match.Groups["version"].Value, CultureInfo.InvariantCulture),
                match.Groups["name"].Value,
                reader.ReadToEnd()));
        }

        migrations.Sort((a, b) => a.Version.CompareTo(b.Version));
        for (var i = 0; i < migrations.Count; i++)
        {
            if (migrations[i].Version != i + 1)
                throw new InvalidOperationException(
                    $"Migrations must be numbered 1..n without gaps or duplicates; found {migrations[i].Version} at position {i + 1}.");
        }

        return migrations;
    }

    [GeneratedRegex(@"^(?<version>\d{4})_(?<name>[a-z0-9_]+)\.sql$")]
    private static partial Regex FileNamePattern();
}
