# Self-Review — issue-437 (otel.db 自动轮转) — re-review after fixes

Second pass. The first review reported FAIL with two blocking (B-1, B-2) and six
non-blocking (B-3…B-8) findings. This pass verifies each was resolved, then sweeps
for anything newly introduced or previously missed.

## Prior findings — resolution check

| # | Finding | Status | Evidence |
|---|---|---|---|
| B-1 | Persisted-watermark contradiction (proposal/spec/issue say persisted; design said not) | Resolved | D6 now persists a minimal `.meta` sidecar (via `IFileSystem`, not the business DB, not inside `otel.db`), seeds admission from it, and falls back conservative if missing/corrupt. Spec `otel-storage-budget` and proposal Impact now match (persisted marker + conservative fallback). All four agree. |
| B-2 | Design D1 wrongly claimed `idx_traces_start` serves the `end_time` filter; no `end_time` index exists | Resolved | D1 corrected: states no `end_time` index exists today and adds `idx_traces_end ON traces(end_time)` as an additive, non-breaking extension. Reflected in risks, migration step 2, proposal schema-contract bullet, and T-001 (description + an AC that deletion is index-served with batch-proportional cost). Verified against `OtelDb.cs:80-87`. |
| B-3 | "Internal write block" undefined/unbounded until #471 | Resolved | Defined in `otel-storage-budget` (in-flight write bounded by the OTLP request body limit until #471, then the bounded chunk) and D2. |
| B-4 | Rebuild ignored concurrent readers | Resolved | D5 adds the reader-fail paragraph; `otel-storage-recovery` adds the "query overlaps rebuild" scenario; T-003 adds an AC. |
| B-5 | Data-reset degradation source/code under-specified | Resolved | D5 + migration step 3 + T-003 specify `storage_data_reset` on the `StorageWrite` source, cleared by the first committed production write (same lifecycle as `storage_unverified`). |
| B-6 | "Safe budget" threshold undefined | Resolved | Defined as 100% of `StorageBudgetBytes` (strictly above the 90% high watermark) in `otel-storage-recovery`, D5, and T-003. |
| B-7 | `incremental_vacuum` efficacy asserted not verified | Resolved | D7 states the accepted limitation explicitly (command-issuance + `auto_vacuum=INCREMENTAL` at init, not observed shrinkage; strongest verification under the no-real-FS constraint). |
| B-8 | Cross-thread reason dominance not deterministic | Resolved | D3 + risks state `storage_budget_exhausted` is published/cleared under the shared state lock so it deterministically dominates `latest_degradation` while admission is closed. |

No regression from the fixes: `tasks.json` is still valid JSON with an intact DAG
(T-001 → T-002 → T-003), all `passes=false`, every task has test-backed acceptance
criteria; all four specs retain valid 4-hashtag scenario formatting with a WHEN/THEN
per scenario and ≥1 scenario per requirement.

## Acceptance-criteria coverage (unchanged, complete)

| Issue AC | Covered by |
|---|---|
| 1. 72h bounded-batch / Span-sync / interruptible | `otel-trace-retention` (D1, T-001) |
| 2. db+WAL+SHM high watermark / ≤1GiB + one block | `otel-storage-budget` (D2, T-002) |
| 3. checkpoint blocked / can't reclaim → stop writes + reject count + reason | `otel-ingest-admission` (D3, T-002) |
| 4. free-page reuse / WAL hard boundary / no full VACUUM | `otel-storage-budget` (D2, T-002) |
| 5. no cleanup when off; resume from persisted watermark | `otel-storage-budget` + `otel-trace-retention` (D6, T-002) |
| 6. oversized-DB upgrade path / rebuild / log / reason | `otel-storage-recovery` (D5, T-003) |
| 7. test coverage (time, size, WAL/SHM, blocked reader, restart, complete Trace, injectable clock) | scenarios across all four specs + D7 |

## New observations (non-blocking)

These do not block building; the behavior is correct regardless and a builder
resolves them during implementation.

- **N-1. Size-eviction "removable" proxy is nearly vacuous.** D1's "removable excludes
  traces with `end_time >= now`" rarely triggers, because Span timestamps record when a
  Span ended (in the past relative to export arrival), so `end_time < now` for essentially
  all real telemetry. In practice size eviction treats all traces as removable and evicts
  oldest-by-`start_time`. This is correct and consistent with the issue's non-goal (space
  preempts full 72h coverage); the real "cannot reclaim" triggers are the blocked
  checkpoint and the cannot-shrink (pre-`auto_vacuum`) file, both covered. Prose precision
  only — a builder may treat "no removable trace" as "store empty or cannot shrink".
- **N-2. First-tick rebuild-vs-eviction ordering is not explicit.** D1 lists the normal
  tick order (time → size → reclaim → arbitrate); D5 says rebuild runs "on the first tick"
  without stating it precedes eviction. Ordering is correctness-neutral (rebuild dominates
  and the store self-corrects to a fresh empty DB either way), but stating rebuild-first on
  the first tick would avoid a small amount of wasted eviction work on a doomed oversized
  database.

## Verdict

All prior blocking and non-blocking findings are resolved without introducing new
inconsistencies; the plan is internally consistent, technically sound, every spec
requirement has design support and task coverage with test verification, and the two new
observations are non-blocking clarity notes. The plan is ready to build.

<promise>PASS</promise>
