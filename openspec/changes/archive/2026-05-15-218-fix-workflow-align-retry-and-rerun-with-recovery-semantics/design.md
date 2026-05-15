## Context

Mohist currently has three overlapping sources of recovery truth: the WorkflowRun aggregate, filesystem/checkpoint state, and stage-runner artifact skip logic. This creates ambiguous recovery behavior: `POST /api/issues/:number/retry` can reject a failed Plan run before `tasks.json` exists, `rerunStage()` resets from the first incomplete task instead of the first stage work item, and Plan rerun can be converted into a continue operation by file-exists checks.

The existing domain model already has most of the right boundary: WorkflowRun owns current stage, task/check state, failures, approvals, and next work. The design keeps that boundary and moves recovery availability and reset semantics toward the WorkflowRun/application-service layer, while leaving filesystem validation and queueing at the API/service edge.

## Goals / Non-Goals

**Goals:**

- Make `Retry` mean retry the current failed work in the latest failed WorkflowRun for the current stage.
- Make `Rerun Stage` mean discard the current stage attempt state and restart from the first work item in the same stage.
- Preserve earlier passed stages during rerun and preserve earlier valid same-stage work during retry.
- Stop using `tasks.json` or checkpoint existence as the primary retry availability signal.
- Prevent Plan stage rerun from silently skipping artifact work because old artifact files exist.
- Return specific recovery errors that API, CLI, and Web UI can surface directly.
- Keep recovery vocabulary limited to retry, rerun, and rewind.

**Non-Goals:**

- Do not add new recovery verbs or restore restart.
- Do not implement rewind.
- Do not change Integrate side-effect rollback semantics.
- Do not solve approval rejection retry behavior covered by #223.
- Do not build the Check review repair exhausted UI covered by #217.
- Do not change merge-ready product scope from #215.

## Decisions

### D1: WorkflowRun Is The Recovery Intent Source

`Retry` availability will be computed from the latest WorkflowRun whose status is `failed`, whose `currentStage` matches the issue stage, and whose current StageRun contains retryable failed work. The API may still validate required external resources, such as project, worktree, and change directory, but those checks are secondary preconditions and produce distinct errors.

The application layer should expose a small recovery-oriented method or result shape, for example a command that returns either `{ ok: true, decision }` or `{ ok: false, reason }`, so `issues.ts` no longer reconstructs retryability from checkpoint/filesystem state. The aggregate remains responsible for state transitions; API handlers remain responsible for HTTP status, queueing, and user-facing error messages.

**Alternatives considered:** Keep checkpoint/file checks in the API as the main retry gate. This preserves current behavior but repeats the bug class because retryability remains coupled to artifact existence instead of failed work. Add a separate RecoveryService. This may be useful later, but for this change it risks a shallow pass-through over WorkflowApplicationService unless more recovery verbs are introduced.

### D2: Model Retry As Failed-Work Reset, Not Stage Reset

`WorkflowRun.retryStage(stage)` should reopen only the current failed StageRun and reset from the failed work boundary. If the failed work is a task, reset that task plus later same-stage tasks/checks that depend on it or occur after it. If the failed work is a check, keep completed tasks and earlier passed checks, reset that check and later checks, and invalidate downstream repair/derived work caused by that failed check where applicable.

Retry should clear WorkflowRun failure and StageRun failure/approval state, set the WorkflowRun and StageRun back to running, and leave `currentStage` unchanged. It should not clear the whole stage checkpoint through domain state; checkpoint cleanup should be targeted to the failed work boundary where checkpoint data exists.

**Alternatives considered:** Continue using first incomplete work as retry boundary. This is close for failed tasks but incorrect for failed checks and hides whether retry is about failure or continuation. Reset the whole stage for retry. This is simpler but makes retry indistinguishable from rerun and loses useful successful work.

### D3: Model Rerun As Current-Stage Attempt Reset From Index Zero

`WorkflowRun.rerunStage(stage)` should be allowed for the current stage on running or latest failed runs after the application layer loads the correct aggregate. It should reset all task/check state in the current StageRun from the first work item, clear StageRun failure, approval, retry-derived state, and WorkflowRun failure, and set the stage/run to running. It must not change `currentStage` and must not modify earlier StageRuns that have already passed.

This makes rerun stronger than retry and gives the API a single operation to call regardless of whether the previous run is running, blocked/failed, or was recovered from a failed latest aggregate.

