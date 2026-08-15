# Design: Explicit Started-Work Reconciliation and Replacement

## Decision

The existing `AgentResultSettlement` remains the authority that records a
physical observation for the original `(taskRunId, workId, runnerId)` tuple.
It is not a result. A replacement is a separate, operator-authorized Workflow
transition after that authority has persisted either `TargetMissing` or
`Unknown` and the settlement has reached `Blocked`.

The feature must use the existing run-scoped control path
`WorkflowRoutes.WorkflowControl`, `IWorkflowGrain`, and `RunCommands`. The CLI
may resolve `--issue` to a run id as it does for existing controls, but it must
post only to the run-scoped endpoint. There is no duplicate issue-scoped
mutation route.

## State Model

The original TaskRun has three relevant states:

1. `Running` plus `AgentResultSettlement(AwaitingResult)` is normal execution.
2. `Running` plus a persisted `Unknown` or `Blocked` settlement is unresolved.
   Its `LastObservation` must be `Unknown` or `TargetMissing` for replacement
   eligibility. `Idle`, `Completed`, `Failed`, `Stopped`, and task-log facts
   are not eligibility evidence.
3. `Superseded` is terminal. It retains the original TaskRun, work, Runner,
   settlement observation, operator, reason, request id, and the new TaskRun
   id. It never accepts another report or observation.

`Superseded` is distinct from `Cancelled`: cancellation says the Workflow was
stopped, while supersession says an operator intentionally created a new
attempt because the original result cannot be recovered. It therefore needs a
new `TaskRunStatus`, `TaskSuperseded` event, status projection, event catalog
entry, and a dedicated immutable `AgentResultReplacementDisposition` on the
old task. The normal terminal-settlement cleanup must not erase that
disposition.

## Atomic Grain Transition

`ReplaceUnconfirmedAgentWorkAsync` receives:

```text
expectedTaskRunId, expectedWorkId, expectedRunnerId,
requestId, reason, confirmation
```

The grain serializes and validates all of these facts before mutation:

- the Run is active and exposes blocked settlement attention;
- exactly one current TaskRun matches the expected tuple;
- that TaskRun is still `Running`, its settlement is `Blocked`, and its stored
  observation is `Unknown` or `TargetMissing`;
- `reason` and `requestId` are non-empty and the confirmation token names this
  operation;
- a replay with the same request id and exact fingerprint returns the already
  created replacement; a different fingerprint is rejected.

On success one durable aggregate commit:

1. copies the original tuple and observation into the old task's immutable
   supersession disposition and changes it to `Superseded`;
2. creates a new pending TaskRun from the old task definition with
   `TaskRun.MakeTask`, recording `ReplacementOfTaskRunId` and allocating a
   distinct task-run id;
3. clears the old Runner assignment so ordinary scheduling can choose a
   current Runner for the new work;
4. preserves the current stage lock because the same Workflow and stage still
   own it; removes only the old settlement reminder and old dispatch snapshot
   after the commit;
5. writes one `TaskSuperseded` event with old and new identities, actor,
   reason, and request id.

The control never creates a `WorkId` itself. The new work id is allocated only
by the ordinary post-commit `ClaimNextAsync` path. This is what prevents a
late report for the old tuple from addressing the new attempt.

## API and CLI Boundary

The future API is:

```text
POST /api/workflow-runs/{workflowRunId}/replace-unconfirmed-agent-work
```

The authenticated `ICurrentUser` is the actor; clients cannot supply it. The
request includes the expected original identity, caller `requestId`, reason,
and confirmation. `WorkflowControlGuard` gets a distinct replacement action
that is allowed only for a `blocked` run. The response returns the old and new
TaskRun ids and is replayable by request id.

The future CLI command is `mo run replace-unconfirmed-agent-work`, accepting a
run id or `--issue`, all three expected original identity fields, `--reason`,
`--request-id`, and `--confirm`. It reuses `RunCommands.ResolveRunTargetAsync`
and the normal selected-JSON result convention. It does not retry a transport
failure blindly; a caller resolves the same request id first.

## Rejection and Late Delivery

- A missing or mismatched tuple, any nonblocked settlement, an `Idle`/Turn/log
  observation, a terminal run, or an absent confirmation is rejected without
  mutation.
- The old task is no longer reportable after the atomic transition, so its
  result is `Stale` before artifact binding, follow-up projection, output, or
  status handling.
- The replacement begins pending and has no relationship to the old runtime
  binding. The runner may not resurrect the old work journal entry as new work.
- No reminder, activation, Runner reconnect, runtime witness, or Session
  reconciler can call the replacement command.

## Required Verification for an Implementation

1. `Unknown` and `TargetMissing` physical facts leave one running original
   TaskRun, no output, and no replacement until the operator command.
2. Exact blocked tuple plus one request id creates exactly one `Superseded`
   record and one new pending TaskRun with distinct task/work identity.
3. Replaying the same request returns the same replacement; a changed tuple or
   reason under that request id conflicts.
4. A late old receipt is stale and cannot bind artifacts, output, or follow-up
   tasks; a normal report for the new tuple can settle only the new TaskRun.
5. The API requires the authenticated operator and confirmation, exposes the
   result through the run read model, and is rejected by every nonblocked
   control state.
6. The CLI posts the exact run-scoped path, preserves `--issue` resolution,
   and does not blind-retry unknown transport submission.
