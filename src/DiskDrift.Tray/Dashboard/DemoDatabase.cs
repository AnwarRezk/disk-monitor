using Dapper;
using DiskDrift.Core;
using DiskDrift.Core.Analysis;
using DiskDrift.Core.Config;
using DiskDrift.Core.Data;
using DiskDrift.Core.Probes;

namespace DiskDrift.Tray.Dashboard;

/// <summary>
/// Builds a self-contained, anonymised SQLite database in a temp folder for <c>--screenshot</c>, so README and UI
/// review screenshots never contain personal usernames, paths or machine-specific history.
/// </summary>
internal static class DemoDatabase
{
    public static string Create(long nowMs)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"diskdrift-demo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Db.FileName);
        var db = Db.OpenOrCreate(path);
        using var connection = db.OpenConnection();

        const long minute = 60_000;
        const long hour = 60 * minute;
        const long day = 24 * hour;
        const long totalBytes = 512 * Bytes.GB;

        // 7 days of Volume Samples on C: at 60 s intervals (within the 2-interval Downtime threshold), with a reboot
        // Downtime gap 9 hours ago and three clear Swings in the last 24 hours.
        var swing1Start = nowMs - (17 * hour);
        var swing1End = swing1Start + (14 * minute);
        var swing2Start = nowMs - (9 * hour) - (2 * minute);
        var swing2End = nowMs - (9 * hour) + (26 * minute);
        var swing3Start = nowMs - (3 * hour);
        var swing3End = swing3Start + (14 * minute);

        var samples = new List<VolumeSample>();
        var startTs = nowMs - (7 * day);
        for (var ts = startTs; ts <= nowMs; ts += minute)
        {
            // Simulate a 26-minute reboot Downtime 9 hours ago (between swing2Start and swing2End).
            if (ts > swing2Start && ts < swing2End)
                continue;

            var wave = (Math.Sin((ts - startTs) / (double)(3 * hour)) * 0.18)
                     + (Math.Cos((ts - startTs) / (double)(70 * minute)) * 0.08);
            double freeGb;
            if (ts <= swing1Start)
            {
                var daysBefore = (swing1Start - ts) / (double)day;
                freeGb = 49.8 + (daysBefore * 0.45) + wave;
            }
            else if (ts <= swing1End)
            {
                var t = (ts - swing1Start) / (double)(swing1End - swing1Start);
                freeGb = 49.8 - (3.3 * t);
            }
            else if (ts <= swing2Start)
            {
                freeGb = 46.5 + wave;
            }
            else if (ts <= swing3Start)
            {
                freeGb = 51.0 + wave;
            }
            else if (ts <= swing3End)
            {
                var t = (ts - swing3Start) / (double)(swing3End - swing3Start);
                freeGb = 51.0 - (3.4 * t);
            }
            else
            {
                freeGb = 47.6 + wave;
            }

            samples.Add(new VolumeSample(ts, "C:", totalBytes, (long)(freeGb * Bytes.GB)));
        }

        using (var tx = connection.BeginTransaction())
        {
            connection.Execute(
                "INSERT INTO volume_samples (ts, volume, total_bytes, free_bytes) VALUES (@Ts, @Volume, @TotalBytes, @FreeBytes)",
                samples,
                tx);
            tx.Commit();
        }

