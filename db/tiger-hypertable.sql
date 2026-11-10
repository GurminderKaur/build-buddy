-- Build Buddy benchmark results in a Tiger Data hypertable
-- Run each step in the Tiger Console SQL editor, in order.

-- 1. A standard PostgreSQL table for Build Buddy's results
CREATE TABLE build_results (
    run_at       TIMESTAMPTZ  NOT NULL,
    model        TEXT         NOT NULL,
    sample       TEXT         NOT NULL,
    error_code   TEXT         NOT NULL,
    build_passes BOOLEAN      NOT NULL,
    fixed        BOOLEAN      NOT NULL,
    seconds      NUMERIC(6,1) NOT NULL
);

-- 2. Convert it into a hypertable, partitioned by run time in 1-day chunks
SELECT create_hypertable('build_results', by_range('run_at', INTERVAL '1 day'));

-- 3. Confirm it is a hypertable
SELECT hypertable_name, num_dimensions, num_chunks
FROM timescaledb_information.hypertables;

-- 4. Paste the INSERT statement from insert.sql here and run it

-- 5. Chunks created by the import (one per day of data)
SELECT show_chunks('build_results');

-- 6. Fix rate and speed per model, per hour of benchmarking
SELECT time_bucket(INTERVAL '1 hour', run_at) AS hour,
       model,
       count(*)                               AS errors_tried,
       round(100.0 * avg(fixed::int), 1)      AS fix_rate_pct,
       round(avg(seconds), 1)                 AS avg_seconds
FROM build_results
GROUP BY hour, model
ORDER BY hour, model;

-- 7. Which error types take the model longest?
SELECT error_code,
       count(*)                AS runs,
       round(avg(seconds), 1)  AS avg_seconds,
       min(seconds)            AS fastest,
       max(seconds)            AS slowest
FROM build_results
GROUP BY error_code
ORDER BY avg_seconds DESC;