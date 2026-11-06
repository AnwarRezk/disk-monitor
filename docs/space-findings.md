# Space findings

This page records common patterns DiskDrift surfaces on Windows machines, and how to get the space back. Add new findings at the top. When a finding becomes a feature, such as a hint in the dashboard or a cleanup rule (M5), link its ticket here.

## WSL and Docker virtual disks

Surfaced in the dashboard as the virtual-disk reclaim callout (DIS-17) whenever WSL or Docker `.vhdx` Probes are large (≥ 10 GB) or growing (≥ 500 MB over the selected range).

| Probe | Typical path |
|---|---|
| WSL disks | `{user}\AppData\Local\Packages\*CanonicalGroup*\LocalState\ext4.vhdx` |
| Docker | `{user}\AppData\Local\Docker` (mostly `wsl\disk\docker_data.vhdx`) |

**Why:** a `.vhdx` grows when the Linux side or containers write data. It does not shrink when that data is deleted, so free space on C: falls slowly and never comes back on its own. This shows up as steady growth rather than Swings (PLAN §8).

**Getting the space back** (close Docker Desktop and run `wsl --shutdown` first):

- Docker: run `docker system prune -a` (add `--volumes` only if you don't need its volumes), then use Docker Desktop's *Troubleshoot → Clean / Purge data*. Newer Docker Desktop versions shrink the disk on their own once the data is gone.
- WSL distro: run `wsl --manage <distro> --set-sparse true` once, so the disk gives freed space back from then on. On Hyper-V editions, you can also compact a disk directly with `Optimize-VHD -Path <file.vhdx> -Mode Full`.
- npm and pnpm caches: `npm cache clean --force`, `pnpm store prune`.

**Note for DiskDrift:** Probe sizes are logical sizes. A sparse `.vhdx` can report more than it takes on disk.
