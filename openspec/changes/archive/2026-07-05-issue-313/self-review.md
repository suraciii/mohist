# Self Review Report

## Result: PASS

Reviewed against issue #313 (`拆分 runner SignalR 传输层`) and the current source in `packages/runner/src/server/runner-signalr.ts` (850 lines) plus its consumers. Every factual claim in the plan was cross-checked against the code:

- `normalizeMaterializePayload` + 6 helpers (`parseSetVars`/`parseOutputs`/`parseJsonObject`/`readString`/`readNullableString`/`readNullableNumber`) are exported/defined only in `runner-signalr.ts` with zero callers in `src` or `tests` — dead code confirmed.
- The five git parsers and the four uncovered handlers (`GetDiff`/`GetCommits`/`GetCommitDiff`/`GetFileContent`) have zero direct test coverage (`rg` over `packages/runner/tests` exits 1) — the test-first gap is real.
- The circular-import workaround `isPathUnder` lives at `runtime/workspace-registry.ts:271-279`; the cross-layer import `isUnderRunnerRoot` lives at `runtime/cleanup-loop.ts:1` — both confirmed.
- `resolveSessionTarget`, `resolveWorkspaceQuery`, `GetWorkspaceStatus` (fetch→rebase→ahead/behind ordering), and the cancel/followup handlers match their specs line-for-line.
- `runner-signalr.spec.ts` is 1241 lines; the `findHandler` + `setRunnerSignalRGitRunnerForTest`/`setRunnerSignalRExistsCheckerForTest` seam exists — the T-006 test-first approach is feasible against the current (pre-migration) code.

### Alignment

- Proposal addresses the actual issue: it decomposes `runner-signalr.ts` by concern, deletes the dead code, and preserves every SignalR contract byte-for-byte.
- Every "What Changes" entry traces to an issue acceptance criterion (dead-code deletion → AC1; parser/path/session-target/liveness extraction → AC2/AC3; test-first → AC4; handler clusters → AC5; contract preservation → AC5/AC6).
- No issue requirement is missing or misinterpreted. The issue body's method-name typo `CancelAgentAgent` was correctly normalized to `CancelAgentSession` across proposal/design/specs to match the code (`runner-signalr.ts:380`).

### Completeness

- All three capability areas have dedicated specs; every spec requirement is covered by at least one task (verified each `### Requirement:` heading maps to a task's acceptance criteria).
- All spec `spec` anchors resolve to real headings in the spec files (verified verbatim).
- Edge cases are captured: binary numstat lines, malformed parser input, legacy top-level field fallback, fire-and-forget non-await semantics, idempotent `active→eligible` transition, runner-root containment refusal, independent base/head resolution.

### Consistency

- Specs align with proposal Capabilities (`workspace-git-queries` / `runner-connection-liveness` / `runner-signalr-push-handlers` ↔ three spec dirs).
- Design D2 module placements match task outputs; `register*Handlers` naming is uniform across design D3 and tasks T-007/T-008.
- D5 contract checklist is reflected in each task's "字节级不变" acceptance criteria.

### Feasibility

- Each task is a complete, independently-green slice appropriate for a pure-refactor issue (extraction + import update + tests kept green). The strict serial chain is justified by design D1 (every phase touches `runner-signalr.ts`; serializing avoids conflicts and enforces per-phase green).
- T-006 is a standalone `TEST` task, which the granularity guidance flags as a smell. Here it is a deliberate, issue-mandated test-first *gate* (AC4 "测试先行"): it pins the four uncovered handlers' current behavior via the existing `findHandler` seam *before* T-007 migrates them, so it cannot be folded into T-007 without losing gate semantics. T-002 and T-005 already inline their own tests, so the plan only splits a test task where the issue forces it.

### Dependency completeness

- T-001 has empty `dependsOn`; T-002…T-008 each declare `dependsOn` against existing IDs.
- All `dependsOn` targets have strictly lower `priority` (T-007→{T-002,T-003,T-006}; T-008→{T-003,T-004,T-007}); no cycles; the DAG is consistent with the functional needs (git handlers need parsers + workspace-query; push handlers need workspace-query + session-target; both edit `runner-signalr.ts` so they serialize).

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: consistency
  Evidence: `proposal.md:9` cited the circular-import workaround as `runner-signalr.ts:271-279`, but the duplicate `isPathUnder` actually lives at `runtime/workspace-registry.ts:271-279` (verified against source; `design.md:12` already cites the correct file/path). `runner-signalr.ts:271-279` is unrelated `GetCommits` handler code. Changed the parenthetical to `runtime/workspace-registry.ts:271-279`.
  Verification: Re-read `runtime/workspace-registry.ts:271-279` (the `isPathUnder` workaround comment + function) and confirmed it matches the now-cited location; `design.md:12` and `proposal.md` Impact section now agree.
  Status: resolved

## Blocking Items

None.

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: completeness
  Evidence: Design D4/P6 allude to a direct unit-test file `workspace-git-handlers.spec.ts` ("新增的直接单元测试…直接从归属模块导入"), but no task explicitly delivers it. T-007 instead relies on the D4 re-export strategy so the T-006 `findHandler`-based tests keep passing. Coverage is preserved, so this is non-blocking.
  SuggestedAction: Optionally add a `workspace-git-handlers.spec.ts` direct-unit-test deliverable to T-007 (or a follow-up) once the handlers are free functions, to reduce reliance on the 1241-line `runner-signalr.spec.ts` seam.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: feasibility
  Evidence: T-006 is a standalone `TEST` task. It is justified by the issue's hard "测试先行" gate and cannot merge into T-007 without losing gate semantics, but it is the one place the plan diverges from the "no separate test tasks" granularity guidance.
  SuggestedAction: None required for this issue; if the test-first gate were ever dropped, T-006's cases should be folded into the corresponding implementation task.
  Status: follow-up

<promise>PASS</promise>
