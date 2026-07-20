## Why

Workflow OpenCode turns complete without reporting their conversation events or terminal outcome to the associated AgentSession, leaving completed plan/build/check/integrate sessions empty and permanently shown as running. The shared runtime event projection and server persistence contract already exist for AgentJob turns, so Workflow turns must now provide the same observable execution record.

## What Changes

- Workflow OpenCode turns report the submitted user input and projected runtime events, including assistant text, reasoning, tool calls, usage, and resolved model facts, to their Workflow AgentSession through the existing workflow runtime-events route.
- A Workflow OpenCode turn reports `session.closed` with a completed or failed outcome when the turn ends, allowing the AgentSession status to converge instead of remaining running indefinitely.
- Runtime-event reporting is best-effort: an upload failure is observable but does not change or block the Workflow turn result.
- AgentJob session reporting remains unchanged, and no retry or local fallback is added for failed uploads.

## Capabilities

- `workflow-agent-session-transcript`: Workflow-source OpenCode turns persist and expose their user input, assistant output, reasoning, tool activity, usage, and model observations in the associated AgentSession without making event-upload failures determine the Workflow turn result.
- `workflow-agent-session-terminal-state`: Workflow-source AgentSessions reach completed or failed after each OpenCode turn through a recorded terminal event, while terminal-event upload remains best-effort and independent of Workflow task completion.

## Impact

- **Runner** (`packages/runner/src/actions/opencode.ts` and focused runner tests): the Workflow OpenCode Action observes projected turn events, sends the initial input and runtime facts to the existing Workflow AgentSession endpoint, and reports the terminal outcome without coupling upload success to the Action result.
- **Shared runtime** (`packages/runner/src/runtime/opencode/`): the existing `RuntimeTurnObserver` and normalized event projection are reused; changes, if any, remain limited to supporting reliable Workflow-side observation and ordering.
- **Server/API/persistence**: no endpoint, request shape, or persistence-model change; the existing Workflow runtime-events route and AgentSession transcript/status handling receive the previously missing events.
- **Web**: no UI contract or empty-state change; existing workflow session views begin showing the persisted transcript and converged status.
- **AgentJob execution**: no behavior change; its existing event-reporting path remains covered by regression tests.
