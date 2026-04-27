## Why

The close, reopen, approve, and reject endpoints all check `agentRunner.isRunning()` and return 409 when an agent is active, but there is no API to stop a running agent — the error message literally says "wait for it to complete or pause first" yet no pause/stop endpoint exists. This leaves users helpless when an agent is stuck or taking too long (e.g. Issue #2: 7-minute wait with repeated 409s).

## What Changes

- Add `POST /api/issues/:number/stop` — forcibly terminate the running ACP session (SIGTERM → SIGKILL) and clean up agent state
- Modify close/reopen/approve/reject handlers to accept a `force` query parameter that auto-stops a running agent before proceeding
- Remove the stale "Removed endpoints return 404" requirement for pause from `http-api` spec (replaced by stop)

## Capabilities

### New Capabilities

- `agent-stop-api` — REST endpoint to forcibly stop a running agent pipeline for a given issue

### Modified Capabilities

- `http-api` — close, reopen, approve, reject endpoints gain `force` parameter; pause endpoint removed from "removed endpoints" list
- `pipeline-model` — pipeline can be externally interrupted at any stage
- `agent-runtime` — AcpSession exposes a public `cancel()` method; AgentRunnerService exposes `stop(issueId)`

## Impact

- `packages/cli/src/api/issues.ts` — add stop route, modify close/reopen/approve/reject with force flag
- `packages/cli/src/services/agent-runner-service.ts` — add `stop(issueId)` method
- `packages/cli/src/agent-runtime/acp-session.ts` — expose `cancel()` as public API (already exists internally at line 382, 766)
- `openspec/specs/http-api/spec.md` — update requirements
- `openspec/specs/pipeline-model/spec.md` — add interrupt scenarios
- `openspec/specs/agent-runtime/spec.md` — add stop/cancel requirement
