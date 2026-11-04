# DiskMon — Implementation Plan

Windows disk-space monitor that records **when** free space changes, **what** grew or shrank, and **which process** was writing, with one-click "open location".

The goal is to explain the "disk fills up, then suddenly 5–10 GB is freed" pattern on Windows.

---

## 1. Scope

**MVP (M0–M3)**
- Continuous free-space timeline per fixed volume.
- Per-process write-bytes ranking over time.
- Size tracking of a configurable list of known "suspect" locations, including hidden ones (VSS shadow storage, pagefile, hiberfil).
- Automatic **events** when free space moves by ≥ 1 GB within 15 min. Each event shows:
  - attribution: which probes changed and which processes wrote the most;
  - an **unaccounted** bucket for space that no probe explains.
- Tray icon with free space; dashboard with chart, event list, event detail and "Open location".

**Post-MVP**
- M4: USN Change Journal reader for exact file-level attribution, including deleted files.
- M5: ETW process↔file attribution, alerts, and cleanup rules.

**Non-goals**
- Cross-platform support.
- Full-drive treemap (WizTree already does it).
- Cloud sync.
- Auto-deleting anything in the MVP.

---

## 2. Key decisions

| Decision | Choice | Reason |
|---|---|---|
| Language/runtime | C# / .NET 10 (`net10.0-windows`) | First-class Win32 P/Invoke, WMI, Windows Service hosting, WinForms tray |
| Process model | **Collector**: elevated Windows Service (LocalSystem). **Tray UI**: normal user process | VSS/WMI, all-process I/O counters and USN need admin; the UI shouldn't run elevated |
| IPC | Shared SQLite DB (WAL mode) plus a `commands` table polled by the collector | Simplest possible; no named pipes in the MVP |
| DB location | `%ProgramData%\DiskMon\diskmon.db` | Machine-wide; ACL grants `BUILTIN\Users` read and write on the folder |
| Data access | `Microsoft.Data.Sqlite` + `Dapper` | Lightweight, no EF |
| Charts | `ScottPlot.WinForms` | Fast, simple, good with time series |
| Logging | `Serilog` rolling file in `%ProgramData%\DiskMon\logs`, 7-file / 10 MB cap | **Don't become the log-growth problem ourselves** |
| Config | `appsettings.json` next to the collector exe | Intervals, thresholds, probes, retention |

---

## 3. Repo layout

```
DiskMon.sln
src/
  DiskMon.Core/              # class lib: models, DB, migrations, config, native interop, pure logic
    Data/                    # Db.cs, Migrations/*.sql, repositories
    Native/                  # Kernel32.cs (GetProcessIoCounters, QueryFullProcessImageName, ...)
    Analysis/                # EventDetector.cs, Attribution.cs (pure, unit-tested)
    Probes/                  # ProbeDefinition, ProbeExpander (per-user path expansion)
  DiskMon.Collector/         # Worker Service (Windows Service host)
    Samplers/                # VolumeSampler, ProcessIoSampler, ProbeSampler, VssSampler
    Services/                # EventService, CommandPoller, RetentionService
    appsettings.json
  DiskMon.Tray/              # WinForms app: NotifyIcon + DashboardForm
tests/
  DiskMon.Core.Tests/        # xUnit
scripts/
  install-service.ps1        # publish + sc create + start (requires admin)
  uninstall-service.ps1
  simulate-growth.ps1        # creates/deletes big files to produce test events
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

CREATE TABLE process_io_samples (                     -- only the top N writers per interval
  ts INTEGER NOT NULL, pid INTEGER NOT NULL, start_time INTEGER NOT NULL,
  name TEXT NOT NULL, exe_path TEXT,
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

CREATE TABLE events (
  id INTEGER PRIMARY KEY, volume TEXT NOT NULL,
  kind TEXT NOT NULL,                                 -- 'growth' | 'release'
  ts_start INTEGER NOT NULL, ts_end INTEGER,          -- ts_end NULL while open
  used_delta_bytes INTEGER,                           -- +growth / -release (= -Δfree)
  attributed_bytes INTEGER, unaccounted_bytes INTEGER
);

CREATE TABLE event_items (
  event_id INTEGER NOT NULL REFERENCES events(id),
  source TEXT NOT NULL,                               -- 'probe' | 'process' | 'usn'
  label TEXT NOT NULL, path TEXT,                     -- path used for "Open location"
  delta_bytes INTEGER NOT NULL, rank INTEGER NOT NULL
);

CREATE TABLE commands (                               -- UI -> collector
  id INTEGER PRIMARY KEY, ts INTEGER NOT NULL,
  command TEXT NOT NULL,                              -- 'probe_now'
  status TEXT NOT NULL DEFAULT 'pending'              -- 'pending' | 'done' | 'failed'
);
```

Migrations are numbered SQL files embedded as resources and applied at collector startup.

**Retention** (`RetentionService`, runs daily):
- raw `volume_samples`: 30 days;
- `process_io_samples` and `probe_samples`: 30 days;
- events: kept forever.

---

## 5. Components