**Alternatives considered:** Implement rerun in API by deleting checkpoints and then calling `retryStage()` when `resumeDecision()` reports failed. This is the current split behavior and causes retry semantics to leak into rerun. Create a new WorkflowRun record per rerun. That would match the phrase stage attempt, but it is a larger persistence model change and unnecessary if StageRun state can represent a new attempt by clearing current-stage progress.

### D4: Stage Runners Must Honor Requested Work And Rerun Mode

Stage runners should execute the work selected by WorkflowRun rather than independently deciding to skip based only on files. For Plan, the runner already receives `ctx.requestedWork`; rerun should make the requested work begin at `proposal`, and Plan runner should not mark an artifact complete merely because the file exists when the current WorkflowRun task is pending due to rerun.

The default Plan artifact policy for rerun is regenerate. File-exists skip may still be valid for normal interruption resume when the WorkflowRun/checkpoint marks prior work completed, but it must not override pending WorkflowRun task state created by rerun. If future stages want artifact reuse during rerun, they need an explicit stage policy surfaced to users.

**Alternatives considered:** Delete existing Plan artifacts before rerun. This would force regeneration but risks data loss and makes rollback/debugging harder. Keep file-exists skip for all paths. This preserves compatibility but violates the user promise that rerun restarts the stage.

### D5: Recovery Errors Are Structured Internally And Plain In Responses

Internal recovery failures should use a small set of reason codes, such as `no-failed-workflow-run`, `stage-mismatch`, `no-retryable-failed-work`, `missing-project`, `missing-worktree`, and `missing-change-artifacts`. The API can map these to 404, 409, or 500 and a clear message. Web and CLI do not need to understand every code for this change; they must display the returned message without swallowing it.

This follows exception aggregation: domain/application code identifies recovery failure reasons once, and API/UI layers consistently surface them.

**Alternatives considered:** Throw generic errors from the aggregate and parse message text in the API. This is easy initially but brittle and makes localized user guidance hard. Add a new availability endpoint first. That may be useful later, but the acceptance criteria only require correct mutation behavior and visible errors.

### D6: UI Action Errors Use One Display Pattern

Issue detail should include `retryMutation.error` in the same action error block that already displays close, reopen, start, and rerun errors. This is intentionally minimal: retry error visibility is more important than introducing a new recovery panel in this change.

**Alternatives considered:** Add per-button inline errors. That would add more UI state and inconsistent placement. Add toast-only errors. Toasts can disappear and do not satisfy the need to inspect the reason while deciding the next action.

## Risks / Trade-offs

- [Risk] Resetting downstream work too broadly could discard valid work during retry. → Mitigation: implement reset from explicit failed task/check boundaries and cover task-failure and check-failure retry with domain tests.
- [Risk] Resetting downstream work too narrowly could reuse stale checks or repair tasks. → Mitigation: reset all later checks after a failed check and reset caused-by repair/derived tasks tied to the failed check.
- [Risk] Plan rerun regenerates over existing artifacts, losing prior draft content. → Mitigation: treat this as intentional rerun semantics; preserve git history/worktree files but do not use file existence as completion evidence for pending rerun work.
- [Risk] API still needs filesystem checks, which could reintroduce artifact-based retry gating. → Mitigation: separate WorkflowRun retryability from external-resource preconditions and use distinct errors for missing worktree/artifacts.
- [Risk] Existing checkpoint resume behavior may depend on Plan file-exists skip. → Mitigation: only disable file-exists skip when WorkflowRun task state says the work is pending for retry/rerun; keep normal checkpoint resume behavior.

## Migration Plan

1. Add focused WorkflowRun domain tests for retry failed task, retry failed check, and rerun current stage from first work.
2. Update WorkflowRun/StageRun reset helpers so retry and rerun have separate semantics.
3. Update WorkflowApplicationService to load latest failed runs for retry/rerun and return distinguishable recovery failure reasons.
4. Simplify `POST /api/issues/:number/retry` to use WorkflowRun recovery availability before queueing, then perform external project/worktree/change-dir checks with distinct errors.
5. Update `POST /api/issues/:number/rerun` to call true rerun semantics instead of falling back to retry semantics for failed latest runs.
6. Update Plan stage runner so pending WorkflowRun work created by rerun is executed even when artifact files already exist.
7. Add regression coverage for the #215 shape and update Web UI action error rendering to include retry errors.
8. Rollback strategy: revert the domain/application/API/UI changes together. The persisted WorkflowRun schema does not need migration; reset behavior changes only how existing task/check rows are updated.

## Open Questions

- None.
