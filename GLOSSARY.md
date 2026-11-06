# DiskDrift

DiskDrift explains why free space on a Windows machine moves: when it changed, what grew or shrank, and which processes were writing.

## Measurement

**Volume**:
A fixed drive identified by its letter, such as `C:`. C: is the default and the priority; other fixed drives are opt-in.
_Avoid_: Disk, drive, partition

**Volume Sample**:
The total and free bytes of one Volume at one moment.
_Avoid_: Reading, snapshot

**Probe**:
A watched location whose size DiskDrift tracks over time: a folder, a file, a glob, or a Volume's VSS shadow storage.
_Avoid_: Watch, suspect, monitor

**Probe Sample**:
The measured size of one Probe at one moment.

**Top Writer**:
A program ranked by how many bytes its processes wrote over a period, summed over all of its running copies. A ranking signal, not a count of bytes on disk.
_Avoid_: Culprit, disk writer

## Swings

**Swing**:
A move of at least 1 GB in a Volume's free space within 15 minutes. Slow, steady growth is not a Swing.
_Avoid_: Event, spike, incident

**Growth Swing**:
A Swing in which used space increases (free space drops).

**Release Swing**:
A Swing in which used space decreases (free space rises).
_Avoid_: Cleanup, free-up

**Open / Closing / Closed**:
The stages of a Swing. Open: free space is still moving. Closing: the move has settled and DiskDrift is waiting for the after-side Probe Samples. Closed: Attribution is final.

**Downtime**:
A stretch with no Volume Samples longer than two sample intervals, such as a reboot, sleep, or the Collector being stopped. A Swing measured across Downtime is flagged as such.
_Avoid_: Outage, gap

**Probe Window**:
The pair of Probe Sample times that bracket a Swing, the last one at or before its start and the first one at or after its end. It is usually wider than the Swing itself.

**Attribution**:
The explanation of a Swing: the Probes whose size changed across its Probe Window, plus its Top Writers.

**Unaccounted**:
The part of a Swing's change in used space that no Probe explains.
_Avoid_: Unknown, other, missing
