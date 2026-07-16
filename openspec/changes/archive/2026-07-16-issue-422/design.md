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

## Decisions

### Decision 1: Model remaining allowance as task-attempt state

Keep `RecoveryDefinition` and `TaskDefinition.Recovery` unchanged. Add nullable `RecoveryRemaining` to `TaskRun` as execution state beside status, output, worker, and timestamps. Its in-memory and wire states are intentional: explicit `null` means a fresh recovery-enabled attempt whose effective allowance must be initialized by the runner; a number means an execution-authored continuation allowance; an absent JSON property is reserved for pre-change persisted state or a malformed wire contract. Numeric state is bounded to `0 <= RecoveryRemaining <= Recovery.Budget`.

Use a small internal task-attempt creation input with distinct fresh and continuation forms. Fresh construction takes only a `TaskDefinition` and stores explicit `null`; continuation construction takes a `TaskDefinition` plus an explicit numeric `RecoveryRemaining`. The persisted `TaskRun` property and runner poll DTO must serialize explicit nulls even though the shared JSON options normally omit null values, so the execution boundary can distinguish fresh from absent. This keeps execution state out of `TaskDefinition`, leaves initial materialization with the runner, and prevents a dropped pass-through value from silently opening a new round.

Alternatives considered:

- Continue decrementing `recovery.budget`. Rejected because it is the root cause and makes declaration data depend on execution history.
- Add `remaining` inside the `recovery` object. Rejected because it preserves the same configuration/state coupling under a different property name.
- Infer remaining allowance from task attempt numbers. Rejected because attempt numbers span manual rounds and do not indicate whether an attempt consumed recovery.
- Initialize a numeric allowance in the control plane. Rejected because the issue assigns recovery-state interpretation to the execution side; the control plane only marks a fresh attempt and passes state through.

### Decision 2: Centralize definition projection and fresh-attempt construction

Add one `TaskRun` definition projection that returns exactly the definition-owned fields: definition id, title, action, input, artifacts, variable writes, and recovery declaration. It excludes attempt id, status, timestamps, worker/work ids, output, provenance, and `RecoveryRemaining`.

All fresh construction paths use the same factory:

- Stage initialization, ordinary runtime task addition, and manual retry create fresh state with explicit `null`; they do not interpret the declaration budget.
- Runner-produced handler tasks with their own recovery declaration carry an explicit full allowance for that declaration; non-recovery handler tasks carry no recovery state.
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

`WorkflowWorkLifecycle`, `WorkflowItemTranslator`, the poll DTO mapper, and the runner connection mapper only pass the value through. `TaskRun.RecoveryRemaining` and `WorkDispatchResponse.RecoveryRemaining` are configured to emit explicit nulls; the TypeScript mapper preserves the distinction between `null` and `undefined`. The runner report path validates that every `addTasks` entry with a recovery declaration has an explicit numeric remaining value; a missing or null value is rejected rather than treated as fresh. Server-originated fresh task creation uses the separate fresh factory and therefore reaches the runner as explicit null without passing through the report path. `ActionContext` does not gain the field: actions do not interpret recovery budget, and `tryRecovery` already operates on `RenderedWorkItem` after action execution.

The remaining value belongs on each follow-up task rather than on `WorkResult`, because one result can contain multiple handler tasks plus a self-retry and each task can begin or continue a different recovery round.

Alternatives considered:

- Put one remaining value on `WorkResult`. Rejected because it cannot describe multiple follow-up tasks independently.
- Persist a separate database row or column. Rejected because the state belongs to a `TaskRun`, is not queried independently, and the workflow aggregate already persists the complete task graph.

### Decision 4: The runner is the sole decrement authority

Replace `decrementRecoveryBudget` with remaining-state handling in `tryRecovery`:

```text
recovery = read immutable work.recovery
state = read work.recoveryRemaining with presence awareness
if state is absent:
    return ordinary normalized result
remaining = recovery.budget when state is null, otherwise clamp(state, 0, recovery.budget)
handler = first matching recovery handler

if no handler or remaining == 0:
    return ordinary normalized result

followUps = handler.tasks mapped so every recovery-enabled task carries:
  recoveryRemaining = its own recovery.budget
if handler.retrySelf:
    append self-retry with:
      recovery = unchanged work.recovery
      recoveryRemaining = remaining - 1

return completed + followUps
```

Only the runner interprets the fresh null marker, materializes the declared budget for evaluation, and authors numeric state on every recovery-enabled follow-up. Every server and transport layer preserves explicit null or numeric state exactly. The runner bounds malformed numeric input to the declaration (negative becomes zero; above-budget becomes the declared budget) and treats an absent property as ineligible rather than resetting it. No-match results do not consume allowance. A matching completed result still schedules recovery. Handler tasks remain in declaration order; a handler task with its own recovery starts with an explicit numeric full allowance, `retrySelf: true` appends the decremented self-retry last, and `retrySelf: false` appends none.

Alternatives considered:

- Decrement in the server when applying `addTasks`. Rejected because it teaches the control plane recovery semantics and duplicates the runner's decision.
- Have the runner report only "recovery consumed" and let the server derive the next value. Rejected because it splits one decision across planes and creates two authorities for the same invariant.

### Decision 5: Persist in the workflow JSON aggregate without a schema migration

`WorkflowRunStore` already serializes the complete `WorkflowRun` into the `WorkflowRuns.State` JSON column, so `TaskRun.RecoveryRemaining` is additive state within that JSON document. No EF migration or new table is required. The immutable `Recovery` object remains the source for any task history or projection that carries recovery configuration; `RecoveryRemaining` is not added to public task status, timeline, CLI, or Web DTOs.

