## Context

User-triggered rebase currently bypasses the active WorkflowRun. `POST /api/issues/:number/rebase` enqueues a legacy issue-queue job, `AgentRunnerService.executeRebaseTask()` performs git rebase directly, and the UI mainly learns progress from dedicated rebase SSE events. That path duplicates workflow scheduling, hides rebase from the stage task list, and mixes task execution with downstream policy such as re-review, approval resets, and build-plan resets.

The codebase already has most of the primitives this change needs:

- `WorkflowRun` can append runtime-added tasks through `StageRun.appendAdHocTask(...)` and already enforces task-order blocking through `nextWork()`.
- `WorkflowApplicationService` is the write boundary for aggregate updates and projections.
- `WorkflowEngine` and `BaseStageRunner` can execute a requested WorkflowRun task via the shared task runtime introduced in #199.
- `ServiceCallTaskHandler` is already the right shape for a synchronous repository/worktree-backed task.

The main design constraint is to reuse those boundaries instead of creating a rebase-only execution path. Rebase should become ordinary stage work: API schedules it, WorkflowRun decides when it runs, a handler executes it, and domain policy decides whether SHA changes invalidate review/check/approval state.

## Goals / Non-Goals

**Goals:**

- Make user-triggered rebase visible as a normal `rebase-branch` WorkflowRun task in the current stage.
- Ensure `rebase-branch` is scheduled only through `WorkflowRun.nextWork()` and obeys the same task-order and failure semantics as other tasks.
- Implement `rebase-branch` on top of the shared non-Build task runtime from #199, using a service-backed handler rather than queue-only logic.
- Record enough factual output from rebase to decide whether the candidate snapshot changed.
- Invalidate review/check/approval state only from a post-rebase SHA-changed fact and stage policy, not from the click itself.
- Keep stage-state projection and Web UI task rendering aligned with canonical WorkflowRun state.

**Non-Goals:**

- Redesigning the overall workflow DSL or stage-definition format.
- Creating a new task runner parallel to `WorkflowEngine`, `BaseStageRunner`, or the #199 handler runtime.
- Letting `rebase-branch` decide replan, re-review, or task regeneration policy in its handler.
- Changing git rebase conflict-resolution capability itself.
- Removing every legacy `taskType='rebase'` branch in one step if a short-lived compatibility path is still needed during migration.

## Decisions

### D1: Schedule rebase through a WorkflowRun application command, not through the issue task queue

Add a dedicated aggregate-facing command at the workflow application boundary, such as `scheduleAdHocTask` or a narrower `scheduleRebaseTask`, that loads the active run, verifies the current stage, appends `rebase-branch` to the current `StageRun`, saves the aggregate, and applies projection updates immediately.

The API route remains the user intent entrypoint, but its non-Done path changes from `agentRunner.enqueue(issue.id, 'rebase', ...)` to a WorkflowRun mutation. That keeps the user-visible source of truth in one place and makes the task appear in stage-state/UI before execution starts.

The command should be idempotent for visible in-flight work: if the current stage already contains `rebase-branch` in `pending` or `running`, the command returns the existing task state instead of appending another copy.

**Alternatives considered:**
Keep the API enqueue behavior and mirror a synthetic `rebase-branch` row into stage-state. Rejected because it would preserve two schedulers and would still let queue execution bypass `WorkflowRun.nextWork()` ordering.

### D2: `rebase-branch` is a shared service-call task, not a special runner branch

Implement `rebase-branch` as a new shared task-runtime entry resolved by stage/task id and executed through `ServiceCallTaskHandler`. The handler's service function should:

- locate the project and issue worktree
- fetch current base/head SHAs before rebase
- perform the existing worktree rebase operation
- collect base/head SHAs after rebase
- derive `shaChanged` from before/after SHA comparison
- return structured factual output plus conflict/error evidence

This keeps rebase as one executable work unit and lets `BaseStageRunner.executeTaskWork(...)` and WorkflowRun task completion keep owning reporting and failure behavior. The handler may still emit existing rebase SSE events for compatibility, but those events become supporting telemetry rather than the primary workflow progress model.

