using Microsoft.Data.Sqlite;

namespace DiskDrift.Core.Data;

/// <summary>Something that opens connections to the DiskDrift database: the Collector's <see cref="Db"/> or the Tray's <see cref="ReadOnlyDb"/>.</summary>
public interface IDbConnectionSource
{
    SqliteConnection OpenConnection();
}
