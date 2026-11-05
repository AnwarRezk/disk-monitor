<p align="center">
  <img src="assets/logo-256.png" width="112" alt="DiskDrift logo">
</p>

<h1 align="center">DiskDrift</h1>

<p align="center">
  <b>Find out where your free space went.</b><br>
  A Windows disk-space monitor that records <i>when</i> free space changes, <i>what</i> grew or shrank, and <i>which program</i> was writing.
</p>

<p align="center">
  <img src="docs/images/dashboard.png" alt="The DiskDrift dashboard: free-space chart, figures and the Where space is tab" width="900">
</p>

## Why

Windows drives tend to fill up quietly and then, just as suddenly, free 5 to 10 GB again. Updates download and clean
up, shadow copies come and go, WSL and Docker disks grow, browsers and package managers cache everything. A one-off
treemap shows what is big *now*; it can't tell you what changed last night.

DiskDrift runs in the background and keeps a timeline. When free space moves by 1 GB or more within 15 minutes it records
a **Swing** and explains it: which known locations changed across it, which programs wrote the most while it happened,
and how much is still **Unaccounted**.

## Features

- **Free-space timeline** for C: (other fixed drives are opt-in), sampled every 30 seconds and kept for 30 days.
- **Tray icon** showing free space in GB, coloured green, amber or red as the drive fills, and grey when the
  Collector isn't running.
- **Dashboard** with a zoomable free-space chart, warning lines, the lowest point, the change over the range, and a
  "days until full" forecast. Ranges from 6 hours to 30 days.
- **Swings**: every growth or release of 1 GB or more, including ones across a reboot or sleep, with the Probes that
  changed, the Top Writers at the time, and the Unaccounted remainder.
- **Probes**: the usual suspects measured every 10 minutes and at each Swing: Windows Update, temp folders, crash
  dumps, page/hibernation/swap files, VSS shadow storage, the Recycle Bin, browser profiles, Docker and WSL disks,
  npm/pnpm/Yarn/pip/NuGet/Gradle/Maven caches and more. "Probe now" measures them on demand.
- **Top Writers**: the programs that wrote the most, per minute, summed across their copies.
- **Open location**: jump from a Probe or Swing item straight to the folder in Explorer.
- **Small footprint**: capped logs (7 × 10 MB), automatic retention, and no growth problem of its own.

DiskDrift only watches and explains. It never deletes anything.

<p align="center">
  <img src="docs/images/swings.png" alt="The Swings tab: each Swing with what changed, the Top Writers and the Unaccounted remainder" width="900">
</p>

## How it works

DiskDrift is two processes sharing one SQLite database at `%ProgramData%\DiskDrift\diskdrift.db`:

| Part | Runs as | Does |
|---|---|---|
| **Collector** (`DiskDrift.Collector`) | Windows Service, LocalSystem | The only writer. Samples free space, process I/O, Probes and VSS; detects and attributes Swings; serves the `DiskDrift` named pipe. |
| **Tray** (`DiskDrift.Tray`) | Your user, unprivileged | Reads the database read-only; shows the tray icon and dashboard; asks the Collector for "Probe now" over the pipe. |

The data folder is writable only by SYSTEM and Administrators, so a normal user can read the history but not rewrite
it. Design notes live in [PLAN.md](PLAN.md), the vocabulary in [GLOSSARY.md](GLOSSARY.md) and the decisions in
[docs/adr](docs/adr/).

## Install

Requirements: Windows 10 1809 or later (x64). Nothing else: the setup carries its own .NET runtime.

1. Download `DiskDrift-Setup-<version>.exe` from the [latest release](https://github.com/AnwarRezk/disk-monitor/releases/latest).
2. Run it and accept the administrator prompt. The setup isn't code-signed yet, so Windows SmartScreen may say
   "Windows protected your PC": choose **More info** → **Run anyway**.
3. Leave **Open the DiskDrift dashboard** ticked on the last page.

The setup:

1. installs the Collector to `%ProgramFiles%\DiskDrift\Collector` and registers the `DiskDrift` service (automatic start,
   restarts after a crash), then starts it; it creates `%ProgramData%\DiskDrift` with its locked-down ACL on first run;
2. installs the Tray to `%ProgramFiles%\DiskDrift\Tray`, starts it now and at every user's logon;
3. adds a **DiskDrift** shortcut with the logo to the Start menu (and the desktop, unless you untick it). It opens the
   dashboard, starting the tray icon first if it isn't running.

Run a newer setup to upgrade in place; your `appsettings.json` is kept. It also takes over an install made with the
old `install-service.ps1` script.

To remove DiskDrift, use **Settings → Apps → Installed apps → DiskDrift → Uninstall**. It asks whether to delete the
history and logs in `%ProgramData%\DiskDrift` (the default keeps them, so a reinstall continues the same timeline).

Check the service with `sc.exe query DiskDrift` (in PowerShell, write `sc.exe`: plain `sc` is `Set-Content`).

### Install from source

With the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), from an administrator PowerShell:

```powershell
git clone https://github.com/AnwarRezk/disk-monitor.git
cd disk-monitor
.\scripts\install-service.ps1          # same layout as the setup; -ResetConfig replaces appsettings.json
.\scripts\uninstall-service.ps1        # add -RemoveData to also delete the history and logs
```

To build the setup yourself, install [Inno Setup 6](https://jrsoftware.org/isinfo.php)
(`winget install JRSoftware.InnoSetup`) and run `.\installer\build.ps1 -Version 0.1.0`; it writes
`artifacts\installer\DiskDrift-Setup-0.1.0.exe`.

## Use

- **Hover** the tray icon for free space and the change over the last 24 hours.
- **Double-click** it, right-click → *Open dashboard*, or start **DiskDrift** from the Start menu or desktop.
- In the dashboard, drag across the chart to zoom (Esc resets), switch ranges at the top of the chart, and use the
  tabs: **Where space is** (Probe sizes), **Swings** (pick one for its detail) and **Top Writers**.
- Right-click the tray icon for *Probe now* and *Open data folder*.

## Configure

Settings live in `%ProgramFiles%\DiskDrift\Collector\appsettings.json`: volumes, sampling intervals, the Swing threshold,
retention, the tray colour thresholds and the Probe list. Per-user Probes use a `{user}` template that expands to every
profile under `C:\Users`. Restart the service after editing:

```powershell
sc.exe stop DiskDrift; sc.exe start DiskDrift
```

## Develop

```powershell
dotnet build                                     # whole solution; also builds on Linux/macOS
dotnet test                                      # xUnit tests
dotnet run --project src/DiskDrift.Collector     # Collector as a console app (elevated terminal)
dotnet run --project src/DiskDrift.Tray          # Tray (normal terminal)
.\scripts\simulate-growth.ps1 -SizeGB 2 -Location temp -Cleanup   # make a test Swing (admin)
```

`DiskDrift.Tray.exe --screenshot <folder>` saves the dashboard at every range and size, which is handy when reviewing UI
changes. Contributor notes are in [CLAUDE.md](CLAUDE.md).

## Privacy

The Collector measures every user profile's Probes, and the database is readable by all local users. That's fine on a
personal machine; keep it in mind on a shared one.

## License

[MIT](LICENSE) © 2026 Anwar Rezk