        // Probes: all per-user paths use the {user} template.
        var probes = new (ExpandedProbe Probe, double BaseGb, double NowGb)[]
        {
            (new("WSL disks", ProbeKind.Glob, @"{user}\AppData\Local\Packages\*CanonicalGroup*\LocalState", "C:"), 37.2, 38.4),
            (new("Docker", ProbeKind.Dir, @"{user}\AppData\Local\Docker", "C:"), 26.8, 26.8),
            (new("Hibernation file", ProbeKind.File, @"C:\hiberfil.sys", "C:"), 12.8, 12.8),
            (new("Page file", ProbeKind.File, @"C:\pagefile.sys", "C:"), 8.0, 8.0),
            (new("VSS shadow storage", ProbeKind.Vss, "C:", "C:"), 5.4, 6.4),
            (new("User Temp", ProbeKind.Dir, @"{user}\AppData\Local\Temp", "C:"), 2.1, 4.2),
            (new("Windows Installer cache", ProbeKind.Dir, @"C:\Windows\Installer", "C:"), 3.8, 3.8),
            (new("NuGet packages", ProbeKind.Dir, @"{user}\.nuget\packages", "C:"), 2.6, 2.9),
            (new("Windows Update downloads", ProbeKind.Dir, @"C:\Windows\SoftwareDistribution\Download", "C:"), 6.8, 2.3),
            (new("npm cache", ProbeKind.Dir, @"{user}\AppData\Local\npm-cache", "C:"), 1.4, 1.8),
            (new("Chrome", ProbeKind.Dir, @"{user}\AppData\Local\Google\Chrome\User Data", "C:"), 1.5, 1.6),
            (new("Edge", ProbeKind.Dir, @"{user}\AppData\Local\Microsoft\Edge\User Data", "C:"), 1.1, 1.1),
            (new("pnpm store", ProbeKind.Dir, @"{user}\AppData\Local\pnpm", "C:"), 0.9, 0.9),
            (new("Windows Temp", ProbeKind.Dir, @"C:\Windows\Temp", "C:"), 0.6, 0.6),
            (new("Crash dumps", ProbeKind.Dir, @"{user}\AppData\Local\CrashDumps", "C:"), 0, 0),
            (new("Minidumps", ProbeKind.Dir, @"C:\Windows\Minidump", "C:"), 0, 0),
            (new("Memory dump", ProbeKind.File, @"C:\Windows\MEMORY.DMP", "C:"), 0, 0),
            (new("Live kernel reports", ProbeKind.Dir, @"C:\Windows\LiveKernelReports", "C:"), 0, 0),
            (new("Delivery Optimization", ProbeKind.Dir, @"C:\ProgramData\Microsoft\Windows\DeliveryOptimization", "C:"), 0, 0),
            (new("Windows upgrade staging", ProbeKind.Dir, @"C:\$WINDOWS.~BT", "C:"), 0, 0),
            (new("Recycle Bin", ProbeKind.Dir, @"C:\$Recycle.Bin", "C:"), 0, 0),
            (new("Swap file", ProbeKind.File, @"C:\swapfile.sys", "C:"), 0, 0),
        };
        var ids = ProbeStore.Upsert(connection, probes.Select(p => p.Probe).ToList());
        ProbeStore.InsertSamples(
            connection,
            nowMs - (23 * hour),
            probes.Select((p, i) => (ids[i], new ProbeMeasurement((long)(p.BaseGb * Bytes.GB), 120, 45, null))).ToList());
        ProbeStore.InsertSamples(
            connection,
            nowMs - (4 * minute),
            probes.Select((p, i) => (ids[i], new ProbeMeasurement((long)(p.NowGb * Bytes.GB), 128, 42, null))).ToList());

