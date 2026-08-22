# Self-Review: issue-639

## Verdict

PASS. This is a re-review. The previous must-fix findings are addressed in the plan artifacts, and no new must-fix problem was found. The plan is ready to build.

## Previous findings and dispositions

- **MF-1 — Workflow-session pure-activity boundary:** fixed. `design.md` decision 1 and T-001 now classify Workflow-introduced sessions from persisted `SourceKind == workflow`, require a non-empty pure `session.activity` batch for the unattributed relaxation, and reject unattributed non-activity or mixed batches before append even when no matching Workflow turn exists. The associated spec and T-001 tests cover the no-turn failure case, current-binding activity, stale binding, and absence of Workflow observations.
- **MF-2 — Workflow cleanup empty-receipt path:** fixed. `design.md` decision 4 and T-003 explicitly change the cleanup grain result, Server cleanup route, `ServerConnection.workflowAgentSessionCleanupTurn`, and the delivery adapter to use a receipt-array protocol: one validated receipt for a new operation and `[]` for an idempotent replay after complete identity checks. The plan also preserves positive cleanup identity checks and prevents the follow-on input from being enqueued after an already-consumed outcome.
- **MF-3 — Deterministic 4xx classification:** fixed. `design.md` decision 2 and T-002 commit to the structured `(status, code)` allowlist, include the observed `(409, conflict)` response, exclude unknown/client-auth/transient statuses, and fix the threshold at exactly three consecutive refusals. The convergence spec and tests make the boundary and retry behavior verifiable.

## Regression check

- The cleanup protocol change is scoped to the cleanup endpoint and its outbox adapter; new-operation positive receipts and existing cleanup identity validation remain required.
- The activity relaxation remains behind the session-scoped route and grain binding fence; the Workflow-labeled route and complete turn-attribution contract remain fail-closed.
- Terminal settlement is staged and persisted before waiter settlement, with rollback on persistence failure. The plan does not add snapshot migration, retargeting, or a second queue.
- The fair scheduler remains the selected mechanism, while terminal settlement releases its group lease and the saturated historical/live fake-timer scenario is assigned to T-004.
- No regression meeting the must-fix threshold was found.

## Review dimensions

### Issue basis — checked, no issue

The issue goals and acceptance criteria were re-read before evaluating the updated artifacts: current-binding activity-only acceptance, preserved Workflow attribution fences, bounded deterministic-refusal settlement, double-empty already-consumed settlement for input and cleanup, warn-once retention behavior, retry preservation, and live Workflow delivery progress.

### Coverage — checked, no issue

`proposal.md`, `design.md`, both capability specs, and all four tasks cover each issue goal and acceptance criterion. T-001 covers the Server boundary; T-002 covers typed refusal metadata and per-key dead-letter settlement; T-003 covers both matching-receipt record families and fail-closed callers; T-004 covers retention crossings, recovery, fairness, and saturated-queue liveness.

### Correctness — checked, no issue

The revised approach reaches the previously missing paths: pure current-binding activity can be accepted without attribution, deterministic refusals can converge after three keyed attempts, cleanup replays can produce observable empty receipt arrays, and two consecutive empty responses can settle without fabricating identity. Retryable failures, positive receipt checks, persistence ordering, and Workflow attribution fences are explicitly preserved.

### Consistency with the current codebase — checked, no issue

The plan targets the existing grain, Runner routes, cleanup route, Server connection, delivery adapter, durable version-1 outbox, and current round-robin scheduler. Its proposed cleanup response change matches the existing ordinary runtime-events array protocol rather than introducing a parallel response format. Its typed-error approach preserves successful delivery APIs while adding metadata only to failure handling.

### Task breakdown — checked, no issue

The task graph is complete and acyclic: Server activity handling and refusal plumbing can proceed independently; already-consumed settlement depends on the refusal/error plumbing; retention and liveness verification depends on both settlement paths. Each task has concrete acceptance criteria and fake-timer/spec coverage, including persistence failure and final snapshot behavior.

## Observations

- The design correctly records that the existing version-1 snapshot still requires full serialization for durable removals. The issue excludes a snapshot-format redesign, and the plan improves batching and terminal multi-record removal; this remains an operational trade-off, not a must-fix gap under the stated acceptance criteria.
- The exact higher-level recovery UX after a typed `already-consumed` outcome remains open. The required fail-closed behavior is specified: no fabricated receipt or Agent turn, no attributed follow-up, and no cleanup follow-on input. The unresolved choice between failing, escalating, or another existing recovery path does not make the outbox convergence plan wrong or incomplete relative to the issue.
- The optional metric for terminal settlements is correctly left as observability-only.

<promise>PASS</promise>
