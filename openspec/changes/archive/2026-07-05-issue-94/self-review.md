# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: consistency
  Evidence: Verified all design code references against the actual source. `EpicGrain.ApplyPendingEvents` (no-op drain calling `ClearPendingEvents`) is at `EpicGrain.cs:601/609`; `EnsureNotTerminal` is at `Epic.Transitions.cs:154`; `EpicQuerier.ListAsync(projectId)` is at `EpicQuerier.cs:27`; the `EpicEvent` union has exactly the 7 "existing" variants T-001 claims to serialize (no `EpicReopened` yet — correctly deferred to T-002). No factual mismatches found; no repair needed.
  Verification: `rg` over `packages/server/src/Mohist.Server/Epic/**` confirmed every cited symbol, line, and variant count.
  Status: resolved

## Blocking Items

_None._

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: feasibility
  Evidence: T-005 (activity timeline read path + web component, priority 3) declares `dependsOn: ["T-001"]` only, yet its acceptance criterion requires rendering "reopen (distinct from a generic status change)". The `EpicReopened` event variant that distinguishes reopen is added to the union/serializer/catalog by T-002 (priority 2). T-005's own note acknowledges this ("Tests seed events directly, so T-002/T-003 are not hard dependencies"). The strict type-level dependency on the `EpicReopened` variant is satisfied in practice because priority ordering (3 > 2) guarantees T-002 lands before T-005, so this is not blocking.
  SuggestedAction: No change required for execution. If desired, the implementer may add `T-002` to T-005's `dependsOn` to make the variant-existence dependency explicit, but the priority-driven execution order already covers it.
  Status: follow-up

## Summary

- **alignment**: PASS — all four issue acceptance criteria (Reopen→Idle, batch link/unlink, list search/sort, event persistence + timeline) map 1:1 to the four proposal Capabilities and four spec folders. The issue's explicit Non-Goals (Done→Closed, hard delete) are respected by proposal and design.
- **completeness**: PASS — every requirement has spec scenarios; every spec has an owning task (T-001/T-005 ← activity-timeline, T-002 ← reopen, T-003 ← batch, T-004 ← list-query). Edge cases covered: partial-failure, idempotency, dedup, re-homed skip, terminal guard regression, injection-safe sort, empty timeline, fake-TimeProvider timestamps.
- **consistency**: PASS — spec anchors in `tasks.json` all resolve to real requirement headings; design decisions (D1–D6) align with specs; naming (`epic-*` capabilities, `EpicReopened`, `EpicNotTerminalException`, `:batch` routes) is uniform across artifacts.
- **feasibility**: PASS — no over-fragmentation: each task is a complete vertical slice (domain + grain + API + web where applicable), no standalone "define interface / register DI / add test" tasks, tests are embedded in each task's acceptance criteria. All mirrored assets (issue-events store, `Issue.Reopen`, `GetActiveMembershipOwnerAsync`) exist in the codebase.
- **dependency_completeness**: PASS — DAG is acyclic: T-001 (p1) and T-004 (p1) have no deps; T-002, T-003 (p2) depend on T-001; T-005 (p3) depends on T-001. Every `dependsOn` points to an existing ID with strictly lower priority.

<promise>PASS</promise>