        // Top Writers over the last 24 hours.
        ProcessIoStore.Insert(
            connection,
            nowMs - (3 * hour),
            [
                new ProcessIoDelta("dotnet.exe", @"C:\Program Files\dotnet\dotnet.exe", (long)(6.4 * Bytes.GB), (long)(2.1 * Bytes.GB)),
                new ProcessIoDelta("TiWorker.exe", @"C:\Windows\WinSxS\amd64_microsoft-windows-servicingstack\TiWorker.exe", (long)(4.2 * Bytes.GB), (long)(1.8 * Bytes.GB)),
                new ProcessIoDelta("vmmemWSL", @"C:\Windows\System32\vmwp.exe", (long)(3.1 * Bytes.GB), (long)(0.9 * Bytes.GB)),
                new ProcessIoDelta("Code.exe", @"{user}\AppData\Local\Programs\Microsoft VS Code\Code.exe", (long)(1.4 * Bytes.GB), (long)(0.6 * Bytes.GB)),
                new ProcessIoDelta("msedge.exe", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", 820 * Bytes.MB, 410 * Bytes.MB),
            ]);

        // Three closed Swings in the last 24 hours.
        connection.Execute(
            """
            INSERT INTO swings (id, volume, kind, stage, across_downtime, ts_start, ts_end, probe_window_start, probe_window_end, used_delta_bytes, attributed_bytes, unaccounted_bytes) VALUES
            (1, 'C:', 'growth',  'closed', 0, @s1Start, @s1End, @s1Start - 120000, @s1End + 60000, @s1Used, @s1Attr, @s1Unacc),
            (2, 'C:', 'release', 'closed', 1, @s2Start, @s2End, @s2Start - 180000, @s2End + 60000, @s2Used, @s2Attr, @s2Unacc),
            (3, 'C:', 'growth',  'closed', 0, @s3Start, @s3End, @s3Start - 120000, @s3End + 45000, @s3Used, @s3Attr, @s3Unacc);
            """,
            new
            {
                s1Start = swing1Start,
                s1End = swing1End,
                s1Used = (long)(3.3 * Bytes.GB),
                s1Attr = (long)(2.8 * Bytes.GB),
                s1Unacc = (long)(0.5 * Bytes.GB),
                s2Start = swing2Start,
                s2End = swing2End,
                s2Used = -(long)(4.5 * Bytes.GB),
                s2Attr = -(long)(4.5 * Bytes.GB),
                s2Unacc = 0L,
                s3Start = swing3Start,
                s3End = swing3End,
                s3Used = (long)(3.4 * Bytes.GB),
                s3Attr = (long)(3.1 * Bytes.GB),
                s3Unacc = (long)(0.3 * Bytes.GB),
            });

        connection.Execute(
            """
            INSERT INTO swing_items (swing_id, source, label, path, delta_bytes, rank) VALUES
            (1, 'probe',   'WSL disks',       '{user}\AppData\Local\Packages\*CanonicalGroup*\LocalState', @wslDelta, 1),
            (1, 'probe',   'npm cache',       '{user}\AppData\Local\npm-cache',                            @npmDelta, 2),
            (1, 'process', 'vmmemWSL',        'C:\Windows\System32\vmwp.exe',                              @wslWrite, 1),
            (2, 'probe',   'Windows Update downloads', 'C:\Windows\SoftwareDistribution\Download',         @wuDelta,  1),
            (2, 'process', 'TiWorker.exe',    'C:\Windows\WinSxS\amd64_microsoft-windows-servicingstack\TiWorker.exe', @tiWrite, 1),
            (3, 'probe',   'User Temp',       '{user}\AppData\Local\Temp',                                 @tempDelta, 1),
            (3, 'probe',   'VSS shadow storage', 'C:',                                                     @vssDelta,  2),
            (3, 'process', 'dotnet.exe',      'C:\Program Files\dotnet\dotnet.exe',                        @dotnetWrite, 1),
            (3, 'process', 'Code.exe',        '{user}\AppData\Local\Programs\Microsoft VS Code\Code.exe',  @codeWrite,   2),
            (3, 'process', 'msedge.exe',      'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe', @edgeWrite, 3);
            """,
            new
            {
                wslDelta = (long)(2.2 * Bytes.GB),
                npmDelta = (long)(0.6 * Bytes.GB),
                wslWrite = (long)(3.1 * Bytes.GB),
                wuDelta = -(long)(4.5 * Bytes.GB),
                tiWrite = (long)(4.2 * Bytes.GB),
                tempDelta = (long)(2.1 * Bytes.GB),
                vssDelta = (long)(1.0 * Bytes.GB),
                dotnetWrite = (long)(6.4 * Bytes.GB),
                codeWrite = (long)(1.4 * Bytes.GB),
                edgeWrite = 820 * Bytes.MB,
            });

        return path;
    }
}
