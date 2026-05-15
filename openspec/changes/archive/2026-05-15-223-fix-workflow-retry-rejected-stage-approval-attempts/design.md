## Context

Stage approval rejection is already represented in the `WorkflowRun` aggregate: `rejectStage(stage)` records a rejected approval, fails the current stage with `approval-rejected`, and persists that failed run through projection. `WorkflowRun.retryStage(stage)` is also already the semantic boundary for whether a failed current stage may be retried.

The failure happens one layer above the aggregate. `POST /issues/:number/reject` records the rejection and enqueues `resume-pipeline`, but projection marks the issue `blocked` before the queue task runs. `AgentRunnerService.executeResumePipelineTask` currently treats most blocked issues as non-runnable and completes the queue task as `skipped`, so `WorkflowEngine.run()` never reaches the existing retry path that calls `WorkflowApplicationService.retryStage()` for a latest failed run at the current stage.

The implementation must keep `plan` as a normal `Stage.name`, avoid custom-stage abstractions, and avoid broadly unblocking arbitrary blocked issues.

## Goals / Non-Goals

**Goals:**

- Let `resume-pipeline` run a blocked issue when the latest `WorkflowRun` is failed at the issue's current stage and that same stage is retryable by `WorkflowRun.retryStage(stage)`.
- Keep genuinely blocked issues skipped/non-runnable when no retryable current-stage failed run exists.
- Ensure approval rejection feedback is recorded as rejection feedback, not lost behind the original approval request output.
- Ensure a rejected Plan approval retry starts a new agent-backed Plan attempt/session, regenerates Plan artifacts, and requests approval again.
- Cover the queue-worker regression and non-retryable blocked case with tests.

**Non-Goals:**

- Do not introduce a first-class `StageAttempt` storage redesign.
- Do not model Plan as a dedicated domain concept.
- Do not change approval gate semantics or bypass approval after retry.
- Do not make all blocked issues resumable.
- Do not add a custom-stage framework.

## Decisions

### D1: Use `WorkflowRun.retryStage(stage)` As The Retryability Boundary

`resume-pipeline` should not duplicate stage-specific retry rules. Add a small service-level predicate or helper that loads the latest aggregate for the issue and determines whether the issue's current stage can be retried by the same rules as `WorkflowRun.retryStage(stage)`.

The helper should require all of the following:

- The latest `WorkflowRun` exists.
- The latest run status is `failed`.
- The latest run `currentStage` equals `issue.stage`.
- Calling the domain retry semantics for that stage would be accepted, without committing state as part of the predicate.

The implementation can satisfy the last condition by either using a cloned/snapshot-loaded aggregate for a dry-run check or by introducing a narrow domain method such as `canRetryStage(stage)` that shares the same guards as `retryStage(stage)`. If `canRetryStage(stage)` is added, it should not encode approval-specific or Plan-specific rules; it should only expose the aggregate's existing retry guard.

**Alternatives considered:** Checking only `latestRun.status === failed && currentStage === issue.stage` is simpler, and `WorkflowEngine` already uses that shape today. It is weaker than the issue's retryability definition because it assumes all current-stage failures are retryable. Calling `retryStage()` directly inside the queue skip predicate was rejected because retryability evaluation should not mutate or persist state before the runner starts.

### D2: Let Retryable Blocked Issues Enter Normal Pipeline Execution

Change `AgentRunnerService.executeResumePipelineTask` blocked handling from a binary skip/unblock decision to this order:

1. If the current-stage approval is approved, keep the existing approved-continuation behavior and clear blocked state.
2. Else if the latest failed current-stage `WorkflowRun` is retryable, do not complete the queue task as `skipped`; allow execution to continue to worktree resolution and `runPipelineToCompletion`.
3. Else preserve the existing blocked skip behavior.

`runPipelineToCompletion` already marks the issue active at the start of actual execution, and `WorkflowEngine.runAggregateWorkflow` already calls `WorkflowApplicationService.retryStage({ stage: issue.stage })` when the latest run is failed at the current stage. Reusing this path keeps the queue worker shallow and leaves stage decisions in the workflow runtime.

**Alternatives considered:** Retrying the aggregate directly inside `executeResumePipelineTask` was rejected because it would split retry startup between the queue worker and `WorkflowEngine`. Broadly clearing `blocked` before checking aggregate retryability was rejected because it would make unrelated blocked issues runnable.

### D3: Preserve Rejection Feedback As The User Response

