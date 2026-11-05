# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project status

DiskMon is a Windows-only disk-space monitor. `PLAN.md` is the source of truth for design, schema, milestones (M0–M5) and acceptance criteria; read it before starting work, together with `GLOSSARY.md` (use its terms: Swing, Probe, Probe Window, Unaccounted, Downtime…) and `docs/adr/`. **Implement one milestone at a time** and stop for review when its acceptance criteria pass. Don't start the next milestone unprompted.

M0 (scaffolding, Linear DIS-6) has landed: the solution, the full MVP schema as migration `0001`, the data-folder ACL, config binding (`DiskMonOptions`), capped Serilog logs and Windows CI. Since then the Collector records Volume Samples, Top Writers and Probe Samples (`DiskMon.Core/Probes`: `ProbeExpander`, `ProbeMeasurer`, `ProbeRunner`; VSS via WMI in `VssSampler`), prunes old samples, and serves `ping` and `probe_now` on the pipe. `SwingService` detects Swings (`SwingDetector`, pure, in `Analysis`; `SwingRecorder` applies its actions to `swings`) and starts a Probe run at Swing open/close. When a Probe run at or after a Closing Swing's end is stored, `SwingAttributor` runs `Attribution` (pure, in `Analysis`) and moves it to `closed`; after a restart, or a close by Downtime, it attributes straight away with the samples it has. The dashboard ("Violet Pro" look, `Theme`) opens a Swing in a drawer (from its chip in the chart's Swing lane, its span on the chart or its row) listing the items, the Probe Window and Unaccounted, with "Open location" (`OpenLocation`).

## Commands

```powershell
dotnet build                                              # whole solution (DiskMon.sln); also builds on Linux/macOS (EnableWindowsTargeting)
dotnet test                                               # xUnit tests in tests/DiskMon.Core.Tests
dotnet test --filter "FullyQualifiedName~SwingDetector"   # run a subset / single test
dotnet run --project src/DiskMon.Collector                # run collector as a console app (use an ELEVATED terminal)
dotnet run --project src/DiskMon.Tray                     # tray UI (normal, non-elevated)
scripts/install-service.ps1                               # publish self-contained win-x64 + sc.exe create/start (admin)
installer/build.ps1 -Version 0.1.0                        # DiskMon-Setup-0.1.0.exe via Inno Setup 6 (needs ISCC)
scripts/simulate-growth.ps1 -SizeGB 2 -Location temp|unprobed -Cleanup   # produce test Swings (admin)
```

Run `dotnet build` and `dotnet test` before declaring a milestone done. CI (`.github/workflows/ci.yml`) runs the same on `windows-latest`, then builds the setup (artifact `DiskMon-Setup-*`). Releasing: run the CI workflow by hand (`workflow_dispatch`) with a `version`, or push a `v*` tag; either tags the commit and publishes a GitHub Release with the setup attached. Windows-only tests (ACLs) call `Assert.SkipUnless(OperatingSystem.IsWindows(), …)` so the suite also runs elsewhere. Tests use xUnit v3.

## Conventions

