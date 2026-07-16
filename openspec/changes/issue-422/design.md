## Context

The [proposal](./proposal.md) and [`workflow-task-recovery` specification](./specs/workflow-task-recovery/spec.md) require recovery configuration to remain immutable, automatic recovery to remain bounded per round, and manual retry to start a fresh full-budget round.

Today the runner's `tryRecovery` reads `recovery.budget`, creates the self-retry by copying `recovery` with `budget - 1`, and returns that task through `addTasks`. The server converts the returned task into a new `TaskRun`, so the decremented configuration becomes persisted task data. When that attempt eventually fails, `RetryFailedTask` reconstructs the manual retry from the failed `TaskRun`, including the already-decremented `Recovery`. An exhausted attempt therefore creates a manual retry with budget 0.

The affected path crosses both planes:

```text
TaskDefinition
  -> TaskRun
  -> WorkflowTaskWork / WorkItem
  -> WorkDispatch / runner poll response
  -> RenderedWorkItem / tryRecovery
  -> addTasks / RuntimeTaskInput
  -> TaskRun
```

The architecture constraint remains unchanged: the control plane persists task state and mechanically inserts runner-produced follow-up tasks; the runner owns output matching, handler selection, and automatic recovery construction. Workflow YAML, public CLI/Web behavior, and action output contracts must not acquire execution-state concerns.

Stakeholders are workflow maintainers, runner maintainers, and operators who use manual retry after an automatic review-fix loop fails.

## Goals / Non-Goals

**Goals:**

- Separate immutable recovery declaration data from the remaining allowance of one task attempt.
- Preserve the remaining allowance through persistence, dispatch, runner execution, and follow-up task insertion.
- Make every fresh task attempt initialize a full allowance from its declared budget.
- Make automatic self-retries continue the same round with exactly one fewer allowance.
- Make manual retry reconstruct a fresh attempt without copying execution state.
- Preserve existing matching, first-handler, follow-up ordering, and ordinary-result behavior.

**Non-Goals:**

- Change `when` syntax, output matching, handler ordering, recovery task definitions, or `retrySelf` behavior.
- Move recovery matching or budget decrementing into the server workflow engine.
- Change approval, rerun, or rerun-from-stage semantics.
- Change workflow YAML, public HTTP APIs, CLI commands, Web UI, or action contracts.
- Backfill or rewrite historical task attempts produced before this change.

## Decisions

### Decision 1: Model remaining allowance as task-attempt state

Keep `RecoveryDefinition` and `TaskDefinition.Recovery` unchanged. Add `RecoveryRemaining` to `TaskRun` as execution state beside status, output, worker, and timestamps. A task without recovery has remaining 0. A fresh recovery-enabled attempt starts at `Recovery.Budget`; an automatic continuation receives an explicit decremented value. Task creation enforces `0 <= RecoveryRemaining <= Recovery.Budget`.

Use a small internal task-attempt creation input containing a `TaskDefinition` plus optional `RecoveryRemaining`. Omitted remaining means "fresh attempt" and is initialized from the definition; an explicit value means "continuation of the current automatic round." This keeps execution state out of `TaskDefinition` while allowing runtime insertion to preserve it.

Alternatives considered:

- Continue decrementing `recovery.budget`. Rejected because it is the root cause and makes declaration data depend on execution history.
- Add `remaining` inside the `recovery` object. Rejected because it preserves the same configuration/state coupling under a different property name.
- Infer remaining allowance from task attempt numbers. Rejected because attempt numbers span manual rounds and do not indicate whether an attempt consumed recovery.

### Decision 2: Centralize definition projection and fresh-attempt construction

Add one `TaskRun` definition projection that returns exactly the definition-owned fields: definition id, title, action, input, artifacts, variable writes, and recovery declaration. It excludes attempt id, status, timestamps, worker/work ids, output, provenance, and `RecoveryRemaining`.

All fresh construction paths use the same factory:

- Stage initialization and ordinary runtime task addition initialize remaining from the declaration.
- Handler tasks returned without an explicit remaining value start their own recovery round from their own declaration.
- Manual `RetryFailedTask` projects the failed task back to definition data and invokes the fresh factory, so the preceding round's remaining value cannot enter the new attempt; the failed attempt and earlier history remain unchanged.
- Runner-generated self-retries use the continuation form and supply the decremented remaining value.

Alternatives considered:

- Clone the failed `TaskRun` and reset selected fields. Rejected because every future execution field would become another leak risk and manual retry would remain coupled to the full runtime shape.
- Reload the task from the current workflow profile during retry. Rejected because a running stage is a snapshot, profiles can change after stage initialization, and runtime-added tasks may not exist in the profile.
- Duplicate the definition field list in each retry/insertion path. Rejected because the current duplicate in `RetryFailedTask` is precisely where state can be copied accidentally.

### Decision 3: Carry `recoveryRemaining` through the existing per-task contract

Extend only task-bearing internal shapes, appending new Orleans field ids and record parameters so existing ids remain stable:

| Boundary | Shape change |
|---|---|
| Persisted workflow state | `TaskRun.RecoveryRemaining` |
| Workflow domain work | `WorkflowTaskWork` and the task variant of `WorkItem` |
| Server-to-runner dispatch | `WorkDispatch` and `WorkDispatchResponse` |
| Runner execution | `WorkDispatchResponse`, `RenderedWorkItem`, and `AddTaskInput` |
| Runner-to-server follow-ups | `RuntimeTaskInput.RecoveryRemaining` inside each `addTasks` entry |

`WorkflowWorkLifecycle`, `WorkflowItemTranslator`, the poll DTO mapper, and the runner connection mapper only pass the value through. `ActionContext` does not gain the field: actions do not interpret recovery budget, and `tryRecovery` already operates on `RenderedWorkItem` after action execution.

