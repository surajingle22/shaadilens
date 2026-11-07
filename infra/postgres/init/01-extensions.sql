-- Runs once when the postgres data volume is first created.
-- pgvector adds the `vector(n)` column type and the <=> (cosine distance) operator
-- used to compare 512-d face embeddings.
CREATE EXTENSION IF NOT EXISTS vector;