`AgentRunnerService.executeRebaseTask()` should stop being the main execution path for Web/API-triggered rebase. Any logic worth preserving there should be moved downward into reusable worktree/service helpers or into post-task domain policy.

**Alternatives considered:**
Add rebase as a direct branch in `CheckStageRunner` or `IntegrateStageRunner`. Rejected because rebase is cross-stage runtime-added work and would spread task knowledge across multiple runners again.

### D3: Distinguish “user asked for rebase” from “snapshot changed after rebase”

Use two separate concepts:

- scheduling metadata: `causedBy.type = 'user-action'` or `branch-changed` with a human-readable reason such as `Target branch moved; rebase requested`
- execution fact: task output fields such as `beforeBaseSha`, `afterBaseSha`, `beforeHeadSha`, `afterHeadSha`, `shaChanged`, `rebased`, and conflict metadata

The click only creates the task. It does not invalidate checks or approval. Only task completion with `shaChanged = true` can trigger follow-up invalidation.

This separates intent from facts and removes the current ambiguity where “the user clicked Rebase” is treated as if the reviewed snapshot definitely changed.

**Alternatives considered:**
Infer invalidation from whether rebase ran successfully at all. Rejected because up-to-date branches and no-op rebases would still incorrectly invalidate review/check state.

### D4: Put SHA-change invalidation in `WorkflowRun` domain policy, not in the rebase handler

Extend aggregate behavior so rebase completion can trigger stage-specific invalidation rules after the task result is recorded. The simplest shape is to enhance `WorkflowRun.completeTask(...)` with a branch for `taskId === 'rebase-branch'` that inspects normalized output and, when `shaChanged` is true, resets the affected task/check/approval state for the current stage and emits invalidation events.

The policy should be stage-local:

- `check`: reset `ai-review`, `review-passed`, `merge-ready`, and approval snapshot because approval truth depends on the reviewed snapshot
- `plan`: reset only the approval snapshot and any review-truth checks that are spec-defined for Plan; do not invent build-plan regeneration behavior in this issue
- `build`: reset only the stage entities whose validity depends on code snapshot change; do not move the issue back to Plan and do not regenerate `tasks.json`
- `integrate`: only allow rebase if the stage policy still permits code-modifying work before any freeze point; after `integrate:merge` freeze, reject scheduling

The handler returns facts; the aggregate owns invalidation. This matches the existing boundary where `fix-review-findings` completion already invalidates downstream check state inside `WorkflowRun.completeTask(...)`.

**Alternatives considered:**
Have the handler call back into `WorkflowApplicationService` to reset checks directly. Rejected because that would split one business decision across task runtime and domain code.

### D5: Approval-waiting stages resume by returning from `awaiting-approval` to `running` when new work is appended

Today `nextWork()` stops at `await-approval` when a stage status is `awaiting-approval`. To let a newly added `rebase-branch` run in that state, the scheduling command must reopen the current stage when it appends executable work. The stage should move from `awaiting-approval` back to `running`, while preserving prior approval evidence until post-rebase SHA policy decides whether it remains valid.

This is the minimal change that satisfies the requirement “approval waiting stages can execute newly appended tasks” without treating approval as a task or forcing the API layer to special-case approval flows.

The reopen behavior belongs in the aggregate append-task command, not in UI polling or runner code.

**Alternatives considered:**
Allow `nextWork()` to emit tasks even while the stage status remains `awaiting-approval`. Rejected because it weakens the meaning of stage status and makes approval semantics harder to reason about everywhere else.

### D6: Keep `rebase-branch` single-instance and stable-id per stage

Use the stable task id `rebase-branch` instead of generating suffixed ids for repeated user clicks. A stage can contain at most one non-terminal `rebase-branch` at a time. After it completes or fails, a later explicit retry can reuse the same id by resetting or replacing that task through an explicit workflow command, rather than by accumulating multiple visually similar rebase rows.

This keeps the task list understandable and avoids teaching users why several `Rebase branch` rows exist for the same stage. If future product requirements need historical repeated rebase attempts, that can be added deliberately with attempt counters rather than duplicate task identities.

