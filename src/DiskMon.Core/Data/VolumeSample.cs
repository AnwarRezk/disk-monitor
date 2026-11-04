namespace DiskMon.Core.Data;

/// <summary>One reading of a Volume's size and free space. <paramref name="Ts"/> is UTC Unix epoch ms.</summary>
public sealed record VolumeSample(long Ts, string Volume, long TotalBytes, long FreeBytes);
