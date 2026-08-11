# Self-Review

## Verdict

PASS. The proposal, capability spec, design, and task graph cover the live issue contract and are consistent with the current implementation boundaries. No must-fix planning defect was found.

## Must-Fix Findings

None.

## Issue Acceptance Coverage

**Checked, no issue.** Every acceptance criterion has a normative scenario and an implementation task with focused tests:

- Stop-delivery failure and timeout no longer settle the Workflow task; the session records the physical observation and the Workflow waits for the final Agent result (`design.md` lines 158-179, spec lines 99-112, T-003).
- Final Agent failures preserve the Agent code/message instead of being replaced by stop-delivery text (`design.md` lines 81-95, spec lines 40-44, T-003).
- Runtime final results are acknowledged only after durable Workflow acceptance, and restart/replay suppresses acknowledged results (`design.md` lines 60-95 and 143-156, spec lines 22-38, T-002 and T-004).
- Agent session reuse and delayed old-run events are isolated by pre-execution `(workflowRunId, taskRunId)` binding (`design.md` lines 97-121, spec lines 46-67, T-003 and T-004).
- Runner loss and Agent session loss keep the task unresolved and explicitly manage Runner capacity without manufacturing a failure (`design.md` lines 181-195, spec lines 114-133, T-005).
- Cleanup obligations survive restarts through Workflow-owned retry state and reminders until server-observed cleanup completion (`design.md` lines 123-141, spec lines 69-97, T-003 and T-004).
- CLI and Web blocked projections expose `WaitingForAgentResult` without changing the persisted `Running` state (`design.md` lines 197-217, spec lines 135-145, T-006).

## Correctness

**Checked, no issue.** The design handles the principal races and failure windows called out by the issue:

- The Workflow grain is the sole authority for task settlement; session, connection, stop, and Runner observations cannot call a failure transition.
- Result identity is bound before execution and carried through dispatch, runtime events, delivery, and Workflow reporting, preventing stale events from attaching to a reused session.
- Durable Workflow acceptance precedes acknowledgement, while Workflow idempotency and the settlement receipt make lost acknowledgements and replay safe.
- Cleanup completion is server-observed, persisted separately from task outcome, and retried by reminders, so a process restart cannot erase the obligation.
- Late results pass through the same acceptance/arbitration boundary; rejected results cannot mutate outcome, artifacts, cleanup state, or acknowledgement state.
- Unresolved non-dispatchable work remains visible to Runner reconciliation and is excluded from capacity without being redispatched.

The rules preserve the stated invariants: final Agent results are the only success/failure facts, each task settles once, late inputs have no side effects, and physical cleanup remains independent of business outcome.

## Current-Code Consistency

**Checked, no issue.** The plan names the actual ownership boundaries and the necessary contract changes:

- `WorkflowReportService` and `WorkflowItemTranslator` are the current report gate and task transition boundary; T-003 moves final-result arbitration there rather than adding a competing owner.
- `RuntimeEventDeliveryService`, `RunnerRoutes`, and `runtime-event-outbox.ts` already form the durable FIFO delivery loop; T-002 and T-004 extend its acknowledgement/reconciliation contract instead of replacing it.
- `DispatchService`, `WorkDispatchResponse`, `DispatchWorkItem`, and the runtime event envelope are the current identity path; T-001 and T-003 close the missing `taskRunId` path end to end.
- `AgentSessionGrain` and `ISessionWorkPort` currently expose abandonment as the physical control path; T-003 replaces that outcome-coupled contract with observation and cleanup operations.
- `RunnerGrain.CloseoutLostAsync`, active-work reconciliation, and capacity accounting are the current Runner-loss boundaries; T-005 changes them coherently.
- The CLI and Web currently derive blocked state from running-item facts; T-006 extends those projections without introducing a new persisted Workflow state.

No task depends on a nonexistent subsystem, and no planned ownership move conflicts with the present server/runner split.

## Task Breakdown

**Checked, no issue.** The seven tasks are vertically scoped and topologically ordered:

1. T-001 introduces identity contracts and preserves transition invariants.
2. T-002 adds the durable settlement receipt and unresolved-work projection used by later slices.
3. T-003 implements Workflow-owned settlement and identity-aware session cleanup.
4. T-004 completes durable runtime acknowledgement, replay, cleanup recovery, and late-result isolation.
5. T-005 applies the unresolved-work model to Runner loss and capacity.
6. T-006 exposes the blocked projection in CLI and Web.
7. T-007 performs cross-layer verification and the full repository gate.

Dependencies reference only earlier tasks, each implementation slice includes focused automated tests, and T-007 checks all acceptance themes plus `npm run verify`. The task graph is specific enough to implement without leaving a known design decision to the implementer.

## Observations

- This was a static planning review; no production code was changed and implementation tests do not exist yet.
- The local repository gate previously could not start because `tsx` is unavailable, and `npx openspec validate issue-589 --strict` could not resolve an executable. These are environment/tooling gaps, not defects in the issue plan; T-007 retains the required full gate before implementation handoff.

<promise>PASS</promise>
