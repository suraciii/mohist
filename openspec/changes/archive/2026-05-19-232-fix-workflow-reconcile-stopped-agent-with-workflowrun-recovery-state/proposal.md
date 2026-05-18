## Why

Mohist can leave a work item's latest execution attempt appearing `Running` after the agent was stopped or its process disappeared, causing the UI to offer `Retry` while the API rejects retry because the WorkflowRun is still `running`. This change is needed now because recovery guidance must be consistent and trustworthy when execution is interrupted: users need to know whether to wait, retry failed work, resume interrupted work, rerun a stage, or inspect evidence.

## What Changes

- Model execution attempts as belonging to stage work items, not to the WorkflowRun as a whole.
- Make the latest work item attempt state the source of truth for recovery action availability.
- Introduce `Interrupted` as a distinct latest-attempt condition for stopped or lost execution, separate from `Failed`.
- Require a `Running` latest attempt to have live execution evidence, such as an active queue task or live agent process.
- Reconcile stale `Running` attempts before exposing primary recovery actions through UI, CLI, or API surfaces.
- Derive workflow-level recovery state from current stage/work progress so WorkflowRun status cannot contradict the current work item's latest attempt.
- Restrict `Retry` to genuinely failed latest attempts; interrupted work should guide users toward resume, rerun, or inspection instead.
- Keep historical stop, interruption, and failure evidence visible while preventing stale runtime evidence from driving current recovery decisions.

## Capabilities

### New Capabilities


### Modified Capabilities

- workflow-run
- workflow-engine
- http-api
- web-ui
- cli-interface
- coder-session-tracking
- ralph-task-execution

## Impact

- Workflow domain and persistence: `WorkflowRun`, `StageRun`, task/check work item state, snapshot repair/hydration, retry/rerun decisions, and projection logic must represent latest attempt state and derive run-level recovery state from work-level evidence.
- Recovery services and queue handling: `WorkflowApplicationService`, `AgentRunnerService`, issue task queue recovery, stopped-agent cleanup, and resume/retry/rerun flows must reconcile stale running attempts before reporting or accepting recovery actions.
- Runtime evidence sources: queue task state, coder session records, agent process PIDs, and session observers must be used to prove whether a `Running` attempt is still live, without becoming a new first-class domain entity.
- HTTP API: issue retry/resume/rerun endpoints, issue detail data, stage-state/workflow-run responses, and queue status endpoints must agree on attempt-derived recovery action availability and return actionable conflicts when the requested verb does not match the latest attempt state.
- Web UI: Issue Detail action rendering must stop showing `Retry` solely because `issue.status === blocked`; recovery controls and messages must align with the latest attempt state and backend availability.
- CLI: issue status/show/recovery commands must present the same recovery guidance as API and UI, including distinguishing failed from interrupted work.
- Tests: add regression coverage for the #229 stale-running shape and for a genuine failed task/check attempt that remains retryable.
