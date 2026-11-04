# Collector runs as a LocalSystem service, separate from the tray UI

DiskMon is split into an elevated Collector, which runs as a Windows Service under LocalSystem and is the only writer of data, and an unprivileged Tray app. We considered a single tray app launched elevated at logon by Task Scheduler. It would have removed the UI-to-Collector IPC and the problem of SYSTEM resolving per-user paths to the wrong profile. We rejected it because it records nothing while no one is logged on, and overnight Windows Update, VSS and cleanup work is a prime suspect for the large Release Swings DiskMon exists to explain.

## Consequences

- Per-user Probe paths must be expanded explicitly over `C:\Users\*`, because environment variables resolve to the system profile.
- The Tray needs a deliberate channel to ask the Collector for things such as "probe now".