The remaining value belongs on each follow-up task rather than on `WorkResult`, because one result can contain multiple handler tasks plus a self-retry and each task can begin or continue a different recovery round.

Alternatives considered:

- Put one remaining value on `WorkResult`. Rejected because it cannot describe multiple follow-up tasks independently.
- Persist a separate database row or column. Rejected because the state belongs to a `TaskRun`, is not queried independently, and the workflow aggregate already persists the complete task graph.

### Decision 4: The runner is the sole decrement authority

Replace `decrementRecoveryBudget` with remaining-state handling in `tryRecovery`:

```text
recovery = read immutable work.recovery
remaining = bounded work.recoveryRemaining
handler = first matching recovery handler

if no handler or remaining == 0:
    return ordinary normalized result

followUps = handler.tasks
if handler.retrySelf:
    append self-retry with:
      recovery = unchanged work.recovery
      recoveryRemaining = remaining - 1

return completed + followUps
```

The server initializes fresh state and persists values, but it never decrements allowance or interprets handler matching. No-match results do not consume allowance. A matching completed result still schedules recovery. Handler tasks remain in declaration order and the self-retry remains last.

Alternatives considered:

- Decrement in the server when applying `addTasks`. Rejected because it teaches the control plane recovery semantics and duplicates the runner's decision.
- Have the runner report only "recovery consumed" and let the server derive the next value. Rejected because it splits one decision across planes and creates two authorities for the same invariant.

### Decision 5: Persist in the workflow JSON aggregate without a schema migration

`WorkflowRunStore` already serializes the complete `WorkflowRun` into the `WorkflowRuns.State` JSON column, so `TaskRun.RecoveryRemaining` is additive state within that JSON document. No EF migration or new table is required. The immutable `Recovery` object remains the source for any task history or projection that carries recovery configuration; `RecoveryRemaining` is not added to public task status, timeline, CLI, or Web DTOs.

Workflow events currently carry task identity and outcome rather than recovery configuration, so no event schema changes are needed. Any representation that does carry recovery must map the unchanged `TaskRun.Recovery`, never synthesize it from remaining state.

The implementation must update `design/workflow/recovery.md`, which currently documents decrementing `recovery.budget`, to describe the separate state and remove the stale example that emits a reduced recovery declaration.

Alternatives considered:

- Add remaining allowance to public task status or timeline DTOs. Rejected because operators act on task failure and retry, not on the internal counter, and the specification requires immutable declarations rather than a new UI surface.
- Emit a workflow event for each budget decrement. Rejected because the generated task attempt already persists the state and appears in workflow history; a second event stream would duplicate the same fact.

### Decision 6: Verify behavior at the owning boundaries

- Runner specs cover unchanged recovery configuration, `recoveryRemaining` decrement, zero allowance, no-match preservation, first-handler selection, and follow-up ordering.
- Server domain tests cover fresh versus continuation task construction and prove the definition projection excludes execution state.
- Workflow grain specs cover runner follow-up insertion preserving remaining state, exhaustion followed by manual retry restoring the full budget, and previous attempts remaining unchanged.
- Translator/API contract tests cover the field in poll dispatch and report `addTasks`; persistence tests cover workflow JSON round-trip.

All tests use existing fakes and deterministic inputs. No real runner, network, database service, or wall-clock timing is introduced.

Alternatives considered:

- Cover only `tryRecovery` with runner unit tests. Rejected because the defect is caused by state crossing into server persistence and manual retry, which a runner-only test cannot observe.
- Build a new cross-process end-to-end harness. Rejected because focused contract tests plus the existing grain spec boundary cover the behavior without introducing real process or network dependencies forbidden by the test policy.

## Risks / Trade-offs

- `[A pass-through layer drops recoveryRemaining]` -> Add focused contract assertions at WorkItem, WorkDispatch, HTTP DTO, runner mapping, report translation, and TaskRun insertion boundaries, plus the grain-level exhaustion/retry scenario.
- `[Server and runner versions are mixed during rollout]` -> Deploy them as one coordinated change with workflow dispatch quiesced; the old runner mutates configuration and the new runner expects separate state, so mixed execution is unsupported.
- `[Malformed remaining state exceeds the declaration or becomes negative]` -> Validate task-attempt creation and bound the value at runner evaluation; invalid state must never increase a round beyond the declared budget.
- `[Rollback occurs after new-format self-retries are persisted]` -> Drain workflow work and rerun affected stages after rollback; the old runner ignores separate remaining state and must not continue a new-format recovery chain.
- `[Historical attempts retain decremented recovery declarations]` -> Do not rewrite history in this actively developed system. Drain in-flight recovery work before deployment and rerun any pre-change affected stage so new attempts use the new invariant.

## Migration Plan

1. Add the server domain state, centralized task-attempt construction, internal contract fields, and server tests.
2. Update runner types, connection mapping, recovery evaluation, and runner tests in the same change.
3. Align `design/workflow/recovery.md` and run `npm run build`, `npm test`, `npm run typecheck -w packages/runner`, and `npm test -w packages/runner`.
4. Before deployment, stop or drain workflow dispatch so no recovery task crosses versions; deploy server and runner together, then resume dispatch.

No database schema or data migration runs. Pre-change in-flight or failed recovery chains are not backfilled; rerun the affected stage after deployment before relying on the new manual-retry behavior.

Rollback requires draining dispatch, rolling back server and runner together, and rerunning any stage that persisted a new-format automatic self-retry. The additive JSON field needs no database rollback.

## Open Questions

None. Field ownership, initialization, transport, rollout, and rollback are resolved by the decisions above.
