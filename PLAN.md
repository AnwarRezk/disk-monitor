# diskdrift — Implementation Plan

Windows disk-space monitor that records **when** free space changes, **what** grew or shrank, and **which process** was writing, with one-click "open location".

The goal is to explain the "disk fills up, then suddenly 5–10 GB is freed" pattern on Windows.

Vocabulary follows [`GLOSSARY.md`](GLOSSARY.md); the reasons behind non-obvious choices are in [`docs/adr/`](docs/adr/). This revision reflects the design review of 2026-11-04.

---

## 1. Scope

**Audience:** a personal tool for the author's own machines first. It should become a distributable tool later, so avoid choices that block hardening (see the privacy gap in §8).

**MVP (M0–M3)**
- Continuous free-space timeline per Volume. **C: is the default and the priority**; other fixed drives are opt-in through config.
- Per-process write-bytes ranking over time (Top Writers).
- Size tracking of a configurable list of known Probes, including hidden ones (VSS shadow storage, pagefile, hiberfil).
- Automatic **Swings** when free space moves by ≥ 1 GB within 15 min, including across Downtime (reboot, sleep). Each Swing shows:
  - Attribution: which Probes changed across its Probe Window and which processes wrote the most;
  - an **Unaccounted** bucket for space that no Probe explains.
- Tray icon with free space; dashboard with chart, Swing list, Swing detail and "Open location".

**Post-MVP**
- M4: USN Change Journal reader for exact file-level attribution, including deleted files.
- M5: ETW process↔file attribution, alerts, and cleanup rules.

**Non-goals**
- Cross-platform support.
- Full-drive treemap (WizTree already does it).
- Cloud sync.
- Auto-deleting anything in the MVP.
- Per-Volume thresholds in the MVP (one global threshold).

---

## 2. Key decisions

| Decision | Choice | Reason |
|---|---|---|
| Language/runtime | C# / .NET 10 (`net10.0-windows`) | First-class Win32 P/Invoke, WMI, Windows Service hosting, WinForms tray |
| Process model | **Collector**: elevated Windows Service (LocalSystem), the only writer. **Tray**: normal user process | 24/7 coverage including overnight Windows Update/VSS work; VSS/WMI, all-process I/O counters and USN need admin. [ADR 0001](docs/adr/0001-collector-service-plus-tray.md) |
| IPC | **Named pipe** `diskdrift` (JSON lines: `ping`, `probe_now`); the Tray only reads the DB | Users must not be able to rewrite history. [ADR 0002](docs/adr/0002-named-pipe-instead-of-commands-table.md) |
| DB location | `%ProgramData%\diskdrift\diskdrift.db` | Machine-wide; ACL: SYSTEM + Administrators full control, `BUILTIN\Users` **read-only** |
| Data access | `Microsoft.Data.Sqlite` + `Dapper` | Lightweight, no EF |
| Charts | `ScottPlot.WinForms` | Fast, simple, good with time series |
| Logging | `Serilog` rolling file in `%ProgramData%\diskdrift\logs`, 7-file / 10 MB cap | **Don't become the log-growth problem ourselves** |
| Config | `appsettings.json` next to the Collector exe | Volumes, intervals, thresholds, Probes, retention, tray colours |

---

## 3. Repo layout

```
DiskDrift.sln
GLOSSARY.md
docs/adr/
src/
  DiskDrift.Core/              # class lib: models, DB, migrations, config, native interop, pure logic
    Data/                    # Db.cs, Migrations/*.sql, repositories
    Native/                  # Kernel32.cs (GetProcessIoCounters, QueryFullProcessImageName, ...)
    Analysis/                # SwingDetector.cs, Attribution.cs (pure, unit-tested)
    Probes/                  # ProbeDefinition, ProbeExpander (per-user path expansion)
    Ipc/                     # pipe name, request/response contracts (shared by Collector and Tray)
  DiskDrift.Collector/         # Worker Service (Windows Service host)
    Samplers/                # VolumeSampler, ProcessIoSampler, ProbeSampler, VssSampler
    Services/                # SwingService, PipeServer, RetentionService
    appsettings.json
  DiskDrift.Tray/              # WinForms app: NotifyIcon + DashboardForm
tests/
  DiskDrift.Core.Tests/        # xUnit
scripts/
  install-service.ps1        # publish + sc create + start + HKLM Run entry for the Tray (requires admin)
  uninstall-service.ps1      # reverses all of the above
  simulate-growth.ps1        # creates/deletes big files to produce test Swings
```

