# Space findings

This page records what diskdrift has found on real machines, and how to get the space back. Add new findings at the top. When a finding becomes a feature, such as a hint in the dashboard or a cleanup rule (M5), link its ticket here.

## 2026-11-04: WSL and Docker virtual disks (author's PC)

The first Probe runs (DIS-12) totalled about 179 GB. The largest Probes were:

| Probe | Size |
|---|---|
| WSL disks (`…\Packages\*CanonicalGroup*\LocalState\ext4.vhdx`) | 74.5 GB |
| Docker (`…\AppData\Local\Docker`, mostly `wsl\disk\docker_data.vhdx`) | 70.9 GB |
| Hibernation file | 6.3 GB |
| Windows Installer cache, Chrome, npm cache, Edge, pnpm store | 1.5 to 2.3 GB each |

**Why:** a `.vhdx` grows when the Linux side writes data. It does not shrink when that data is deleted, so free space on C: falls slowly and never comes back. This shows up as steady growth, not as Swings (PLAN §8).

**Getting the space back** (close Docker Desktop and run `wsl --shutdown` first):

- Docker: run `docker system prune -a` (add `--volumes` only if you don't need its volumes), then use Docker Desktop's *Troubleshoot → Clean / Purge data*. Newer Docker Desktop versions shrink the disk on their own once the data is gone.
- WSL distro: run `wsl --manage <distro> --set-sparse true` once, so the disk gives freed space back from then on. On Hyper-V editions, you can also compact a disk directly with `Optimize-VHD -Path <file.vhdx> -Mode Full`.
- npm and pnpm caches: `npm cache clean --force`, `pnpm store prune`.

**Note for diskdrift:** Probe sizes are logical sizes. A sparse `.vhdx` can report more than it takes on disk.