- .NET 10 (`net10.0-windows`, SDK pinned in `global.json`), C# latest, nullable enabled, file-scoped namespaces; shared settings in `Directory.Build.props`. Warnings are errors in `DiskMon.Core`.
- Pure logic lives in `DiskMon.Core/Analysis` (e.g. `SwingDetector`, `Attribution`) and must have unit tests. **No Win32 calls there.** `SwingDetector` is a pure function: (ordered samples + current state) → list of actions.
- Every P/Invoke lives in `DiskMon.Core/Native`, using `[LibraryImport]` with `SetLastError = true`.
- Data access is `Microsoft.Data.Sqlite` + `Dapper` (no EF). Charts: `ScottPlot.WinForms`. Logging: Serilog.
- Timestamps are UTC Unix epoch **milliseconds** (INTEGER); sizes are INTEGER bytes.
- Schema changes go in new numbered SQL migration files (`NNNN_snake_name.sql`, numbered 1..n without gaps) under `DiskMon.Core/Data/Migrations`, embedded as resources and applied by `Migrator` at collector startup, each in its own transaction. `schema_version` is created by `Migrator`, not by a migration. `PRAGMA journal_mode = WAL` is set by `Db.OpenOrCreate` because it can't run inside a transaction. Never edit an applied migration.
- Config lists (`Volumes`, `Probes`) start empty and get defaults in `DiskMonOptions.Normalize()`, because the binder appends to pre-filled lists. Add validation for new settings in `DiskMonOptions.Validate()`.
- The logo's source is `assets/logo.svg` (Violet palette). `assets/diskmon.ico` (16–256 px) is the exe and window icon for both apps; the Tray embeds it plus `logo-128.png` for the dashboard header (`AppIcons`). Regenerate both from the SVG when it changes, and the setup's wizard images (`installer/wizard-*.bmp`: the logo on white, and on the #2d2150→#150f23 gradient). The tray icon itself stays the live free-space number. README screenshots live in `docs/images` (take them with `--screenshot`).
- Log sinks go through `DiskMonLogging.CappedFile(...)` (7 files × 10 MB). The data folder (and its ACL) must exist before the logger is created, since logs live inside it.

## Architecture

Two processes sharing one SQLite DB at `%ProgramData%\DiskMon\diskmon.db` (folder ACL: SYSTEM + Administrators write, Users read-only):

- **DiskMon.Collector**: Worker Service running as an elevated Windows Service (LocalSystem) via `UseWindowsService()`, or as a console app from a terminal. It is the **only writer**. Samplers on independent cadences: `VolumeSampler` (30 s, Volumes from config, default C:) → `ProcessIoSampler` (60 s, top 10 writers ≥ 1 MB, identity in `processes`) → `ProbeSampler` + `VssSampler` (10 min, at Swing open/close, and on demand). `VolumeSampler` publishes samples to an in-process channel consumed by `SwingService`, which opens/closes `growth`/`release` Swings (|Δfree| ≥ 1 GB over a 15-min ring buffer, one global threshold). A move ≥ threshold in the opposite direction closes the Swing at its peak and opens the opposite kind. Stages: `open` → `closing` (waiting for the post-close probe run) → `closed` (attributed). A sample gap > 2 intervals is Downtime: reset the ring buffer and, if free space moved ≥ threshold across it, record a Swing with `across_downtime = 1`. `PipeServer` serves the named pipe `DiskMon` (`ping`, `probe_now`). `RetentionService` prunes samples older than 30 days daily (Swings are kept forever).
- **DiskMon.Tray**: unprivileged WinForms `NotifyIcon` + `DashboardForm`. **Never writes the DB**: opens it `Mode=ReadOnly`, falling back to an immutable open when the Collector is stopped. Talks to the Collector only through the pipe (no `commands` table). Shows "Collector not running" if `ping` fails or the newest sample is older than 2 min.
- **DiskMon.Core**: models, DB/migrations, config (`DiskMonOptions`, bound from `appsettings.json` next to the collector exe), native interop, IPC contracts, probe expansion, analysis.

Swing attribution: probe deltas use the nearest sample ≤ `ts_start` and ≥ `ts_end` (stored as the Swing's Probe Window), keep items with |Δ| ≥ 50 MB; `unaccounted = used_delta − Σ probe deltas`. Process write-byte items are a separate ranking axis and are **not** added to `attributed`.

## Installer

`installer/DiskMon.iss` (Inno Setup 6) mirrors `scripts/install-service.ps1`: same folders, service, HKLM Run value `DiskMonTray` and kept `appsettings.json`. Its service steps live in `installer/service.ps1` (Stop before files are copied, Install after, Remove on uninstall); keep the two install paths in step. Never change the `.iss` `AppId`. The Start menu/desktop shortcut runs `DiskMon.Tray.exe --dashboard`; a second Tray launch signals the running one (named event `Local\DiskMon.Tray.OpenDashboard`) instead of showing a second icon.

## Gotchas

- The collector runs as SYSTEM, so `%LOCALAPPDATA%` etc. resolve to the system profile. Per-user probes use `{user}` templates expanded over `C:\Users\*` (excluding `Default`, `Default User`, `Public`, `All Users`) by `ProbeExpander`.
- Directory scans must skip reparse points (`AttributesToSkip = ReparsePoint`, `IgnoreInaccessible = true`) and honour a per-probe timeout (keep the partial size). Never scan all of `C:\`; WinSxS is deliberately not a probe.
- `pagefile.sys`/`hiberfil.sys`/`swapfile.sys` can't be opened; read sizes from the directory entry (`DirectoryInfo("C:\\").EnumerateFiles(name)`).
- `ProcessIoSampler` keys previous counters by `(pid, start_time)` to survive PID reuse. Its counters include non-disk I/O; present them as a ranking signal, not bytes on disk.
- SQLite: WAL, `busy_timeout=5000`, single writer. A read-only WAL reader needs the `-wal`/`-shm` files the Collector keeps alive, hence the Tray's immutable fallback.
- Per-user Probes cover every profile and the DB is world-readable: a known privacy gap to fix before distributing.
- DiskMon must not cause disk growth itself: Serilog capped at 7 files × 10 MB in `%ProgramData%\DiskMon\logs`, DB retention enforced, target < ~50 MB/month DB growth.
- VSS, WMI and all-process I/O counters require admin; run the collector elevated during development.

## Agent skills

### Issue tracker

Issues live in Linear, workspace `disk-monitor`, team `DIS`. See `docs/agents/issue-tracker.md`.

### Triage labels

Default five labels: `needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, `wontfix`. See `docs/agents/triage-labels.md`.

### Domain docs

Single-context: `GLOSSARY.md` + `docs/adr/` at the repo root. See `docs/agents/domain.md`.