### 5.1 VolumeSampler (every 30 s)
- `DriveInfo.GetDrives()` filtered to `DriveType.Fixed` and `IsReady`.
- Insert one row per volume.
- Publish the latest sample to an in-process channel consumed by `EventService`.

### 5.2 ProcessIoSampler (every 60 s)
- For each process: `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)`, then `GetProcessIoCounters`, `GetProcessTimes` (start time) and `QueryFullProcessImageName`.
- Key the previous counters by `(pid, start_time)` so PID reuse doesn't produce bogus deltas.
- Skip access-denied and protected processes silently, with a debug-level log.
- Store the top 20 by `write_bytes_delta`, skipping deltas of 0.
- Caveat to note in the UI: these counters include all I/O, not only disk writes. Treat them as a **ranking signal**, not as exact bytes on disk.

### 5.3 ProbeSampler (every 10 min, and on demand)

**Probe definitions** come from config. Per-user templates use `{user}` and are expanded over every folder in `C:\Users\*`, excluding `Default`, `Default User`, `Public` and `All Users`.
- This matters because the service runs as SYSTEM, so `%LOCALAPPDATA%` would resolve to the systemprofile.

**Directory size** is a recursive `Directory.EnumerateFiles` with:
- `EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }`, which avoids junction loops;
- a sum of `FileInfo.Length`;
- a per-probe timeout (default 60 s). On timeout, record the error and keep the partial size.

**Root system files** (`pagefile.sys`, `hiberfil.sys`, `swapfile.sys`): read their size through `new DirectoryInfo("C:\\").EnumerateFiles(name)`. Opening them fails, but the directory entry has the size.

**VSS shadow storage** comes from `VssSampler`: WMI `SELECT UsedSpace, AllocatedSpace, MaxSpace, Volume FROM Win32_ShadowStorage` (System.Management). Map `Volume` to a drive letter via `Win32_Volume`. It is stored as a probe with `kind='vss'`.

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

### 5.4 EventService (consumes volume samples)

The detection logic lives in `DiskMon.Core/Analysis/EventDetector.cs` as a **pure function**: input is an ordered list of samples plus the current state, output is a list of actions. This keeps it unit-testable.

**Window:** compare the current free space with free space at about `now - 15 min`, taken from an in-memory ring buffer.

**Opening an event:** when `|Δfree| ≥ 1 GB` and there is no open event for that volume:
- open a `growth` or `release` event, with `ts_start` set to the ring-buffer timestamp the comparison used;
- trigger an immediate probe run.

**Closing an event:** when the 15-min window Δ stays below `threshold / 2` for 2 consecutive samples, or after a 2-hour maximum duration:
- set `ts_end` and trigger an immediate probe run;
- then run attribution, which gives `used_delta = free(ts_start) - free(ts_end)`.

**Probe attribution:**
- For each probe on that volume, take the size at the nearest sample **≤ ts_start** and the nearest **≥ ts_end**. The delta is the difference.
- Keep items with `|delta| ≥ 50 MB`, ranked by absolute delta.
- `attributed = Σ probe deltas`, `unaccounted = used_delta - attributed`.

**Process attribution:** `Σ write_bytes_delta` per `(name, exe_path)` between ts_start and ts_end, top 10, stored as `source='process'` items with `path = exe_path`. Process items are not added into `attributed`; they live on a different axis.

**On startup:** close any event left open, using the last sample.

### 5.5 CommandPoller (every 5 s)
- Picks up `pending` commands.
- `probe_now` runs ProbeSampler immediately.
- Marks each command `done` or `failed`.

### 5.6 Tray app (DiskMon.Tray)

**`NotifyIcon`** in a hidden `ApplicationContext`:
- Icon: a bitmap rendered at runtime with the free GB number of the system drive. Background is green above 20 GB, amber at 10–20 GB, red below 10 GB.
- Tooltip, for example: `C: 12.4 GB free (−3.1 GB in 24h)`.
- Context menu: Open dashboard · Probe now · Open data folder · Exit.
- Refreshes from the DB every 30 s.
- If the DB is missing or has not been updated for more than 2 min, show a grey icon with a "Collector not running" tooltip.

**`DashboardForm`:**
- Volume dropdown and range buttons (6h / 24h / 7d / 30d).
- ScottPlot line chart of free GB, with vertical spans for events: red for growth, green for release.
- Events `ListView` with columns: time, volume, kind, Δ, attributed, unaccounted.
- Detail pane for the selected event:
  - probe items, then process items, then an explicit **Unaccounted** row with a hint: "Hidden system storage (VSS, pagefile) or locations not covered by probes".
- Double-click or the "Open location" button:
  - an existing file: `explorer.exe /select,"<path>"`;
  - a directory: `explorer.exe "<path>"`;
  - a process: `/select` on its exe.
  - If the path no longer exists, open the nearest existing parent.
- "Top writers" tab: process ranking for the selected range.

**Startup:** the installer adds a `HKCU\...\Run` entry, or there is a checkbox in the context menu.

---

## 6. Milestones

Each milestone ends with its acceptance criteria passing. Stop and review between milestones.

