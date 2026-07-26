## Why

`/api/agent/activity` is a polling read for operational oversight, but it currently rebuilds each returned Session's event summary from transcript records on every request. As transcript history grows, unchanged activity cards become progressively more expensive to serve, violating the requirement that polling cost grow only with currently relevant data.

## What Changes

- Persist the activity-card Session summary as part of Session state updates, so the current summary remains available without replaying transcript records on every activity-feed request.
- Have `/api/projects/{projectRef}/agent/activity` and its `/api/agent/activity` alias build card summaries from the persisted Session summaries.
- Preserve the existing activity-feed response schema, card ordering, activity semantics, preview behavior, and transcript detail endpoints.
- Keep amplification reporting truthful: repeated activity polling must not materialize transcript records solely to obtain Session summaries, and adding transcript history to unchanged Sessions must not increase that work.
- Prove that persisted summaries remain correct as runtime events update a Session, including model resolution, tool counts, and terminal failure facts.

## Capabilities

- `agent-activity-persistent-summary`: Agent Sessions maintain the summary required by the activity feed when their runtime observations are persisted, and the activity endpoints consume that summary without replaying transcript history while preserving their established visible behavior and amplification accounting.

## Impact

- **Server Session persistence and projection** (`packages/server/src/Mohist.Server/Sessions/` and `packages/server/src/Mohist.Server/Infrastructure/Data/Sessions/`): add a persisted Activity-feed summary owned by the Session write path.
- **AgentOps activity read** (`packages/server/src/Mohist.Server/AgentOps/Services/AgentActivityFeedAssembler.cs`): read the stored summary instead of reducing transcript parts for each request.
- **APIs** (`/api/projects/{projectRef}/agent/activity` and `/api/agent/activity`): response contract remains unchanged; performance and amplification behavior change.
- **Tests** (`packages/server/tests/Mohist.Server.SpecTests/Specs/Sessions/` and AgentOps activity specs): cover write-time summary correctness and activity polling with growing transcript history using explicit operation counts, not wall-clock timing.
- **Dependencies and persistence**: no new dependency; Session persistence changes and may require an EF migration for the durable summary shape.