Normalize pre-change task state in the raw JSON migration step before ordinary deserialization. Presence of the `recoveryRemaining` property is the format discriminator: missing means legacy, while explicit null and explicit numbers are new-format state and are never normalized again. For each legacy `DefinitionId` group, compare recovery declarations structurally while ignoring only `budget`; normalize only when handlers, predicates, tasks, and `retrySelf` values match. Use the earliest matching attempt's recovery declaration as canonical, copy each legacy attempt's currently stored `recovery.budget` into its new numeric `RecoveryRemaining`, and replace only that attempt's recovery declaration with the canonical one. Reject an ambiguous reused-id group with an actionable load error rather than rewriting it. This conversion is idempotent and does not change attempt identity, status, output, ordering, or other history. A failed legacy attempt with encoded budget 0 therefore retains remaining 0 but regains the original declaration, allowing manual retry to create explicit fresh null and the runner to restore the full budget.

Workflow events currently carry task identity and outcome rather than recovery configuration, so no event schema changes are needed. Any representation that does carry recovery must map the unchanged `TaskRun.Recovery`, never synthesize it from remaining state.

The implementation must update `design/workflow/recovery.md`, which currently documents decrementing `recovery.budget`, to describe the separate state and remove the stale example that emits a reduced recovery declaration.

Alternatives considered:

- Leave pre-change recovery chains unchanged and require operators to rerun the stage. Rejected because the reported defect exists on an already-persisted exhausted chain and the manual-retry requirement is unconditional.
- Add remaining allowance to public task status or timeline DTOs. Rejected because operators act on task failure and retry, not on the internal counter, and the specification requires immutable declarations rather than a new UI surface.
- Emit a workflow event for each budget decrement. Rejected because the generated task attempt already persists the state and appears in workflow history; a second event stream would duplicate the same fact.

### Decision 6: Verify behavior at the owning boundaries

- Runner specs cover unchanged recovery configuration, explicit-null initialization, absent-state fail-closed behavior, `recoveryRemaining` decrement, zero allowance, no-match preservation, first-handler selection, nested recovery-enabled handler initialization, both `retrySelf` branches, malformed clamps, and follow-up ordering.
- Server domain tests cover fresh-null versus numeric-continuation task construction, explicit-null serialization, reject missing/null runner continuation state, and prove the definition projection excludes execution state.
- Workflow grain specs cover runner follow-up insertion preserving remaining state, exhaustion followed by manual retry restoring the full budget, and previous attempts remaining unchanged.
- Translator/API contract tests cover explicit null versus numeric values in poll dispatch and numeric state in report `addTasks`; raw-JSON persistence tests cover format presence detection, idempotence, preservation of explicit null/zero, normalization of a pre-change 2 -> 1 -> 0 recovery chain, rejection of structurally ambiguous reused ids, and manual retry of an exhausted legacy attempt.

All tests use existing fakes and deterministic inputs. No real runner, network, database service, or wall-clock timing is introduced.

Alternatives considered:

- Cover only `tryRecovery` with runner unit tests. Rejected because the defect is caused by state crossing into server persistence and manual retry, which a runner-only test cannot observe.
- Build a new cross-process end-to-end harness. Rejected because focused contract tests plus the existing grain spec boundary cover the behavior without introducing real process or network dependencies forbidden by the test policy.

## Risks / Trade-offs

- `[A pass-through layer drops recoveryRemaining]` -> Require explicit state on every runner-produced recovery-enabled follow-up, fail closed when it is missing, and add focused contract assertions at WorkItem, WorkDispatch, HTTP DTO, runner mapping, report translation, and TaskRun insertion boundaries.
- `[Server and runner versions are mixed during rollout]` -> Deploy them as one coordinated change with workflow dispatch quiesced; the old runner mutates configuration and the new runner expects separate state, so mixed execution is unsupported.
- `[Malformed remaining state exceeds the declaration or becomes negative]` -> Validate task-attempt creation and bound the value at runner evaluation; invalid state must never increase a round beyond the declared budget.
- `[Rollback occurs after new-format self-retries are persisted]` -> Drain workflow work and rerun affected stages after rollback; the old runner ignores separate remaining state and must not continue a new-format recovery chain.
- `[Legacy normalization chooses the wrong declaration for reused definition ids]` -> Normalize only when all non-budget recovery structure matches, reject ambiguous groups, and cover profile and runtime-added recovery tasks with persisted legacy fixtures.

## Migration Plan

1. Add the server domain state, distinct fresh/continuation task-attempt construction, legacy normalization, internal contract fields, and server tests.
2. Update runner types, connection mapping, recovery evaluation, and runner tests in the same change.
3. Align `design/workflow/recovery.md` and run `npm run build`, `npm test`, `npm run typecheck -w packages/runner`, and `npm test -w packages/runner`.
4. Before deployment, stop or drain workflow dispatch so no recovery task crosses versions; deploy server and runner together, then resume dispatch.

No database schema migration runs. Existing workflow JSON is normalized by presence-aware raw JSON migration on load; the next aggregate save persists canonical recovery declarations and explicit remaining values. New-format explicit null and zero survive repeated loads unchanged. Verify deployment against a persisted pre-change exhausted chain before resuming general dispatch.

Rollback requires draining dispatch, rolling back server and runner together, and rerunning any stage that persisted a new-format automatic self-retry. The additive JSON field needs no database rollback.

## Open Questions

None. Field ownership, initialization, transport, rollout, and rollback are resolved by the decisions above.
