-- MVP schema (PLAN.md §4). schema_version is created by the Migrator itself.
-- Timestamps: UTC Unix epoch milliseconds. Sizes: bytes. Never edit this file once applied.

CREATE TABLE volume_samples (
  ts          INTEGER NOT NULL,
  volume      TEXT    NOT NULL,              -- 'C:'
  total_bytes INTEGER NOT NULL,
  free_bytes  INTEGER NOT NULL
);
CREATE INDEX ix_volume_samples ON volume_samples(volume, ts);

CREATE TABLE processes (                     -- process identity, deduplicated
  id       INTEGER PRIMARY KEY,
  name     TEXT NOT NULL,
  exe_path TEXT,
  UNIQUE(name, exe_path)
);

CREATE TABLE process_io_samples (            -- Top Writers per interval
  ts                INTEGER NOT NULL,
  process_id        INTEGER NOT NULL REFERENCES processes(id),
  write_bytes_delta INTEGER NOT NULL,
  read_bytes_delta  INTEGER NOT NULL
);
CREATE INDEX ix_process_io_ts ON process_io_samples(ts);
CREATE INDEX ix_process_io_process ON process_io_samples(process_id);

CREATE TABLE probes (
  id     INTEGER PRIMARY KEY,
  name   TEXT NOT NULL,
  kind   TEXT NOT NULL CHECK (kind IN ('dir', 'file', 'vss', 'glob')),
  path   TEXT NOT NULL,                      -- expanded, absolute
  volume TEXT NOT NULL,
  UNIQUE(kind, path)
);

CREATE TABLE probe_samples (
  ts          INTEGER NOT NULL,
  probe_id    INTEGER NOT NULL REFERENCES probes(id),
  size_bytes  INTEGER NOT NULL,
  file_count  INTEGER,
  duration_ms INTEGER,
  error       TEXT
);
CREATE INDEX ix_probe_samples ON probe_samples(probe_id, ts);

CREATE TABLE swings (
  id                 INTEGER PRIMARY KEY,
  volume             TEXT    NOT NULL,
  kind               TEXT    NOT NULL CHECK (kind IN ('growth', 'release')),
  stage              TEXT    NOT NULL CHECK (stage IN ('open', 'closing', 'closed')),
  across_downtime    INTEGER NOT NULL DEFAULT 0,   -- 1 if measured across Downtime
  ts_start           INTEGER NOT NULL,
  ts_end             INTEGER,                      -- NULL while open
  probe_window_start INTEGER,                      -- actual bracketing Probe Sample times
  probe_window_end   INTEGER,
  used_delta_bytes   INTEGER,                      -- +growth / -release (= -delta free)
  attributed_bytes   INTEGER,
  unaccounted_bytes  INTEGER
);
CREATE INDEX ix_swings_volume_ts ON swings(volume, ts_start);

CREATE TABLE swing_items (
  swing_id    INTEGER NOT NULL REFERENCES swings(id),
  source      TEXT    NOT NULL CHECK (source IN ('probe', 'process', 'usn')),
  label       TEXT    NOT NULL,
  path        TEXT,                                -- used for "Open location"
  delta_bytes INTEGER NOT NULL,
  rank        INTEGER NOT NULL
);
CREATE INDEX ix_swing_items_swing ON swing_items(swing_id, source, rank);