The rejection API should pass the user's rejection feedback into `WorkflowApplicationService.rejectStage()` as the rejection approval output. If the prior approval request output is still useful for audit, wrap both values in a structured object rather than replacing user feedback with the old request output.

For example, the rejected approval output may carry `feedback` plus optional `approvalContext` from the prior approval state. Stage prompt/context code should read this rejected approval output from the latest failed `WorkflowRun` or its projections when constructing the retried stage input.

**Alternatives considered:** Relying only on issue comments was rejected because comments are not the WorkflowRun decision history and may not be included in stage prompt context. Reusing `issue.approvalState.output ?? message` was rejected because Plan approval output normally exists, causing concrete rejection feedback to be dropped.

### D4: Verify New Attempts Through Existing Observability

Do not add a `StageAttempt` table for this bug fix. Treat a retry as observable when the failed current stage is reset by the aggregate, the normal runner starts stage work again, and an agent-backed stage such as Plan creates a new `coder_session`/session stream after rejection.

Plan checkpoints and completed artifact shortcuts must not cause the retried Plan stage to immediately reuse old artifacts without giving the agent the rejection feedback. The rejection route already clears the Plan checkpoint; the retry path should also ensure aggregate task state and artifact execution semantics cause Plan work to run again when approval rejection requires regenerated artifacts.

**Alternatives considered:** Adding a durable stage-attempt model would make attempts explicit but is outside the scope and would increase migration and UI complexity. Deleting all existing Plan artifacts on rejection was rejected as the default design because the agent may need prior artifacts as context; the key invariant is a new attempt that regenerates reviewable artifacts, not blind file removal.

### D5: Test At The Queue Boundary And Aggregate Boundary

Add regression tests that reproduce the actual failure path: rejected current-stage approval, issue projected as blocked, queued `resume-pipeline`, and execution not marked `skipped`. The Plan regression should verify a new Plan session/runner invocation begins and that the retry attempt can see the rejection feedback.

Also add a negative test for a blocked issue that does not have a latest failed current-stage retryable run; its `resume-pipeline` task should still complete as `skipped` or remain non-runnable according to existing behavior.

**Alternatives considered:** Testing only `WorkflowRun.retryStage()` was rejected because that code path already works and would not catch the queue-level skip regression. Testing only through API rejection was rejected because the core bug is the worker's blocked/runnable decision after enqueue.

## Risks / Trade-offs

[Risk] A retryability predicate drifts from `retryStage(stage)` semantics. → Mitigation: implement it in the domain aggregate or as a dry-run against the aggregate rather than copying rules in `AgentRunnerService`.

[Risk] Allowing blocked issues to pass the queue guard could unintentionally resume unrelated failures. → Mitigation: require latest failed `WorkflowRun`, exact current-stage match, and aggregate retry acceptance before bypassing the blocked skip.

[Risk] Rejection feedback may be stored in a new structured shape that older prompt code does not read. → Mitigation: normalize feedback extraction in one helper that handles both string feedback and structured `{ feedback, approvalContext }` values.

[Risk] Plan retry may skip work because artifacts still exist. → Mitigation: ensure rejection retry invalidates the Plan checkpoint and resets WorkflowRun task/check/approval state so the normal Plan runner performs a new attempt and approval cycle.

[Risk] Tests that require real agent sessions may be slow or flaky. → Mitigation: use injected/mocked runners or session repositories for queue-level tests, and reserve real agent behavior for existing integration layers if available.

## Migration Plan

1. Add the aggregate-backed retryability helper or domain predicate.
2. Update `executeResumePipelineTask` blocked handling to allow retryable current-stage failed runs through to normal execution.
3. Update rejection recording so user feedback is persisted in WorkflowRun rejection output and remains available to retried stage context.
4. Add feedback extraction to the Plan-stage prompt/context path if current prompt assembly does not already include rejected approval output.
5. Add regression tests for rejected Plan approval retry and non-retryable blocked skip behavior.
6. Run the relevant workflow/API test suites and typecheck.

Rollback is code-only: revert the queue guard and rejection-feedback changes. No data migration is required because existing rejected runs remain valid; older records without structured feedback should continue to be handled by fallback extraction.

## Open Questions

- Should the domain expose `canRetryStage(stage)` directly, or should retryability be checked by loading a throwaway aggregate instance and dry-running `retryStage(stage)`? The direct domain predicate is cleaner if it shares guards with `retryStage(stage)` and stays mutation-free.
