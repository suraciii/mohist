## Why

Today, giving an issue a particular role (e.g. "senior reviewer" or "exploratory coder") means hand-pasting a JSON blob into `config.jsonc`, a project profile, or an issue's `vars.agent`. Prompts get rewritten each time, names don't persist, and definitions are copy-pasted across issues until they drift. We need to sediment a **role** (system prompt + model + concurrency cap) into a project-scoped, **named, reusable** entity: create it once, pull it back by name for any issue later. This issue only delivers "define and manage the agent entity"; actually "running an agent by name" is #126.

## What Changes

- Introduce a **project-scoped Agent aggregate** as a named, reusable role definition with full CRUD.
- Add top-level CLI verbs `mo agent create / list / show / update / delete`, mirroring the existing `mo issue` command pattern.
- Add the matching HTTP API surface (`POST/GET/PATCH/DELETE /agents`), aligned with the `IssueGrain` / `IIssueGrain` external shape.
- An Agent persists: `name`, `description`, `instructions` (free-text system prompt, stored verbatim, no template rendering), `agentConfig` (model + opencode settings, reusing the JSON shape produced by `MohistIssueWorkflowProfileBase.BuildAgentConfig`), `skills` (declarative metadata only — runner does not consume it in v1), `maxConcurrentRuns` (soft cap metadata, enforced in #126), and `status` (`active`/`archived`).
- `delete` is **soft** (`status=archived`); there is no hard delete. An archived `name` is permanently occupied and cannot be reused by a new agent.
- `name` is unique within a project, including archived agents.
- Add a new `AgentGrain : Grain, IAgentGrain` reusing `IssueGrain`'s persistence pattern (`IStateStore<Agent>` + `OnActivateAsync` load + `GetPrimaryKeyString()`). The grain key encodes project scope (e.g. `projectId|agentId`) to guarantee cross-project isolation.
- Add a new `Agents` table + EF migration with both forward-apply and clean-rollback scripts. No foreign key to `Issues` (Agent is referenced by id/name, not strongly bound).

## Capabilities

### New Capabilities

- `agent-definitions`: Project-scoped Agent aggregate — domain model (fields, `name` uniqueness including archived, soft-delete semantics), `AgentGrain` / `IAgentGrain` persistence reusing the `IssueGrain` pattern, grain-key project scoping, and the `Agents` table / EF migration contract.

### Modified Capabilities

- `http-api`: New `/agents` endpoint group (`POST /agents`, `GET /agents`, `GET /agents/{id}`, `PATCH /agents/{id}`, `DELETE /agents/{id}`) with project scope taken from the current project context, name-conflict 409 semantics, and list filtering by `status`.
- `cli-interface`: New top-level `mo agent` command group (`create / list / show / update / delete`) following the `mo issue` verb model, including `--all` / `--status archived` list filters and name-or-id resolution.

## Impact

- **New aggregate & persistence**: `Agent` entity, `AgentGrain` / `IAgentGrain`, new `Agents` table, EF migration (forward + rollback), grain key encoding project scope.
- **HTTP API**: New `/api/agents` route group; project context resolution reuses the existing current-project mechanism.
- **CLI**: New `mo agent` command group and shared `apiClient` usage; mirrors `mo issue` output conventions.
- **Reuses**: `agentConfig` reuses the `BuildAgentConfig` JSON shape (`Dictionary<string,object?>`) — **not** the legacy `IssueInfo.AgentConfig` attribute.
- **Unchanged (explicit non-goals)**: `IssueGrain` startup path, `IssueVariableBuilder`, `BuildAgentConfig`, `IssueInfo.AgentConfig`. No Web UI, no authority/permission model, no run read-model, no runner-side skill consumption — those belong to #126 and later issues.
- **Dependencies**: None. Ships in parallel with #126; this issue delivers only the define/manage layer.
