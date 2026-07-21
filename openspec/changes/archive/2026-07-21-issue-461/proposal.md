## Why

Workflow turn events and follow-up user input are currently lost when their runner-to-server upload fails, leaving permanent gaps in AgentSession transcripts during transient network failures. These events must survive both delivery failures and runner restarts without delaying the work that produced them.

## What Changes

- Retain undelivered Workflow turn input, normalized runtime content, and terminal events in runner-local durable delivery state until the server accepts them.
- Give follow-up user-input events the same eventual-delivery guarantee; the existing durable reporting of follow-up terminal outcomes remains intact.
- Retry pending events after transient upload failures and resume delivery after runner restart or server reconnection, preserving each event's AgentSession target, runtime binding, and required turn order.
- Keep event delivery asynchronous from Workflow turn and follow-up execution so an unavailable server does not block or change their runtime result.
- Keep AgentJob transcript reporting unchanged; generic follow-up delivery does not add a cross-producer ordering guarantee relative to AgentJob's existing direct event uploads.
- Do not add server-side deduplication or idempotency behavior, transfer pending events between runners, or recover follow-up runtime execution interrupted by a runner crash.

## Capabilities

- `agent-session-runtime-event-delivery`: Durable, non-blocking, runner-local eventual delivery of Workflow turn events and follow-up user-input events across transient upload failures and runner restarts, preserving order within each outbox-managed producer sequence.
- `workflow-agent-session-transcript`: Workflow transcript reporting changes from best-effort, no-retry uploads to eventual delivery while preserving turn event order and independence from the Workflow result.
- `workflow-agent-session-terminal-state`: Workflow terminal events remain independent of task completion but are retained and retried so the associated AgentSession eventually converges to completed or failed.

## Impact

- **Runner event reporting** (`packages/runner/src/actions/workflow-agent-session-reporter.ts`, `packages/runner/src/actions/opencode.ts`, and `packages/runner/src/server/followup-handler.ts`): failed Workflow and follow-up input uploads enter a shared durable delivery path instead of being logged and discarded.
- **Runner lifecycle and local state** (`packages/runner/src/server/runner-signalr.ts` and runner-owned state under `.mohist/runner-state/`): startup and reconnection resume pending delivery; the local state contract expands beyond follow-up terminal outcomes.
- **Tests** (`packages/runner/tests/`): focused coverage must simulate upload failure, later recovery, non-blocking execution, ordered delivery, and restart recovery without real network, filesystem, or wall-clock dependencies.
- **Server APIs and Session persistence**: existing runtime-event endpoints, receipt behavior, binding validation, event allowlist, and persistence model remain unchanged. No new external dependency or cross-runner coordination is introduced.
- **AgentJob execution**: its existing direct AgentSession reporting path and cross-producer ordering relative to generic follow-up events remain unchanged.
