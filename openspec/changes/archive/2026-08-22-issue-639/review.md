# Review: issue-639

## Verdict

PASS — no must-fix problems remain; the change is ready to merge.

## Re-review disposition

- **Previous MF-1 — valid empty Workflow responses were rejected by `ServerConnection`: fixed and remains fixed.** `packages/runner/src/server/connection.ts:501-525` now preserves a valid HTTP 2xx empty array while still rejecting non-empty receipt-count mismatches. The cleanup route and adapter also preserve `[]`, so both ordinary Workflow input and cleanup can reach the outbox's double-empty settlement path.
- **Previous MF-2 — the changed Server boundary left the full Server SpecTests assembly failing: fixed.** The final fixture/route/caller migration in the current change aligns generic sessions with the generic route and gives Workflow callers complete acknowledged identity where required. A fresh full Server SpecTests run passed **3005/3005**. The persisted `SourceKind == "workflow"` fail-closed rule remains in production code; the repair did not weaken it.

## Review dimensions

- **Issue basis: checked, no issue.** The issue acceptance criteria and both capability specifications were reread before reviewing the implementation: current-binding activity-only acceptance, preserved Workflow attribution, bounded deterministic-refusal settlement, double-empty input/cleanup settlement, warn-once retention behavior, retry preservation, and saturated-queue liveness.
- **Coverage: checked, no issue.** The current change covers every criterion. Server boundary specs cover active and idle current-binding activity, stale binding no-op behavior, mixed/non-activity rejection before mutation, absent and mismatched Workflow identity, and absence of Workflow observations. Runner tests cover the explicit refusal allowlist, exactly-three threshold, key isolation, persistence failure, two-empty input and cleanup settlement, positive receipt identity checks, interrupted confirmations, retention crossings/recovery, fairness, and saturated historical/live progress. Workflow reporter tests cover fail-closed already-consumed behavior without inventing an Agent turn or enqueueing cleanup follow-on input.
- **Correctness: checked, no issue.** `AgentSessionGrain.AppendRuntimeEventsAsync` classifies Workflow-introduced sessions from persisted metadata, restricts unattributed current-binding batches to non-empty pure `session.activity`, and delegates the physical binding fence to `AppendEventsAsync`. Accepted activity is applied as session-level state without Workflow observations. Workflow-attributed routes still require the complete frozen execution binding. The outbox removes all records for a refused binding key only after the durable snapshot write, confirms matching records only after two consecutive valid empty responses, and restores records/confirmation state on persistence failure.
- **Consistency with the surrounding codebase: checked, no issue.** The implementation uses the existing version-1 snapshot, delivery-group scheduler, route boundaries, structured `ApiResponse` codes, injected timers, and existing persistence/retry mechanisms. Generic fixture changes now use the generic session contract rather than relying on Workflow-labelled sessions to exercise unrelated behavior.
- **Tests: checked, no issue.** Verification completed successfully:
  - Server SpecTests: **3005 passed, 0 failed**.
  - Server UnitTests: **3712 passed, 0 failed**.
  - Runner tests: **1701 passed, 0 failed** across 155 files.
  - Runner production and test TypeScript typechecks passed.
  - Format, file-size, script typecheck, and architecture-script checks passed.

## Observations

- `SessionTranscriptBuilder.PublicPromptText` now strips internal execution-envelope sections from canonical input text as well as the legacy accumulator path. This is a small adjacent correctness repair included with the fixture migration; the relevant Server unit and specification tests pass, and it does not affect the issue verdict.
- Snapshot persistence remains a full version-1 snapshot rewrite for durable settlements. The design explicitly keeps this format and defers a storage redesign; the batching, terminal multi-record removal, retention warning edge tracking, and fair scheduler provide the issue-required convergence behavior within that constraint.
- Refusal and empty-confirmation counters are intentionally process-local and reset across Runner restart, as specified by the plan; recovered records remain durable and retryable without a snapshot migration.

<promise>PASS</promise>
