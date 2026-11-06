# Code signing policy

Free code signing is provided by [SignPath.io](https://about.signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).

## Roles

- **Committers and reviewers:** [Anwar Rezk](https://github.com/AnwarRezk)
- **Approvers (releases):** [Anwar Rezk](https://github.com/AnwarRezk)

## Signed artifacts

Release builds in [`.github/workflows/ci.yml`](.github/workflows/ci.yml) sign:

- `DiskDrift.Collector.exe` (Windows Service collector)
- `DiskDrift.Tray.exe` (system tray and dashboard application)
- `DiskDrift-Setup-<version>.exe` (Inno Setup installer; local builds also support `SignedUninstaller=yes` via `installer/build.ps1 -SignCommand`)

## Privacy policy

This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it.

DiskDrift runs entirely on the local machine, makes no network requests, and stores its measurements locally in `%ProgramData%\DiskDrift\diskdrift.db`. Per-user Probes are summed across profiles under `{user}` template paths so local usernames are not recorded in the database.

