## Why

`AgentSession` is Mohist's user-facing AI developer working session, but its current boundary still mixes runtime lifecycle, ACP SDK wiring, workflow visibility, EventBus emission, and DB persistence concerns. Cleaning this boundary now makes cancellation, timeout handling, recovery, live progress, and persisted session history safer to evolve across Plan, Build, Check, Explore, Skill, and conflict-resolution flows without accidental regressions in unrelated visibility systems.

## What Changes

- Refactor `AgentSessionOptions` so it contains runtime/session configuration only, with workflow visibility integrations supplied through `observers: SessionObserver[]` instead of direct `EventBus` or DB repo options.
- Remove `WorkflowSessionObserver` construction from `AgentSession`; workflow and service consumers create workflow visibility observers explicitly or through a workflow-layer helper.
- Preserve `WorkflowSessionObserver` as the adapter that maps session lifecycle, text chunks, tool calls, raw ACP updates, and state changes to EventBus events, workflow logs, session stream logs, and `coder_session` persistence.
- Keep `AgentSession` responsible for the domain lifecycle of an observable agent session: create, execute prompts, cancel, close, track state, aggregate output, enforce timeout/abort behavior, and notify observers.
- Optionally extract ACP SDK communication into a small internal driver/helper if it reduces `AgentSession` responsibility without introducing workflow, DB, EventBus, or issue/stage concepts into that adapter.
- Preserve existing frontend API shape, persistence schema, Plan and Check multi-round session reuse, task retry semantics, model override behavior, cleanup guarantees, and realtime/historical visibility behavior.
- No **BREAKING** external API change is intended; the expected breakage is limited to internal TypeScript construction sites and tests that currently pass workflow visibility dependencies through `AgentSessionOptions`.

## Capabilities

### New Capabilities


### Modified Capabilities

- `agent-runtime`
- `workflow-log`
- `pipeline-session-events`
- `coder-session-tracking`

## Impact

- `packages/cli/src/agent-runtime/agent-session.ts`: remove workflow visibility dependencies from options and imports, receive observers directly, keep lifecycle and cleanup behavior intact, and optionally delegate ACP SDK calls to a small runtime-only helper.
- `packages/cli/src/agent-runtime/session-observer.ts`: keep `SessionObserver` and `WorkflowSessionObserver` behavior, but avoid making workflow visibility observer construction part of the runtime boundary; may move workflow-specific observer implementation or factory to a workflow/service layer if needed.
- Workflow consumers including `workflow-engine.ts`, `stage-context.ts`, `plan-stage-runner.ts`, `build-stage-runner.ts`, `check-stage-runner.ts`, `checks/*`, and `openspec/ralph-executor.ts`: construct and pass observers while preserving Plan and Check multi-round session reuse.
- Service consumers including `agent-runner-service.ts`, `skill-service.ts`, `explore-acp-service.ts`, `conflict-resolution.ts`, and server entry points: stop passing EventBus and repo dependencies as runtime options and instead attach visibility observers where progress/logging is required.
- Visibility systems remain behaviorally unchanged: EventBus/SSE `coder_text_chunk`, `coder_tool_call`, `plan_round_start`, `plan_session_update`, workflow log rows, session stream log rows, and `coder_session` status updates continue to be produced through observers.
- Tests that construct or mock `AgentSessionOptions` need updates to the cleaned option shape and should cover abort, timeout, close cleanup, model override warning behavior, observer event delivery, and multi-round session reuse.
