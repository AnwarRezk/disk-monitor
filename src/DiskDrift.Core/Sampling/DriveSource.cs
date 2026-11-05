namespace DiskDrift.Core.Sampling;

/// <summary>What the OS reports about one drive letter at one moment.</summary>
public sealed record DriveState(DriveType Type, bool IsReady, long TotalBytes, long FreeBytes);

/// <summary>Looks up drives by letter. A seam so Volume selection can be tested without real drives.</summary>
public interface IDriveSource
{
    /// <summary>The drive's state, or null when no drive has that letter. <paramref name="volume"/> is like "C:".</summary>
    DriveState? Get(string volume);
}

/// <summary>Real drives via <see cref="DriveInfo"/>.</summary>
public sealed class SystemDriveSource : IDriveSource
{
    public DriveState? Get(string volume)
    {
        var drive = new DriveInfo(volume);
        if (drive.DriveType == DriveType.NoRootDirectory)
            return null;
        if (!drive.IsReady)
            return new DriveState(drive.DriveType, false, 0, 0);

        // TotalFreeSpace is the Volume's free space, ignoring any per-user quota; that is what a Swing measures.
        return new DriveState(drive.DriveType, true, drive.TotalSize, drive.TotalFreeSpace);
    }
}
