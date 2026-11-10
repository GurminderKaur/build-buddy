-- Build Buddy: AI fix explanations with vector embeddings, in a Tiger Data hypertable
-- Run each step in the Tiger Console SQL editor, in order.

-- 1. Turn on pgvector
CREATE EXTENSION IF NOT EXISTS vector;

-- 2. One row per explanation a model gave for a compiler error
CREATE TABLE fix_notes (
    noted_at         TIMESTAMPTZ NOT NULL,
    source           TEXT        NOT NULL,  -- build-buddy run or challenge-2 benchmark
    model            TEXT        NOT NULL,
    error_code       TEXT        NOT NULL,
    compiler_message TEXT        NOT NULL,
    explanation      TEXT        NOT NULL,
    review           TEXT        NOT NULL CHECK (review IN ('correct', 'wrong', 'inconsistent')),
    embedding        VECTOR(768) NOT NULL   -- nomic-embed-text, generated locally with Ollama
);

-- 3. Make it a hypertable partitioned by time
SELECT create_hypertable('fix_notes', by_range('noted_at', INTERVAL '1 day'));

-- 4. Vector index for cosine distance (not needed for 12 rows, but it is what makes this scale)
CREATE INDEX fix_notes_embedding_idx ON fix_notes USING hnsw (embedding vector_cosine_ops);

-- 5. Paste and run db/out/fix-notes-insert.sql here

-- 6. Check the data
SELECT to_char(noted_at, 'YYYY-MM-DD') AS day, model, review, count(*) AS notes
FROM fix_notes
GROUP BY day, model, review
ORDER BY day, model, review;

-- 7. Then paste and run the queries from db/out/fix-notes-queries.sql
