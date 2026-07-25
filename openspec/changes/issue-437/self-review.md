# Self-Review — issue-437 (otel.db 自动轮转)

Reviewer mode. Artifacts reviewed against the issue body: `proposal.md`, `specs/*`,
`design.md`, `tasks.json`. Coverage of the issue's seven acceptance criteria is complete
(see matrix below); the problems below are about internal consistency and technical
soundness, not missing scope.

## Acceptance-criteria coverage

| Issue AC | Covered by |
|---|---|
| 1. 72h 有界批次删除 / Span 同步 / 可中断续跑 | `otel-trace-retention` |
| 2. db+WAL+SHM 高水位 / ≤1GiB + 一个写入块 | `otel-storage-budget` (see B-3 on "写入块" definition) |
| 3. checkpoint 阻塞或无法回收 → 停写 / 拒绝计数 / 降级原因 | `otel-ingest-admission` |
| 4. 空闲页复用 / WAL 硬边界 / 不做 full VACUUM | `otel-storage-budget` (D2) |
| 5. 关闭观测不清理；重启/重启用安全继续 | `otel-storage-budget` + `otel-trace-retention` (see B-1 on "持久化") |
| 6. 超大库升级路径 / 重建 / 日志 / 状态原因 | `otel-storage-recovery` |
| 7. 测试覆盖（时间、空间、WAL/SHM、阻塞 reader、重启、完整 Trace、可注入时钟） | scenarios across all four specs (see B-6 on incremental_vacuum efficacy) |

## Blocking findings

### B-1. Design D6 unilaterally reverses a "persisted watermark" decision stated in proposal, spec, AND issue

The issue AC5 says *"重新启用后从持久化水位安全继续"*. The proposal (Impact) says
*"持久化水位放本地元数据"*. The `otel-storage-budget` spec says reclamation state
*"SHALL be recovered ... from ... persisted reclamation state"*. Design D6 explicitly
chooses **not** to persist anything and to re-derive from the probe with a conservative
admission-closed-until-first-probe rule.

D6's argument (re-derivation is a *stronger* safety guarantee than a persisted marker)
is reasonable on its merits, but it contradicts a SHALL in the spec, a statement in the
proposal, and the wording of the issue. A builder following the spec will persist a
marker; a builder following the design will not. This must be reconciled before build:
either (a) soften the spec's "persisted" language to "re-derived/conservative" and note
the deviation from the issue wording, or (b) add a minimal persisted marker in the design
(local metadata, not the business DB). As written, the plan contradicts itself on a
normative requirement.

### B-2. Design D1's index claim is factually wrong and yields a spec violation

D1 states the time-eviction cutoff *"is a lexicographic comparison served by the existing
`idx_traces_start` index."* It is not. `OtelDb.cs:80-87` defines only:

- `idx_traces_service_start ON traces(service_name, start_time DESC)`
- `idx_traces_start ON traces(start_time DESC)`
- `idx_spans_trace ON spans(trace_id)`

There is **no index on `end_time`**, and the retention deletion filters on `end_time`
(the spec correctly mandates `end_time` so a still-growing Trace is not aged out). With
no supporting index, the batched `DELETE ... WHERE end_time < $cutoff ORDER BY start_time`
must scan and filter the `traces` table, so per-tick cost scales with the number of traces
— a direct violation of the `otel-trace-retention` requirement that *"Per-pass database
statement count ... does not grow with unrelated history"* and of `design/testing.md`'s
cost-independence constraint. D1 must be corrected to add an index that serves `end_time`
(e.g. `idx_traces_end ON traces(end_time)` or a composite), and the schema-contract note
updated (a new index is an extension, not a rename; CLI readers do not depend on indexes,
so this is non-breaking). Without this, the plan as written leads a builder to ship a
cost regression.

## Non-blocking findings

### B-3. "一个内部写入块" is undefined/unbounded until #471

`otel-storage-budget` promises the store *"SHALL NOT grow beyond the budget plus a single
internal write block, where an internal write block is one bounded ingest transaction."*
No ingest transaction is bounded today (write chunking is #471, explicitly out of scope).
D2 admits the dependency, but the spec states the bound as a hard SHALL with no definition
of the block size. Either define the block against the current request-body limit
explicitly, or mark this bound as best-effort pending #471. Affects testability of AC2.

### B-4. Rebuild does not address concurrent readers (D5)

D5 handles ingest during rebuild via admission-closed, but `/otel/api/query` opens
read-only connections that may be live when the rebuild deletes the file. The design
should state that queries during the (startup-time) rebuild fail or are best-effort, or
that the rebuild excludes readers. Low real-world impact at startup, but the gap should
be explicit.

### B-5. Data-reset degradation source/code is under-specified

D5 says the data-reset reason is published *"on a source consistent with the existing
contract"* but does not name which of the five sources (`Collector`/`ProcessRead`/
`StorageRead`/`StorageWrite`/`IngestProtection`) or the code string. `storage_budget_exhausted`
(D3) is cleanly mapped to `IngestProtection`; the data-reset reason (T-003) is not. The
builder needs the source + code. (T-002's `storage_budget_exhausted` is fine.)

### B-6. "Safe budget" threshold for recovery is not numerically defined in the spec

`otel-storage-recovery` says *"exceeds the safe budget"* without a value; D5 defines it as
100% of `StorageBudgetBytes` (vs. the 90% eviction high watermark). The spec should state
the threshold crisply so the 90%-vs-100% boundary between eviction and rebuild is
unambiguous.

### B-7. incremental_vacuum efficacy is asserted, not verified

Per the no-real-filesystem constraint, the central claim (delete → `incremental_vacuum` →
file shrinks) is tested only by asserting the command is issued, not by observing
shrinkage. Acceptable given the constraint, but it should be acknowledged as an accepted
limitation rather than implied as verified. The risks section partially covers it.

### B-8. Degradation-reason dominance is cross-thread (D3)

`storage_budget_exhausted` is published on the maintenance thread; `TelemetryRejected`
flows from `RecordIngest` on the request thread; both target `IngestProtection`. D3's
"publish after the ingest outcome so the more specific reason dominates" is not
deterministic across threads. Minor — the builder should sequence the specific publication
under the existing state lock.

## Notes

- Spec format is sound: every requirement has ≥1 scenario, scenarios use exactly four
  hashtags, every WHEN has a THEN, no delta headers, SHALL/MUST used normatively.
- `tasks.json` is valid JSON, acyclic DAG, every `dependsOn` points to a strictly lower
  priority, every task has acceptance criteria with test verification, all `passes=false`.
- T-002 deliberately merges `otel-storage-budget` + `otel-ingest-admission` (shared guard
  = interface/state; budget-aware decision = call-site switchover); this matches the
  "merge interface + impl + call-site" split principle. Its `spec` field references only
  the storage-budget spec; acceptable since the notes and criteria cover admission, but
  could reference both.

## Verdict

Two blocking findings — a self-contradiction on a normative ("persisted") requirement
(B-1) and a factual design error that produces a spec violation (B-2) — must be fixed
before build. The non-blocking findings should be addressed in the same pass but do not
alone block.

<promise>FAIL</promise>
