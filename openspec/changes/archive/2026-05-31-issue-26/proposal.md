## Why

Mohist currently can fail an ACP agent task as unresponsive even while the session is visibly streaming thought, tool, or other progress events. This makes long-running build tasks unreliable and confusing because users see ongoing transcript activity immediately followed by a liveness timeout and blocked workflow.

## What Changes

- Treat meaningful ACP session notifications as liveness activity across shared, resumed, new, and ephemeral session paths, not only assistant message chunks.
- Reset quiet timers and satisfy active probes when thought chunks, tool calls/results, assistant message chunks, or other forward-progress session notifications arrive.
- Keep task output accumulation independent from liveness activity, so final task artifacts may remain limited to assistant answer text.
- Expose probe state in session metadata/events so true timeouts are explainable, including probe sent time, deadline, and last qualifying activity after the probe.
- Add coverage for long-running shared ACP sessions that stream thought chunks without assistant message chunks and must not fail liveness.

## Capabilities

### New Capabilities

- None.

### Modified Capabilities

- `agent-runtime`: Agent session liveness requirements change to count all qualifying ACP progress notifications, not just assistant message chunks.
- `pipeline-session-events`: Session event/metadata requirements change to expose enough liveness probe state to explain probe sends, activity after probes, deadlines, and timeout failures.

## Impact

- Affects ACP runner liveness handling in `packages/runner/src/actions/acp-agent.ts` and any shared helper paths used by new, resumed, shared, and ephemeral ACP sessions.
- Affects session event or metadata payloads consumed by the server and Web UI for explainable liveness state.
- Affects runner tests for ACP session liveness and may add or update fixtures for thought/tool notification streams.
- No API-breaking change is intended; existing task output text contracts remain unchanged.
