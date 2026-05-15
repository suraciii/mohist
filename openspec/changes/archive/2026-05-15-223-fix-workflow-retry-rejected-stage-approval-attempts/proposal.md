## Why

Rejecting an approval-gated stage currently records the user's feedback and says the pipeline restarted, but the queued `resume-pipeline` task can skip execution because the issue projection is already `blocked`. This breaks the review loop: rejection must reliably retry the current stage so the next stage attempt receives and acts on the feedback.

## What Changes

- Make `resume-pipeline` distinguish genuinely blocked issues from blocked projections backed by a retryable latest `WorkflowRun` failure at the issue's current stage.
- Treat approval rejection of the current stage as a retryable failure only when `WorkflowRun.retryStage(stage)` accepts that same stage retry.
- Allow retryable current-stage rejection reruns to proceed through the normal workflow runner instead of completing the queue task as `skipped`.
- Preserve blocked/non-runnable behavior for issues without a retryable current-stage failed `WorkflowRun`.
- Ensure rejected Plan approval in the built-in workflow starts a fresh Plan-stage runner/coder session, carries the rejection feedback into the retried attempt context, regenerates artifacts, and requests approval again.
- Add regression coverage for approval rejection followed by queued resume and for non-retryable blocked issues still being skipped.

## Capabilities

### New Capabilities

- None

### Modified Capabilities

- `workflow-run` — Current-stage approval rejection failure must remain retryable through the WorkflowRun aggregate, and retry must restart the same stage without bypassing approval.
- `pipeline-model` — Blocked issue projections must not prevent retryable current-stage rejection reruns, while genuinely blocked issues remain non-runnable.
- `coder-session-tracking` — Agent-backed same-stage retries after rejected approval must be observable as a new stage attempt/session rather than reusing the prior session.

## Impact

- `packages/cli/src/services/agent-runner-service.ts` — adjust `executeResumePipelineTask` blocked-issue handling so retryable current-stage failed runs can enter `runPipelineToCompletion` instead of being marked `skipped`.
- `packages/cli/src/services/workflow-application-service.ts` and `packages/cli/src/workflow/domain/` — use existing `WorkflowRun.retryStage(stage)` semantics as the retryability boundary and avoid broad unblocking of arbitrary blocked issues.
- `packages/cli/src/api/issues.ts` — rejection flow continues to record feedback through WorkflowRun and enqueue `resume-pipeline`; user-facing restart behavior must match actual execution.
- `packages/cli/src/workflow/` stage runners and prompt/context assembly — ensure the retried stage receives recorded rejection feedback and requests approval again after regenerated artifacts.
- `packages/cli/src/db/issue-task-queue-repo.ts`, `workflow-run-repo.ts`, and related projections — queue results and WorkflowRun state must show an executed retry rather than a skipped resume for retryable rejection failures.
- `packages/cli/tests/` — add regression tests for Plan approval rejection retry and for blocked issues without retryable current-stage failures staying skipped/non-runnable.