---

## 4. Data model (SQLite)

Timestamps are Unix epoch milliseconds, stored as INTEGER (UTC). Sizes are INTEGER bytes.

```sql
PRAGMA journal_mode = WAL;

CREATE TABLE schema_version (version INTEGER NOT NULL);

CREATE TABLE volume_samples (
  ts INTEGER NOT NULL, volume TEXT NOT NULL,          -- 'C:'
  total_bytes INTEGER NOT NULL, free_bytes INTEGER NOT NULL
);
CREATE INDEX ix_volume_samples ON volume_samples(volume, ts);

CREATE TABLE processes (                              -- process identity, deduplicated
  id INTEGER PRIMARY KEY,
  name TEXT NOT NULL, exe_path TEXT,
  UNIQUE(name, exe_path)
);

CREATE TABLE process_io_samples (                     -- top 10 writers per interval, >= 1 MB written
  ts INTEGER NOT NULL, process_id INTEGER NOT NULL REFERENCES processes(id),
  write_bytes_delta INTEGER NOT NULL, read_bytes_delta INTEGER NOT NULL
);
CREATE INDEX ix_process_io_ts ON process_io_samples(ts);

CREATE TABLE probes (
  id INTEGER PRIMARY KEY, name TEXT NOT NULL,
  kind TEXT NOT NULL,                                 -- 'dir' | 'file' | 'vss' | 'glob'
  path TEXT NOT NULL,                                 -- expanded, absolute
  volume TEXT NOT NULL,
  UNIQUE(kind, path)
);

CREATE TABLE probe_samples (
  ts INTEGER NOT NULL, probe_id INTEGER NOT NULL REFERENCES probes(id),
  size_bytes INTEGER NOT NULL, file_count INTEGER, duration_ms INTEGER, error TEXT
);
CREATE INDEX ix_probe_samples ON probe_samples(probe_id, ts);

CREATE TABLE swings (
  id INTEGER PRIMARY KEY, volume TEXT NOT NULL,
  kind TEXT NOT NULL,                                 -- 'growth' | 'release'
  stage TEXT NOT NULL,                                -- 'open' | 'closing' | 'closed'
  across_downtime INTEGER NOT NULL DEFAULT 0,         -- 1 if measured across Downtime
  ts_start INTEGER NOT NULL, ts_end INTEGER,          -- ts_end NULL while open
  probe_window_start INTEGER, probe_window_end INTEGER, -- actual bracketing Probe Sample times
  used_delta_bytes INTEGER,                           -- +growth / -release (= -Δfree)
  attributed_bytes INTEGER, unaccounted_bytes INTEGER
);

CREATE TABLE swing_items (
  swing_id INTEGER NOT NULL REFERENCES swings(id),
  source TEXT NOT NULL,                               -- 'probe' | 'process' | 'usn'
  label TEXT NOT NULL, path TEXT,                     -- path used for "Open location"
  delta_bytes INTEGER NOT NULL, rank INTEGER NOT NULL
);
```

There is no `commands` table; Tray requests go over the named pipe (§5.5).

Migrations are numbered SQL files embedded as resources and applied at Collector startup.

**Retention** (`RetentionService`, runs daily):
- `volume_samples`, `process_io_samples`, `probe_samples`: 30 days;
- `processes` rows no longer referenced: pruned;
- Swings and Swing items: kept forever.

**Size budget:** steady state should land around 15–25 MB; growth must stay under ~50 MB/month.

---

## 5. Components

### 5.1 VolumeSampler (every 30 s)
- Volumes come from config (default `["C:"]`); each must be `DriveType.Fixed` and `IsReady`.
- Insert one row per Volume.
- Publish the latest sample to an in-process channel consumed by `SwingService`.

