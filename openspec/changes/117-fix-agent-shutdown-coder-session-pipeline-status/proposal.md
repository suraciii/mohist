## Why

Agent hangs indefinitely in check stage (AI Review), server shutdown leaves orphan agent subprocesses and `coder_session` rows stuck in `running`, and `recoverIssues()`/`reopen` produce inconsistent issue states — causing multiple issues to display no Approve button and forcing users into broken reopen loops.

## What Changes

- `AgentRunnerService.shutdown()` aborts all active agents and clears internal maps (currently only unsubscribes from eventBus)
- `recoverIssues()` cleans up orphaned `coder_session` rows (`status=running` → `failed`) when marking issues as `interrupted`
- `executePipeline()` explicitly sets `issue.status = active` on start to guard against DB write races
- `runPipelineCheckStage()` wraps AI Review in an outer 30-minute stage timeout, independent of ACI SDK internal timeout
- `reopen` API for check-stage issues with all tasks passed (`isReviewRecovery=true`) sets approval gate directly instead of re-launching the agent

## Capabilities

### New Capabilities

- `pipeline-stage-timeout` — outer timeout guard for pipeline stages, ensuring no stage hangs forever regardless of underlying SDK behavior

### Modified Capabilities

- `server-daemon` — shutdown MUST abort active agents and clear in-memory state
- `coder-session-tracking` — `coder_session` rows MUST be cleaned up during crash recovery (`recoverIssues`)
- `reopen-resume` — reopen for check-stage issues with completed tasks MUST set approval gate directly, not re-run the agent
- `agent-pool` — `shutdown()` MUST abort all tracked active agents

## Impact

- `packages/cli/src/services/agent-runner-service.ts` — shutdown logic, recoverIssues cleanup, pipeline status guard, outer pipeline timeout
- `packages/cli/src/api/issues.ts` — reopen check-stage approval gate shortcut
- `packages/cli/src/agent-runtime/acp-session.ts` — pass `stage` into coder_session insert calls
