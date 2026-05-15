## Why

Check approval is currently able to appear without durable evidence that the exact candidate implementation passed the configured full verification command. This weakens user approval decisions because AI review and merge-readiness can look complete even when the same candidate has not yet proven it can build and pass tests.

## What Changes

- Require Check to run the configured full verification gate before generating a new AI review, evaluating `review-passed`, evaluating `merge-ready`, or requesting user approval.
- Persist the Check full verification result as approval evidence with a stable check identity such as `health:check` or the compatible `build-test` name.
- Record verification command, status, duration, summary, and failure log excerpt in the persisted Check-stage evidence.
- Bind Check full verification evidence, AI review, merge-ready evidence, and approval eligibility to the same candidate implementation so stale evidence cannot support approval after the candidate changes.
- Block Check approval when full verification evidence is missing, failed, or stale, and surface the blocking reason to users instead of hiding it in internal logs.
- Keep `merge-ready` scoped to mergeability and keep Integrate `health:integrate` as the final post-merge safety net.
- Preserve existing `checks.buildTest` compatibility by mapping it to the Check full verification policy when the newer health gate configuration is not present.

## Capabilities

### New Capabilities

- None

### Modified Capabilities

- `workflow-engine` - Check ordering, candidate evidence freshness, approval gating, and stale evidence invalidation change so approval only follows verified candidate evidence.
- `workflow-run` - Check-stage runtime state must include durable full verification evidence alongside review and merge-ready checks for the same candidate implementation.
- `workflow-config` - Existing `checks.buildTest` configuration must remain usable or map clearly to the Check full verification health gate policy.
- `workflow-log` - Stage check evidence and diagnostics must expose Check full verification command metadata, summaries, and failure excerpts.
- `http-api` - Issue state and approval-related endpoints must expose failed or missing Check full verification evidence and must not allow approval of an unverifiable candidate.
- `web-ui` - Issue detail and approval surfaces must make failed or missing Check full verification evidence visible before approval.
- `cli-interface` - `mo issue show <number>` must surface failed or missing Check full verification evidence clearly.

## Impact

- **Check workflow**: `packages/cli/src/workflow/check-stage-runner.ts` must place full verification before the `ai-review` task and before `ReviewPassedCheck`, `MergeReadyCheck`, and `UserApprovalCheck`.
- **Health checks**: `packages/cli/src/workflow/checks/build-test-check.ts`, `health-gate-check.ts`, and related check orchestration must support Check-stage full verification evidence with command, duration, summary, and log excerpt.
- **Workflow runtime state**: WorkflowRun stage definitions and check result persistence must include Check full verification (`health:check` or compatible `build-test`) and bind it to the candidate snapshot used by review, merge-ready, and approval.
- **Configuration**: `packages/cli/src/workflow/workflow-loader.ts` must preserve `checks.buildTest` behavior or map it to the newer Check health gate policy.
- **Approval and stale evidence guards**: approval request/approve paths must reject missing, failed, or stale full verification evidence before transitioning Check toward Integrate.
- **User surfaces**: CLI issue display, issue APIs, and Web UI issue/approval panels must show failed or missing Check full verification evidence with enough command/log context for action.
- **Tests**: Add regression coverage proving verification failure prevents AI review, merge-ready, and approval, and proving passing verification evidence is persisted before review, merge-ready, and approval for the same candidate.