### 5.2 ProcessIoSampler (every 60 s)
- For each process: `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)`, then `GetProcessIoCounters`, `GetProcessTimes` (start time) and `QueryFullProcessImageName`.
- Key the previous counters by `(pid, start_time)` so PID reuse doesn't produce bogus deltas.
- Skip access-denied and protected processes silently, with a debug-level log.
- Compute deltas per process instance, then sum them **per program** (name + exe path, case-insensitive), so a program running as several processes gets one row.
- Upsert identity into `processes`; store the **top 10** programs by summed `write_bytes_delta`, only those with **≥ 1 MB** written in total.
- Caveat to note in the UI: these counters include all I/O, not only disk writes. Treat them as a **ranking signal**, not as exact bytes on disk.

### 5.3 ProbeSampler (every 10 min, at Swing open and close, and on demand)

**Probe definitions** come from config. Per-user templates use `{user}` and are expanded over every folder in `C:\Users\*`, excluding `Default`, `Default User`, `Public` and `All Users`.
- This matters because the service runs as SYSTEM, so `%LOCALAPPDATA%` would resolve to the systemprofile.

**Directory size** is a recursive `Directory.EnumerateFiles` with:
- `EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }`, which avoids junction loops;
- a sum of `FileInfo.Length`;
- a per-probe timeout (default 60 s). On timeout, record the error and keep the partial size.

**Root system files** (`pagefile.sys`, `hiberfil.sys`, `swapfile.sys`): read their size through `new DirectoryInfo("C:\\").EnumerateFiles(name)`. Opening them fails, but the directory entry has the size.

**VSS shadow storage** comes from `VssSampler`, which runs **on the same cadence as the ProbeSampler (every 10 min) and at every Swing open and close**: WMI `SELECT UsedSpace, AllocatedSpace, MaxSpace, Volume FROM Win32_ShadowStorage` (System.Management). Map `Volume` to a drive letter via `Win32_Volume`. It is stored as a Probe with `kind='vss'`.

