-- Aggregate per-user Probes and process paths under {user} so local usernames are never stored in diskdrift.db (DIS-19).
-- Never edit this file once applied.

CREATE TEMP TABLE _user_probes AS
WITH raw AS (
  SELECT id AS old_id, name, kind, path, volume,
         substr(path, 10, instr(substr(path, 10), '\') - 1) AS profile,
         '{user}' || substr(substr(path, 10), instr(substr(path, 10), '\')) AS template_path
  FROM probes
  WHERE lower(substr(path, 2, 8)) = ':\users\'
    AND instr(substr(path, 10), '\') > 1
)
SELECT old_id,
        CASE
          WHEN lower(name) LIKE '% (' || lower(profile) || ')'
          THEN substr(name, 1, length(name) - length(profile) - 3)
          ELSE name
        END AS base_name,
        profile,
        kind,
        template_path,
        volume
FROM raw
WHERE lower(profile) NOT IN ('default', 'default user', 'public', 'all users');

INSERT INTO probes (name, kind, path, volume)
SELECT MIN(base_name), kind, template_path, MIN(volume)
FROM _user_probes
GROUP BY kind, template_path
HAVING 1 = 1
ON CONFLICT (kind, path) DO UPDATE SET name = excluded.name, volume = excluded.volume;

CREATE TEMP TABLE _probe_sample_sources AS
SELECT u.old_id AS source_id, target.id AS target_id, u.profile AS profile
FROM _user_probes u
JOIN probes target ON target.kind = u.kind AND target.path = u.template_path
UNION
SELECT DISTINCT target.id AS source_id, target.id AS target_id, '' AS profile
FROM _user_probes u
JOIN probes target ON target.kind = u.kind AND target.path = u.template_path;

CREATE TEMP TABLE _merged_probe_samples AS
SELECT src.target_id AS probe_id,
       s.ts AS ts,
       SUM(s.size_bytes) AS size_bytes,
       CASE WHEN COUNT(s.file_count) > 0 THEN SUM(s.file_count) END AS file_count,
       MAX(s.duration_ms) AS duration_ms,
       MAX(CASE
         WHEN s.error IS NOT NULL AND src.profile <> ''
         THEN replace(
           replace(s.error, substr(u_probe.path, 1, 9 + length(src.profile)), '{user}'),
           lower(substr(u_probe.path, 1, 9 + length(src.profile))), '{user}'
         )
         ELSE s.error
       END) AS error
FROM _probe_sample_sources src
JOIN probe_samples s ON s.probe_id = src.source_id
LEFT JOIN probes u_probe ON u_probe.id = src.source_id
GROUP BY src.target_id, s.ts;

DELETE FROM probe_samples WHERE probe_id IN (SELECT source_id FROM _probe_sample_sources);
INSERT INTO probe_samples (ts, probe_id, size_bytes, file_count, duration_ms, error)
SELECT ts, probe_id, size_bytes, file_count, duration_ms, error FROM _merged_probe_samples;
DELETE FROM probes WHERE id IN (SELECT old_id FROM _user_probes);

DROP TABLE _merged_probe_samples;
DROP TABLE _probe_sample_sources;
DROP TABLE _user_probes;

CREATE TEMP TABLE _user_processes AS
WITH raw AS (
  SELECT id AS old_id, name, exe_path,
         substr(exe_path, 10, instr(substr(exe_path, 10), '\') - 1) AS profile,
         '{user}' || substr(substr(exe_path, 10), instr(substr(exe_path, 10), '\')) AS template_path
  FROM processes
  WHERE exe_path IS NOT NULL
    AND lower(substr(exe_path, 2, 8)) = ':\users\'
    AND instr(substr(exe_path, 10), '\') > 1
)
SELECT old_id, name, template_path
FROM raw
WHERE lower(profile) NOT IN ('default', 'default user', 'public', 'all users');

INSERT OR IGNORE INTO processes (name, exe_path)
SELECT DISTINCT name, template_path FROM _user_processes;

UPDATE process_io_samples
SET process_id = (
  SELECT p.id FROM _user_processes u
  JOIN processes p ON p.name = u.name AND p.exe_path = u.template_path
  WHERE u.old_id = process_io_samples.process_id
)
WHERE process_id IN (SELECT old_id FROM _user_processes);

CREATE TEMP TABLE _merged_process_io_samples AS
SELECT ts, process_id,
       SUM(write_bytes_delta) AS write_bytes_delta,
       SUM(read_bytes_delta) AS read_bytes_delta
FROM process_io_samples
GROUP BY ts, process_id;

DELETE FROM process_io_samples;
INSERT INTO process_io_samples (ts, process_id, write_bytes_delta, read_bytes_delta)
SELECT ts, process_id, write_bytes_delta, read_bytes_delta FROM _merged_process_io_samples;
DROP TABLE _merged_process_io_samples;

DELETE FROM processes WHERE id IN (SELECT old_id FROM _user_processes);
DROP TABLE _user_processes;

UPDATE swing_items
SET label = CASE
      WHEN lower(label) LIKE '% (' || lower(substr(path, 10, instr(substr(path, 10), '\') - 1)) || ')'
      THEN substr(label, 1, length(label) - length(substr(path, 10, instr(substr(path, 10), '\') - 1)) - 3)
      ELSE label
    END,
    path = '{user}' || substr(substr(path, 10), instr(substr(path, 10), '\'))
WHERE path IS NOT NULL
  AND lower(substr(path, 2, 8)) = ':\users\'
  AND instr(substr(path, 10), '\') > 1
  AND lower(substr(path, 10, instr(substr(path, 10), '\') - 1)) NOT IN ('default', 'default user', 'public', 'all users');

CREATE TEMP TABLE _merged_swing_items AS
SELECT swing_id, source, label, path, SUM(delta_bytes) AS delta_bytes
FROM swing_items
GROUP BY swing_id, source, label, path;

DELETE FROM swing_items;
INSERT INTO swing_items (swing_id, source, label, path, delta_bytes, rank)
SELECT swing_id, source, label, path, delta_bytes,
       ROW_NUMBER() OVER (PARTITION BY swing_id, source ORDER BY abs(delta_bytes) DESC, label ASC)
FROM _merged_swing_items;

DROP TABLE _merged_swing_items;
