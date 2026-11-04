namespace DiskMon.Core.Data;

/// <summary>One numbered SQL script embedded under Data/Migrations, e.g. <c>0001_initial_schema.sql</c>.</summary>
public sealed record Migration(int Version, string Name, string Sql);