A probe run reports completion (to `SwingService` and to the pipe's `probe_now` caller) once all its Probe Samples are written.

**Default probes:**

```
System:
  C:\Windows\SoftwareDistribution\Download
  C:\Windows\Temp
  C:\Windows\LiveKernelReports
  C:\Windows\Minidump
  C:\Windows\MEMORY.DMP                                    (file)
  C:\ProgramData\Microsoft\Windows\DeliveryOptimization
  C:\$WINDOWS.~BT
  C:\$WinREAgent
  C:\$Recycle.Bin
  C:\Windows\Logs
  C:\Windows\Installer
  C:\pagefile.sys  C:\hiberfil.sys  C:\swapfile.sys         (file)
  VSS shadow storage per volume                             (vss)

Per user ({user} = C:\Users\<name>):
  {user}\AppData\Local\Temp
  {user}\AppData\Local\CrashDumps
  {user}\AppData\Local\Google\Chrome\User Data
  {user}\AppData\Local\Microsoft\Edge\User Data
  {user}\AppData\Local\Docker
  {user}\AppData\Local\Packages\*CanonicalGroup*\LocalState (glob, WSL vhdx)
  {user}\AppData\Local\npm-cache  {user}\AppData\Local\pnpm  {user}\AppData\Local\Yarn
  {user}\AppData\Local\pip\Cache
  {user}\.gradle  {user}\.m2  {user}\.nuget\packages
  {user}\AppData\Roaming\Code  {user}\AppData\Roaming\Cursor
  {user}\AppData\Local\JetBrains
  {user}\Downloads
```

**Probe lifecycle:** on startup, upsert the expanded probe list into `probes`. Probes that point at missing paths are recorded with size 0 and are not treated as errors.

### 5.4 SwingService (consumes Volume Samples)

The detection logic lives in `DiskDrift.Core/Analysis/SwingDetector.cs` as a **pure function**: input is an ordered list of samples plus the current state, output is a list of actions. This keeps it unit-testable.

**Threshold:** one global value from config, default 1 GB over 15 min, applied to every Volume.

**Window:** compare the current free space with free space at about `now - 15 min`, taken from an in-memory ring buffer.

**Opening a Swing:** when `|Δfree| ≥ threshold` and there is no open Swing for that Volume:
- open a `growth` or `release` Swing in stage `open`, with `ts_start` set to the ring-buffer timestamp the comparison used;
- trigger an immediate probe run (including VSS).

**Direction flip:** if a Swing is open and free space then moves by ≥ threshold in the opposite direction (measured from the open Swing's peak), close the current Swing **at its peak** and open a new Swing of the other kind starting there.

**Closing a Swing:** when the 15-min window Δ stays below `threshold / 2` for 2 consecutive samples, or after a 2-hour maximum duration:
- set `ts_end`, move the Swing to stage `closing`, and trigger an immediate probe run (including VSS);
- when that probe run reports completion, run Attribution and move the Swing to `closed`. `used_delta = free(ts_start) - free(ts_end)`.

**Downtime:** a gap between consecutive Volume Samples longer than 2 sample intervals (reboot, sleep, Collector stopped) resets the ring buffer. If free space moved by ≥ threshold across the gap, record a Swing with `across_downtime = 1`, `ts_start` = last pre-gap sample and `ts_end` = first post-gap sample, go straight to `closing` and attribute it the usual way.

**Probe attribution:**
- For each Probe on that Volume, take the size at the nearest sample **≤ ts_start** and the nearest **≥ ts_end**. The delta is the difference. Store the actual bracketing times as the Swing's Probe Window (`probe_window_start`, `probe_window_end`).
- Keep items with `|delta| ≥ 50 MB`, ranked by absolute delta.
- `attributed = Σ probe deltas`, `unaccounted = used_delta - attributed`.

**Process attribution:** `Σ write_bytes_delta` per process between ts_start and ts_end, top 10, stored as `source='process'` items with `path = exe_path`. Process items are not added into `attributed`; they live on a different axis.

**On startup:** a Swing left `open` is closed using the last sample before the restart; a Swing left `closing` is attributed with whatever Probe Samples exist. Then the Downtime rule applies to the restart gap.

### 5.5 PipeServer

- Named pipe `diskdrift`, ACL: SYSTEM full control, `INTERACTIVE` (or `Authenticated Users`) read/write. One JSON request line, one JSON response line, then close.
- `{"cmd":"ping"}` → `{"ok":true,"version":"…","lastSampleTs":…}`.
- `{"cmd":"probe_now"}` → runs ProbeSampler + VssSampler and responds `{"ok":true}` (or an error) **when the run finishes**. A second request while a run is in progress waits for the same run.
- No other commands in the MVP; add new ones only when a feature needs them.

### 5.6 Tray app (DiskDrift.Tray)

**DB access:** the Tray never writes. While the Collector runs it opens the DB `Mode=ReadOnly`. If that fails (the Collector is stopped, so the `-wal`/`-shm` files are gone), it falls back to opening the DB as **immutable** and shows the stale history under a "Collector not running" banner. *Verify the immutable fallback with Microsoft.Data.Sqlite in M2.*

**`NotifyIcon`** in a hidden `ApplicationContext`:
- Icon: a bitmap rendered at runtime with the free GB number of the system drive. Colours (configurable): **red** below 5 % or below 10 GB free; **amber** below 10 % or below 20 GB; otherwise **green**.
- Tooltip, for example: `C: 12.4 GB free (−3.1 GB in 24h)`.
- Context menu: Open dashboard · Probe now (via the pipe) · Open data folder · Exit.
- Refreshes from the DB every 30 s.
- Collector status comes from the pipe `ping`. If it fails, or the newest sample is older than 2 min, show a grey icon with a "Collector not running" tooltip.

**`DashboardForm`:**
- Volume dropdown and range buttons (6h / 24h / 7d / 30d).
- ScottPlot line chart of free GB, with vertical spans for Swings: red for growth, green for release; Swings across Downtime are drawn hatched.
- Swings `ListView` with columns: time, volume, kind, Δ, attributed, unaccounted, stage (shows "attributing…" while `closing`).
- Detail pane for the selected Swing:
  - its Probe Window ("probe window 14:02–14:41") and an "across downtime" note when flagged;
  - Probe items, then process items, then an explicit **Unaccounted** row with a hint: "Hidden system storage (VSS, pagefile) or locations not covered by probes".
- Double-click or the "Open location" button:
  - an existing file: `explorer.exe /select,"<path>"`;
  - a directory: `explorer.exe "<path>"`;
  - a process: `/select` on its exe.
  - If the path no longer exists, open the nearest existing parent.
- "Top writers" tab: process ranking for the selected range.

**Startup:** `install-service.ps1` adds a machine-wide `HKLM\Software\Microsoft\Windows\CurrentVersion\Run` entry for the Tray; `uninstall-service.ps1` removes it. No per-user checkbox in the MVP.

---

## 6. Milestones

Each milestone ends with its acceptance criteria passing. Stop and review between milestones.

### M0 — Scaffolding
- Solution and the 4 projects; nullable enabled; warnings as errors in Core.
- `Db` class: opens or creates the DB, enables WAL, applies embedded migrations, sets folder ACLs on first create (SYSTEM + Administrators full, Users read-only).
- Config binding (`DiskDriftOptions`).
- Serilog with size caps.
- GitHub Actions: `dotnet build` + `dotnet test` on `windows-latest`.

**Accept:** `dotnet test` passes, and running the Collector in a console creates the DB with all tables and the expected ACL.

### M1 — Collector core
- `VolumeSampler`, `ProcessIoSampler` (with `processes` table), `RetentionService`.
- Host runs as a console app when started from a terminal and as a service under the service control manager (`UseWindowsService()`).
- `scripts/install-service.ps1`: `dotnet publish -c Release -r win-x64 --self-contained`, then `sc.exe create diskdrift binPath=... start=auto obj=LocalSystem`, then start the service.

**Accept:**
- After 10 min of running, `volume_samples` has about 20 rows for C: (and for any opted-in Volume).
- `process_io_samples` shows plausible Top Writers, at most 10 per minute, none below 1 MB.
- The service survives a reboot.

### M2 — Tray + chart + pipe
- `PipeServer` with `ping`; Tray uses it for Collector status.
- `NotifyIcon` with the rendered icon (percentage/GB colours), tooltip and context menu.
- `DashboardForm` with the chart and range selector; Swing list visible but empty.
- Read-only DB access with the immutable fallback; install script adds the HKLM Run entry.

**Accept:**
- The tray shows correct free GB (cross-check with Explorer), and the chart renders 24h of data in under 500 ms.
- A non-admin user cannot modify `diskdrift.db`.
- With the service stopped, the tray still shows history plus "Collector not running".

### M3 — Probes, VSS, Swings (**MVP complete**)
- `ProbeExpander` (unit-tested), `ProbeSampler`, `VssSampler`, `SwingService` + `SwingDetector`, `probe_now` over the pipe.
- Swing detail pane with Probe Window, Open location, Unaccounted row, Top writers tab.

**Accept**, using `scripts/simulate-growth.ps1`:
1. Create a 2 GB file in `%TEMP%` (`fsutil file createnew`). A Growth Swing appears, with the **user Temp** Probe at about +2 GB.
2. Delete it. A Release Swing appears, with Temp at about −2 GB.
3. Create a 2 GB file, then delete it within 10 minutes. Two Swings appear (growth, then release), not one netted Swing.
4. Create a 2 GB file in an unprobed folder. Unaccounted is about +2 GB.
5. Stop the service, create a 2 GB file, start the service. A Swing flagged "across downtime" appears.
6. "Open location" on the Temp item opens Explorer at that folder.

### M4 — USN Change Journal (post-MVP)
**Reading the journal:**
- `FSCTL_QUERY_USN_JOURNAL` once per NTFS volume, then every 2 min `FSCTL_READ_USN_JOURNAL` from the stored `next_usn`.
- Reason mask: `FILE_CREATE | FILE_DELETE | DATA_EXTEND | DATA_TRUNCATION | DATA_OVERWRITE | CLOSE`. Process only records that include `CLOSE`.

**Resolving paths:** `OpenFileById` + `GetFinalPathNameByHandle`. For deleted files, use a parent-ref → path cache plus the file name.

**Sizes:**
- Stat existing files.
- Keep a `file_sizes(file_ref, size, path)` cache so deletions get a size.
- Seed it lazily: only files seen in the journal, never a full-disk scan.

**Gaps:** if `journal_id` changes or `FirstUsn > next_usn`, log a gap and restart from `FirstUsn`.

**Output:** new tables `usn_state` and `file_changes`. Swing attribution adds the top 20 `usn` items: per-file net delta, then rolled up per directory. This also removes the fuzziness of the Probe Window.

**Accept:** the simulate-growth tests show the exact file path as the top item, including for the deleted file.

### M5 — Later
- ETW (`Microsoft.Diagnostics.Tracing.TraceEvent`, kernel FileIO) for process→file attribution.
- Toast alerts (free space below a threshold, or growth rate above X GB/h).
- Opt-in cleanup actions per Probe.
- Allocated-size accounting via `GetCompressedFileSize` (sparse and compressed files).
- Per-Volume thresholds and per-user privacy (see §8) for the distributable version.

---

## 7. Testing strategy

**Unit tests (Core):**
- `SwingDetector` with synthetic sample sequences: open, close, max duration, flapping near the threshold, **direction flip at the peak**, **Downtime gaps (with and without a ≥ threshold move)**, restart with an `open` or `closing` Swing.
- `Attribution`: probe delta math, nearest-sample selection, Probe Window recording, unaccounted computation.
- `ProbeExpander`: `{user}` expansion and globs.
- Pipe contracts: request/response serialization.
- Migrations apply cleanly on an empty DB and are idempotent.

**Manual / integration:** `scripts/simulate-growth.ps1 -SizeGB 2 -Location temp|unprobed -Cleanup`, which must run elevated.

**Soak test:** run for 3+ days to capture a real fill-and-release cycle, including at least one reboot. Confirm that DB growth stays under about 50 MB per month.

---

## 8. Risks & gotchas

- **Service runs as SYSTEM:** environment variables resolve to the system profile, so always expand per-user paths explicitly.
- **Junctions and symlinks:** skip reparse points, or probes loop and double count. WinSxS hardlinks inflate sizes, which is why it is not a Probe.
- **Probe cost:** a large Chrome or Docker folder can take seconds to scan, so keep a 10-min cadence and per-probe timeouts. Never scan the whole of `C:\`.
- **Fuzzy Probe Window:** `ts_start` is detected ~15 min late and Probes run every 10 min, so the before-side Probe Sample can be ~25 min before the move. Accepted for the MVP; the UI shows the real window; M4 fixes it.
- **Process I/O counters** include non-disk I/O. Label them as a ranking signal in the UI.
- **WSL/Docker vhdx** files grow without ever shrinking. They show up as steady growth rather than Swings; the chart makes that visible. On the author's PC they were ~145 GB of the Probes' 179 GB; see [`docs/space-findings.md`](docs/space-findings.md) for how to reclaim it.
- **Logical vs allocated size:** the MVP uses logical size. Compressed and sparse files (and the vhdx) may differ.
- **Our own footprint:** cap logs and apply DB retention. diskdrift must not become a source of growth itself.
- **SQLite concurrency:** WAL, one writer (the Collector), the Tray read-only (immutable fallback when the Collector is stopped), `busy_timeout=5000`.
- **Privacy gap (fix before distributing):** per-user Probes cover every profile and the DB is readable by all users, so any user can see the folder sizes of other users.

---

## 9. Kickoff with Claude Code

1. Create the repo (`git init`) and copy in `PLAN.md`, `CLAUDE.md`, `GLOSSARY.md`, `docs/` and `.claude/`.
2. First prompt: *"Read PLAN.md, GLOSSARY.md and docs/adr/. Implement M0 only. Stop when its acceptance criteria pass and summarize what you built."*
3. Run the Collector in an **elevated** terminal during development (`dotnet run --project src/DiskDrift.Collector`), because VSS, WMI and all-process counters need admin.
