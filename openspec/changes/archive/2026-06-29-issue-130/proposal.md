## Why

#129 made it possible to launch a generic `AgentSession` from an Agent profile, but those sessions are effectively invisible once running. The `agent-id`/`agent-name` labels are stamped on generic sessions yet are neither queryable nor indexed (`AgentSessionQuery` falls into a `_ => false` branch for them), the project activity feed mis-attributes generic sessions to a synthetic `issue_{projectId}_0` card with no agent identity, and the active-agents status readout skips them entirely. Without a read layer, a user cannot answer "which sessions does this agent have, what is their status, and where did they fail" — which blocks the Agent workbench (#132) and the CLI from consuming direct-Agent usage. This change builds that visibility layer on top of the existing `AgentSession` transcript and state model, without introducing a new `AgentTask` read model.

## What Changes

- **Agent-scoped session list**: generic sessions become queryable by agent id/name, plus by `source-kind`, status, and context references (issue/epic/repository). This requires making `agent-id`, `agent-name`, and the `agent-launch/*` context-ref labels queryable and indexed (today only the 8 workflow-shaped `AgentSessionQueryMetadataKeys` are).
- **Agent workbench session list shape**: an agent profile exposes its recent / running / failed / ended sessions so the workbench can render the four states.
- **Session summary enrichment**: the generic session read path carries agent profile, status, created/last-activity, resolved model, usage, failure category, tool counts, and workspace/repository/context refs.
- **Activity integration**: direct Agent sessions appear as Agent activity with proper agent attribution (no synthetic issue-0 card), and sessions referencing an issue/epic surface as a lightweight association on those entities that links back to the session. The active-agents/capacity readout includes generic agent-launch sessions.
- **Stable API shape** for #132 (Web UI) and CLI consumption.

## Capabilities

### New Capabilities

- `agent-session-visibility`: Read/visibility model for direct-Agent-usage `AgentSession`s — agent-scoped session list & summary (query by agent, source, status, and context refs; summary carries agent profile, status, timing, resolved model, usage, failure category, tool counts, and context refs) and activity integration (a direct Agent session appears as Agent activity with agent attribution; an issue/epic reference surfaces as a lightweight association back to the session).

### Modified Capabilities

- `http-api`: Adds stable HTTP endpoint contracts for agent-scoped session listing, the generic session summary, and agent-session activity / issue-epic association reads, distinct from the existing workflow-session and launch endpoints.
- `cli-interface`: Adds CLI read commands that consume the new visibility API (list an agent's sessions, show a session summary), mirroring the launch-side CLI entry points.

## Impact

- **Server / Sessions query**: `AgentSessionQuery.QueryRowsByLabels` switch (`packages/server/src/Mohist.Server/Sessions/Services/AgentSessionQuery.cs:105`) and the computed label columns + index on `AgentSessionRow` (`AgentSessionRow.cs:16`) must admit `agent-id`, `agent-name`, and the `agent-launch/*` context-ref keys; a migration adds the new indexed columns. `AgentSessionQuerier` (`packages/server/src/Mohist.Server/Workflow/Services/Sessions/AgentSessionQuerier.cs`) gains an agent-scoped list method and an enriched generic-session summary.
- **Server / Activity**: `GetActivityAsync` (`AgentSessionQuerier.cs:382`) and `ToActivityCard` (`AgentSessionQuerier.cs:627`) stop synthesizing `issue_{projectId}_0` for generic sessions and instead attribute by agent; `ActivityCardDto` (`AgentSessionReadModels.cs:192`) gains agent identity. `WorkflowActivityQuerier.ListActiveAgentsAsync` (`WorkflowActivityQuerier.cs:25`) stops excluding records with blank `workflowRunId`/`workId`.
- **Server / API**: new endpoints alongside `AgentSessionFollowupRoutes` / `AgentRoutes` for agent-scoped listing, summary, activity, and issue/epic association reads.
- **Server / metadata keys**: `GenericAgentSessionMetadata` (`GenericAgentSessionMetadata.cs:36`) keys become first-class query keys.
- **CLI**: new read commands under the agent/session group in `packages/cli/Mohist.Cli/`.
- **Dependencies**: builds on completed #129 (generic session launch + metadata). No new external dependencies; no LLM-provider calls from Mohist.
