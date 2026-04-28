## Why

When an agent pipeline is running (status=active), the IssueDetailPage shows only a Close button — the user has zero visibility into what stage the agent is in, which build task it's executing, or when it last produced output. This makes multi-minute pipeline runs a blind spot where users cannot monitor progress or intervene when things go wrong.

## What Changes

- Extend `AgentStatus` API response with pipeline progress metadata: current stage, build task progress (completed/total from tasks.json), current round index (for Plan/Review), and last-activity timestamp from the most recent workflow_log entry or SSE event.
- Add `RunningAgent` fields to carry this progress data, updated in-band as the pipeline executes (no polling).
- Replace the lone Close button on IssueDetailPage with a live progress panel showing stage/round/task info and last-activity time, plus a Force Stop button that terminates the child process.
- Add `POST /api/issues/:number/force-stop` endpoint that kills the agent's child process, sets issue status to `interrupted`, and cleans up resources.
- Store the ACP child process reference in `RunningAgent` to enable Force Stop.
- Add a 5-second defensive timeout to `cleanup()` in `acp-session.ts` to prevent theoretical hangs on stream cancellation.

## Capabilities

### New Capabilities

- `agent-progress-tracking`: AgentStatus API and RunningAgent enriched with stage/round/task-progress/last-activity metadata; progress updated in-band during pipeline execution.
- `agent-force-stop`: API endpoint and logic to forcefully terminate a running agent's child process, set issue to interrupted, and clean up.

### Modified Capabilities

- `web-ui`: IssueDetailPage replaces Close-only button with live progress panel + Force Stop action for active agents.

## Impact

- **API**: `GET /api/agent/status` response shape expands (backward-compatible additions). New `POST /api/issues/:number/force-stop` endpoint.
- **Services**: `AgentRunnerService` — `RunningAgent` gains progress fields + child process ref; `getStatus()` returns richer data.
- **Workflow**: `WorkflowController` / `RalphExecutor` — pipeline stages report progress updates to `RunningAgent` as they execute.
- **Frontend**: `IssueDetailPage.tsx` button area refactored; new progress display component.
- **Agent Runtime**: `acp-session.ts` `cleanup()` gains timeout wrapper; child process ref exposed via `RunningAgent`.
- **Related specs**: `agent-pool`, `http-api`, `agent-session-ui`, `pipeline-session-events`, `error-resilience` may need minor delta adjustments.
