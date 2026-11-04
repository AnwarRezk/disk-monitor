using System.Management;
using DiskMon.Core.Probes;

namespace DiskMon.Collector.Samplers;

/// <summary>
/// Reads VSS shadow storage through WMI (<c>Win32_ShadowStorage</c>, needs admin) and maps each entry's Volume to its
/// drive letter through <c>Win32_Volume</c>. <see cref="ProbeSampler"/> calls it once per Probe run, so it shares the
/// Probes' cadence: every 10 minutes, and on demand.
/// </summary>
public sealed class VssSampler : IShadowStorageReader
{
    private const string Scope = @"root\cimv2";

    public IReadOnlyList<ShadowStorage> Read()
    {
        var letters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var volumes = new ManagementObjectSearcher(Scope, "SELECT DeviceID, DriveLetter FROM Win32_Volume"))
        using (var results = volumes.Get())
        {
            foreach (var volume in results.Cast<ManagementBaseObject>())
            {
                using (volume)
                {
                    if (volume["DeviceID"] is string id && volume["DriveLetter"] is string letter && letter.Length > 0)
                        letters[id] = letter.ToUpperInvariant();
                }
            }
        }

        var storage = new List<ShadowStorage>();
        using var searcher = new ManagementObjectSearcher(Scope, "SELECT UsedSpace, AllocatedSpace, MaxSpace, Volume FROM Win32_ShadowStorage");
        using var entries = searcher.Get();
        foreach (var entry in entries.Cast<ManagementBaseObject>())
        {
            using (entry)
            {
                var id = ShadowStoragePaths.DeviceId(entry["Volume"] as string);
                if (id is null || !letters.TryGetValue(id, out var letter))
                    continue; // Shadow storage for a Volume without a drive letter: no Probe can name it.

                storage.Add(new ShadowStorage(letter, Bytes(entry["UsedSpace"]), Bytes(entry["AllocatedSpace"]), Bytes(entry["MaxSpace"])));
            }
        }
        return storage;
    }

    // UInt64 in WMI; MaxSpace is UInt64.MaxValue when unbounded.
    private static long Bytes(object? value) => value is null ? 0 : (long)Math.Min(Convert.ToUInt64(value), long.MaxValue);
}
