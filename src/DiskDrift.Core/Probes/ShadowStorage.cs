namespace DiskDrift.Core.Probes;

/// <summary>
/// VSS shadow storage on one Volume, from WMI <c>Win32_ShadowStorage</c>. <see cref="AllocatedBytes"/> is what the
/// shadow copies take from the Volume's free space, so it is what a vss Probe records.
/// </summary>
public sealed record ShadowStorage(string Volume, long UsedBytes, long AllocatedBytes, long MaxBytes);

/// <summary>Reads VSS shadow storage for every Volume that has any (needs admin).</summary>
public interface IShadowStorageReader
{
    IReadOnlyList<ShadowStorage> Read();
}

/// <summary>Helpers for the WMI object paths that link <c>Win32_ShadowStorage</c> to <c>Win32_Volume</c>.</summary>
public static class ShadowStoragePaths
{
    /// <summary>
    /// The <c>DeviceID</c> inside a WMI reference such as <c>Win32_Volume.DeviceID="\\\\?\\Volume{…}\\"</c>
    /// (backslashes and quotes escaped), giving <c>\\?\Volume{…}\</c>; null when the reference has no DeviceID.
    /// </summary>
    public static string? DeviceId(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
            return null;

        const string key = "DeviceID=\"";
        var start = reference.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return null;

        var value = new System.Text.StringBuilder();
        for (var i = start + key.Length; i < reference.Length; i++)
        {
            var c = reference[i];
            if (c == '"')
                return value.ToString();
            if (c == '\\' && i + 1 < reference.Length)
                c = reference[++i];
            value.Append(c);
        }
        return null;
    }
}