### M0 — Scaffolding
- Solution and the 4 projects; nullable enabled; warnings as errors in Core.
- `Db` class: opens or creates the DB, enables WAL, applies embedded migrations, sets folder ACLs on first create.
- Config binding (`DiskMonOptions`).
- Serilog with size caps.
- GitHub Actions: `dotnet build` + `dotnet test` on `windows-latest`.

**Accept:** `dotnet test` passes, and running the collector in a console creates the DB with all tables.

### M1 — Collector core
- `VolumeSampler`, `ProcessIoSampler`, `RetentionService`.
- Host runs as a console app when started from a terminal and as a service under the service control manager (`UseWindowsService()`).
- `scripts/install-service.ps1`: `dotnet publish -c Release -r win-x64 --self-contained`, then `sc.exe create DiskMon binPath=... start=auto obj=LocalSystem`, then start the service.

**Accept:**
- After 10 min of running, `volume_samples` has about 20 rows per fixed drive.
- `process_io_samples` shows plausible top writers.
- The service survives a reboot.

### M2 — Tray + chart
- `NotifyIcon` with the rendered icon, tooltip and context menu.
- `DashboardForm` with the chart and range selector; events list visible but empty.

**Accept:** the tray shows correct free GB (cross-check with Explorer), and the chart renders 24h of data in under 500 ms.

### M3 — Probes, VSS, events (**MVP complete**)
- `ProbeExpander` (unit-tested), `ProbeSampler`, `VssSampler`, `EventService` + `EventDetector`, `CommandPoller`.
- Event detail pane, Open location, Unaccounted row, Top writers tab.

**Accept**, using `scripts/simulate-growth.ps1`:
1. Create a 2 GB file in `%TEMP%` (`fsutil file createnew`). A `growth` event appears, with the **user Temp** probe at about +2 GB.
2. Delete it. A `release` event appears, with Temp at about −2 GB.
3. Create a 2 GB file in an unprobed folder. Unaccounted is about +2 GB.
4. "Open location" on the Temp item opens Explorer at that folder.

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

**Output:** new tables `usn_state` and `file_changes`. Event attribution adds the top 20 `usn` items: per-file net delta, then rolled up per directory.

**Accept:** the simulate-growth tests show the exact file path as the top item, including for the deleted file.

### M5 — Later
- ETW (`Microsoft.Diagnostics.Tracing.TraceEvent`, kernel FileIO) for process→file attribution.
- Toast alerts (free space below a threshold, or growth rate above X GB/h).
- Opt-in cleanup actions per probe.
- Allocated-size accounting via `GetCompressedFileSize` (sparse and compressed files).

---

## 7. Testing strategy

**Unit tests (Core):**
- `EventDetector` with synthetic sample sequences: open, close, max duration, flapping near the threshold, restart with an open event.
- `Attribution`: probe delta math, nearest-sample selection, unaccounted computation.
- `ProbeExpander`: `{user}` expansion and globs.
- Migrations apply cleanly on an empty DB and are idempotent.

**Manual / integration:** `scripts/simulate-growth.ps1 -SizeGB 2 -Location temp|unprobed -Cleanup`, which must run elevated.

**Soak test:** run for 3+ days to capture a real fill-and-release cycle. Confirm that DB growth stays under about 50 MB per month.

---

## 8. Risks & gotchas

- **Service runs as SYSTEM:** environment variables resolve to the system profile, so always expand per-user paths explicitly.
- **Junctions and symlinks:** skip reparse points, or probes loop and double count. WinSxS hardlinks inflate sizes, which is why it is not a probe.
- **Probe cost:** a large Chrome or Docker folder can take seconds to scan, so keep a 10-min cadence and per-probe timeouts. Never scan the whole of `C:\`.
- **Process I/O counters** include non-disk I/O. Label them as a ranking signal in the UI.
- **WSL/Docker vhdx** files grow without ever shrinking. They show up as steady growth rather than events; the chart makes that visible.
- **Logical vs allocated size:** the MVP uses logical size. Compressed and sparse files (and the vhdx) may differ.
- **Our own footprint:** cap logs and apply DB retention. DiskMon must not become a source of growth itself.
- **SQLite concurrency:** WAL, one writer (the collector), the UI opens read-only (`Mode=ReadOnly`) except for inserts into `commands`, and `busy_timeout=5000`.

---

## 9. Kickoff with Claude Code

1. `mkdir DiskMon && cd DiskMon && git init`, then copy this file in as `PLAN.md`.
2. Add these conventions to your AGENTS.md / CLAUDE.md:
   - `.NET 10`, C# latest, nullable on, file-scoped namespaces.
   - Pure logic goes in `DiskMon.Core/Analysis` with tests; no Win32 calls there.
   - Every P/Invoke lives in `DiskMon.Core/Native` with `[LibraryImport]` and `SetLastError = true`.
   - Implement one milestone at a time; run `dotnet build` and `dotnet test` before declaring it done.
3. First prompt: *"Read PLAN.md. Implement M0 only. Stop when its acceptance criteria pass and summarize what you built."*
4. Run the collector in an **elevated** terminal during development (`dotnet run --project src/DiskMon.Collector`), because VSS, WMI and all-process counters need admin.
