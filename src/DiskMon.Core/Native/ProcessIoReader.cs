using System.Diagnostics;
using System.Runtime.Versioning;
using DiskMon.Core.Analysis;
using Microsoft.Win32.SafeHandles;

namespace DiskMon.Core.Native;

/// <summary>Reads every process's cumulative I/O counters, start time and image path.</summary>
[SupportedOSPlatform("windows")]
public static class ProcessIoReader
{
    /// <summary>
    /// Returns a snapshot per readable process. Processes that deny access (protected ones, or any when not
    /// elevated) or exit mid-read are left out and counted in <paramref name="skipped"/>.
    /// </summary>
    public static List<ProcessIoSnapshot> ReadAll(out int skipped)
    {
        var snapshots = new List<ProcessIoSnapshot>();
        skipped = 0;

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                // PID 0 is the Idle pseudo-process: no counters to read.
                if (process.Id == 0)
                    continue;

                if (TryRead(process.Id, process.ProcessName) is { } snapshot)
                    snapshots.Add(snapshot);
                else
                    skipped++;
            }
        }

        return snapshots;
    }

    private static ProcessIoSnapshot? TryRead(int pid, string processName)
    {
        using var handle = Kernel32.OpenProcess(Kernel32.ProcessQueryLimitedInformation, false, (uint)pid);
        if (handle.IsInvalid)
            return null;

        if (!Kernel32.GetProcessTimes(handle, out var creation, out _, out _, out _))
            return null;
        if (!Kernel32.GetProcessIoCounters(handle, out var io))
            return null;

        var exePath = ImagePath(handle);
        // Name from the image when we have it (keeps ".exe" and matches the path); otherwise the OS's name,
        // e.g. "System", "Registry" or "MemCompression", which have no image path.
        var name = exePath is null ? processName : Path.GetFileName(exePath);
        var startMs = DateTimeOffset.FromFileTime(creation).ToUnixTimeMilliseconds();

        return new ProcessIoSnapshot(new ProcessKey(pid, startMs), name, exePath, io.ReadTransferCount, io.WriteTransferCount);
    }

    private static unsafe string? ImagePath(SafeProcessHandle handle)
    {
        const int capacity = 32_768;
        var buffer = new char[capacity];
        var size = (uint)capacity;
        fixed (char* p = buffer)
        {
            return Kernel32.QueryFullProcessImageName(handle, 0, p, ref size) && size > 0
                ? new string(buffer, 0, (int)size)
                : null;
        }
    }
}