**Alternatives considered:**
Reuse `appendAdHocTask` suffix behavior to create `rebase-branch:2`, `rebase-branch:3`, and so on. Rejected because the user story emphasizes clarity of the current visible workflow step, not an append-only history of repeated button clicks.

### D7: Stage-state and UI stay WorkflowRun-backed; rebase SSE becomes secondary

The UI should continue to render the canonical task list from stage-state / WorkflowRun projections. After the API schedules `rebase-branch`, the immediate visible result is the new pending task row. Running/completed/failed transitions come from normal task projection updates.

Existing `rebase_started`, `rebase_progress`, `rebase_completed`, and `rebase_conflict` events may still be emitted during migration because they already power progress affordances and conflict UI, but they should no longer be required to understand whether rebase is part of the workflow or whether later checks are pending because of it.

**Alternatives considered:**
Remove all rebase SSE immediately and rely only on task status polling. Rejected because it increases rollout risk and would unnecessarily regress fine-grained progress messaging.

## Risks / Trade-offs

- [Appending work to an `awaiting-approval` stage may break assumptions in existing approval code] -> Reopen the stage only through a single aggregate command and add focused tests around approval-preserved vs approval-invalidated flows.
- [Rebase invalidation policy could accidentally recreate old replan/re-review side effects] -> Limit the handler to fact reporting and keep each stage's reset list explicit in one domain-policy branch.
- [Legacy rebase queue path and new WorkflowRun path may diverge during migration] -> Make Web/API use the new path first and reduce the old path to compatibility-only callers with clear comments and tests.
- [Integrate stage may already be frozen after merge] -> Reject scheduling `rebase-branch` once `freezePoint` exists, and keep Done-stage merge-queue retry as a separate compatibility path.
- [Using a stable `rebase-branch` id could complicate repeated retries after failure] -> Treat retry as a reset/re-execution of the same visible task in this issue; defer multi-row history until there is a concrete product need.
- [UI may still over-emphasize SSE and under-emphasize canonical task state] -> Update the rebase action flow so optimistic refresh and subsequent renders always come from stage-state task data first.

## Migration Plan

1. Add or extend WorkflowRun application-service support for scheduling an ad hoc current-stage task and reopening an approval-paused stage when executable work is appended.
2. Implement a shared `rebase-branch` task executor on top of `ServiceCallTaskHandler`, extracting reusable factual rebase logic out of `AgentRunnerService.executeRebaseTask()` where appropriate.
3. Extend `WorkflowRun.completeTask(...)` with post-rebase SHA-change invalidation policy and projection events.
4. Update `POST /api/issues/:number/rebase` so non-Done stages schedule `rebase-branch` through WorkflowRun instead of enqueueing `taskType='rebase'`.
5. Keep dedicated rebase SSE emission during task execution, but switch UI progress semantics to treat WorkflowRun/stage-state as canonical.
6. Reduce legacy queue-based rebase logic to compatibility-only use cases, or remove it if no remaining caller depends on it.
7. Add focused tests for: scheduling visibility, duplicate-click idempotency, approval-stage reopen, `shaChanged=false` no-op invalidation, `shaChanged=true` check reset, and task failure blocking.

Rollback is straightforward: revert the API route to queue-enqueue behavior and disable the `rebase-branch` task registration. Because the change is additive around runtime-added tasks, the legacy path can be preserved until the new path is proven stable.

## Open Questions

- The change-local `specs/` directory is currently empty. Before implementation starts, which exact deltas will be written for `workflow-run`, `workflow-engine`, `http-api`, and `web-ui`, and do they require any stage-specific invalidation rules beyond Check?
- Should the new application command be a generic `scheduleAdHocTask(...)` API or a narrower `scheduleRebaseTask(...)` method for this slice, with generalization deferred until another runtime-added user action needs it?
- For `build`, is the intended policy truly “no invalidation unless a concrete Build-stage check/task depends on snapshot identity,” or is there an existing accepted spec delta that should explicitly reset part of Build after `shaChanged=true`?
