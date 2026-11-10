# Vector search over AI fix explanations: results

Tiger Data hypertable `fix_notes` with pgvector. 12 real explanations that models gave for C# compiler errors (Build Buddy run on Nov 9/10 UTC, plus the Challenge 2 benchmark answers), embedded locally with `nomic-embed-text` (768 dimensions) through Ollama. Each explanation has a manual review: `correct`, `wrong`, or `inconsistent`.

Files: [fix-notes-data.json](fix-notes-data.json) (data, queries), [embed-fix-notes.ps1](embed-fix-notes.ps1) (embeddings to SQL), [fix-notes-schema.sql](fix-notes-schema.sql) (table, hypertable, HNSW index).

Distance is pgvector cosine distance (`<=>`): lower means closer in meaning. The full rankings below were run with `SET enable_indexscan = off;` so every row is compared exactly.

## Summary

| Query | Top match | Distance | Next best |
|---|---|---|---|
| **Q1** "A statement is missing its semicolon at the end of the line." | CS1002, Gemma, **wrong** | **0.231** | 0.318 |
| **Q1-reviewed** (same, `review = 'correct'`) | CS0161 (unrelated) | 0.318 | No relevant match left |
| **Q2** "An async call returns a Task and is assigned to a string without await." | CS0029, Grok 4.7, correct | **0.245** | 0.338 |
| **Q2-since-nov-9** (same, `noted_at >= '2026-11-09'`) | CS0161 (unrelated) | 0.338 | The Nov 8 match is filtered out |
| **Q3** "A method does not return a value on every code path." | CS0161, Gemma, correct | **0.186** | 0.214 (correct), 0.261 (**wrong**) |

## Full ranking: Q1 (missing semicolon)

| # | Day | Model | Code | Review | Distance |
|---|---|---|---|---|---|
| 1 | 2026-11-10 | gemma3:4b | CS1002 | wrong | 0.231 |
| 2 | 2026-11-08 | grok-4.7 | CS0161 | correct | 0.318 |
| 3 | 2026-11-10 | gemma3:4b | CS0161 | correct | 0.331 |
| 4 | 2026-11-09 | gemma3:4b | CS0161 | wrong | 0.333 |
| 5 | 2026-11-10 | gemma3:4b | CS0103 | correct | 0.352 |
| 6 | 2026-11-10 | gemma3:4b | CS1061 | wrong | 0.362 |
| 7 | 2026-11-09 | gemma3:4b | CS1061 | correct | 0.375 |
| 8 | 2026-11-08 | grok-4.7 | CS1061 | correct | 0.381 |
| 9 | 2026-11-09 | gemma3:4b | none | wrong | 0.392 |
| 10 | 2026-11-10 | gemma3:4b | CS0246 | correct | 0.407 |
| 11 | 2026-11-10 | gemma3:4b | CS0029 | inconsistent | 0.412 |
| 12 | 2026-11-08 | grok-4.7 | CS0029 | correct | 0.414 |

## Full ranking: Q2 (async without await)

| # | Day | Model | Code | Review | Distance |
|---|---|---|---|---|---|
| 1 | 2026-11-08 | grok-4.7 | CS0029 | correct | 0.245 |
| 2 | 2026-11-10 | gemma3:4b | CS0161 | correct | 0.338 |
| 3 | 2026-11-10 | gemma3:4b | CS1002 | wrong | 0.341 |
| 4 | 2026-11-10 | gemma3:4b | CS1061 | wrong | 0.362 |
| 5 | 2026-11-08 | grok-4.7 | CS0161 | correct | 0.368 |
| 6 | 2026-11-10 | gemma3:4b | CS0103 | correct | 0.374 |
| 7 | 2026-11-10 | gemma3:4b | CS0029 | inconsistent | 0.388 |
| 8 | 2026-11-09 | gemma3:4b | CS1061 | correct | 0.396 |
| 9 | 2026-11-09 | gemma3:4b | CS0161 | wrong | 0.399 |
| 10 | 2026-11-10 | gemma3:4b | CS0246 | correct | 0.399 |
| 11 | 2026-11-08 | grok-4.7 | CS1061 | correct | 0.405 |
| 12 | 2026-11-09 | gemma3:4b | none | wrong | 0.420 |

## What this shows

1. **Real matches stand out.** Every true match scored 0.186–0.261; everything unrelated scored 0.318 or more. A **distance threshold of about 0.30** separates them in this data.
2. **Filters can leave nothing relevant, and search still returns something.** Q1-reviewed and Q2-since-nov-9 only had unrelated results (0.318+). Without a threshold, a RAG system would answer from them anyway.
3. **A threshold alone does not stop wrong answers.** Q1's top match was a **wrong** explanation at 0.231, and Q3's third match was wrong at 0.261. Both pass the threshold.
4. **So retrieval for RAG needs both:** trusted sources only (`review = 'correct'`) **and** a relevance threshold, with "no trusted answer" when nothing passes.
5. **Meaning beats error codes.** For the async question, Grok's CS0029 (Task assigned to string) ranked #1, while Gemma's CS0029 (string assigned to int) ranked only #7. Same code, different cause, and the search told them apart.

## Limitations
- 12 notes, 5 queries, one embedding model. This is a demonstration, not a benchmark.
- Reviews are my own manual judgement.
- Challenge 2 timestamps are approximate (when each answer was recorded).
- The ~0.30 threshold fits this data only; it would need tuning on a larger set.
