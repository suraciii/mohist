## Why

The Session activity model now records terminal execution facts as `session.activity`, but retired `session.closed` and `session.followup_*` names remain accepted or displayed across Server and Web paths. This leaves a dead compatibility surface and causes event feeds to describe current facts with an obsolete name, making the active contract unclear.

## What Changes

- **BREAKING** Remove `session.closed`, `session.followup_completed`, and `session.followup_failed` from the accepted Session runtime-event and transcript vocabulary; only the current Session event set remains supported.
- Remove the corresponding retired event types, subscription entries, labels, and view branches from the Web so each current Session event has one consistent handling path.
- Project and issue event feeds will represent terminal Session activity as `session.activity`, retaining the terminal status and failure details that make those entries visible and actionable.
- Update or remove tests that exercise retired names; retain coverage that terminal Session activity is persisted, visible in both feeds, and does not change activity-based command eligibility.
- Do not migrate or delete historical persisted transcript data.

## Capabilities

- `agent-session-runtime-event-vocabulary`: Agent Sessions accept, persist, and expose only the current runtime-event vocabulary; retired terminal and follow-up event names are no longer part of the runtime, transcript, or Web event contracts.
- `agent-session-terminal-feed-vocabulary`: Project and issue event feeds expose terminal AgentSession activity with the current `session.activity` type and terminal context, and the Web presents it consistently.

## Impact

- **Server Session domain:** runtime event recognition, transcript-part mapping, and related Server tests in `packages/server` remove retired Session event handling.
- **AgentOps feeds:** `ProjectEventFeedAssembler` and `IssueEventFeedAssembler` change the user-visible terminal Session event type while retaining their current source, ordering, and payload context.
- **Web:** canonical event types, live-event payload types, transcript/session views, activity-feed labels, and tests in `packages/web` remove retired branches and render current terminal activity.
- **APIs and dependencies:** project and issue event-feed consumers observe `session.activity` instead of `session.closed`; no new dependency, persistence migration, runner behavior, or activity-state decision logic is introduced.
