using Microsoft.Data.Sqlite;

namespace DiskMon.Core.Data;

/// <summary>Something that opens connections to the DiskMon database: the Collector's <see cref="Db"/> or the Tray's <see cref="ReadOnlyDb"/>.</summary>
public interface IDbConnectionSource
{
    SqliteConnection OpenConnection();
}
