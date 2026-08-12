# Self-Review

## Verdict

PASS. The final proposal, capability spec, design, and seven-task graph cover the live issue contract and the implementation-review corrections. No must-fix planning defect remains.

## Must-Fix Findings

None.

## Issue Acceptance Coverage

**Checked, no issue.** The normative spec and tasks cover each required behavior:

- Inconclusive stop, activity, target, transport, and Runner-loss facts remain physical observations; they cannot produce a Workflow task success or failure (T-001, T-004, T-005).
- The first unknown observation retains the original execution identity and fixed deadline, becomes blocked rather than failed after the configured five-minute default, and remains eligible for a late result (T-001, T-002, T-006).
- A stable input delivery is acknowledged only after AgentSession and Workflow bindings are durable. The receipt returns the canonical AgentTurn identity, and later events and batches use the full immutable identity (T-003, T-004).
- Recovery reuses issue #562's recorded stop operation and may redeliver it only after positive reconciliation proves the exact physical target is still active (T-004).
- Runner disconnect/reconnect preserves unknown work without replacement dispatch while retained in-flight and awaiting-ack keys still consume capacity (T-002, T-005).
- Explicit Workflow stop atomically cancels the task and stops the run; post-commit snapshot, reminder, and lock cleanup is idempotently repaired by replay or activation (T-002).
- The first authoritative result wins once. All later reports are stale and side-effect free, and artifact replay is keyed by uniquely indexed `SourceUploadId` with real `TaskRun.Id` attribution (T-006).
- Blocked attention is projected with reason `agent-result-unconfirmed` to Issue, Inbox, Web, and CLI, while failure fields, failed-task retry, and failure-only GitHub, Hermes, and Agent subscribers remain excluded (T-007).

## Correctness

**Checked, no issue.** The design resolves the principal races and ownership hazards:

- `TaskRun.AgentResultSettlement.State` is the single arbitration authority. Task and stage status are derived; only the minimum run-level blocked value needed by indexed queries is persisted separately.
- The Server receipt, not local Runner snapshot persistence, is the pre-execution fence. Lost acknowledgements replay the same input and Turn.
- AgentSession observations are built from the frozen Turn binding rather than mutable Session labels, and mixed-identity batches are rejected or partitioned.
- Deadline and reminder repair use persisted absolute time and injected `TimeProvider`; duplicate observations and activation repair registration without extending the deadline.
- Stop cleanup is deliberately post-commit and crash-convergent, so external cleanup cannot roll back or replace the committed stopped outcome.
- Result arbitration happens inside the serialized Workflow grain before artifacts, follow-up projection, output, events, or advancement can have effects.

## Current-Code Consistency

**Checked, no issue.** Every required change has an existing boundary and an owning task:

- T-001 extends `TaskRun`/`WorkflowRun` and the Workflow grain with the settlement, identity-fenced bind/observe commands, and reportable-attempt lookup.
- T-002 owns the reminder, unresolved dispatch/control fences, capacity accounting, and explicit-stop cleanup recovery.
- T-003 extends Workflow dispatch/runtime-event metadata, `RunnerRoutes`, AgentSession input acceptance, the Runner reporter, and the receipt contract.
- T-004 replaces `ISessionWorkPort.AbandonActiveWorkAsync` outcome coupling and fixes outbox sequence/batch identity while preserving #562 stop ownership.
- T-005 changes Agent executor unknown results, report translation, `RunnerGrain.CloseoutLostAsync`, and reconnect reconciliation without weakening non-Agent failures.
- T-006 moves report side effects behind grain arbitration and adds the nullable, uniquely indexed `WorkflowArtifactRow.SourceUploadId` migration.
- T-007 updates events, indexed/read projections, Issue/Inbox, Web, CLI, and subscriber routing, and owns the full repository gate.

## Task Breakdown

**Checked, no issue.** Tasks are source-ordered and dependency-ordered: T-001 establishes authority; T-002 and T-003 independently add deadline/control and pre-execution binding; T-004 joins both for immutable observations; T-005 adds Runner-loss semantics; T-006 adds late-result and artifact arbitration; T-007 adds public blocked projections. Every task embeds focused tests and `npm run test:fast`; the final task also requires `npm run verify`.

## Observations

- This is a static planning review; implementation tests do not exist yet.
- The plan worktree did not have `tsx`, so the earlier broad repository gate could not start there. This is not green evidence. T-007 retains the required full gate after implementation in the prepared workflow workspace.

<promise>PASS</promise>
