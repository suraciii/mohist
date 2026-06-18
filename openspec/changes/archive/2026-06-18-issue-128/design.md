## Context

Mohist is a **C#/.NET** Orleans + EF Core (SQLite) + ASP.NET Core minimal-API application with a `System.CommandLine` CLI. (The proposal's prose mentions `createIssueRoutes`/Hono/`ProjectService.getCurrentId()` — that naming is stale TS; the real patterns are below.)

Today, "giving an issue a role" means hand-pasting a JSON blob into `config.jsonc`, a project profile, or `vars.agent`. There is no project-scoped, named, reusable role definition. This change introduces a **project-scoped Agent aggregate** with full CRUD, backed by a new Orleans grain, an EF table, an HTTP API, and a CLI command group. It is the "define and manage" layer only — execution ("run an agent by name") is #126.

Reference patterns (the implementation will mirror these directly):
- **Grain**: `IssueGrain` (`packages/server/src/Mohist.Server/Issue/Grains/IssueGrain.cs`) uses `IStateStore<Domain.Issue>` + `OnActivateAsync` load + `GetPrimaryKeyString()`. **`EpicGrain`** (`.../Epic/Grains/EpicGrain.cs`) is the closer precedent because it already encodes project scope in the grain key (`projectId:epicId`) and parses it per-method — exactly what Agent needs.
- **EF**: Aggregates are stored as JSON `State` with hot query columns exposed via SQLite `json_extract` computed columns + a unique composite index (`MohistDbContext.cs` L188–203 for `Issues`).
- **HTTP**: `static class XxxRoutes { MapXxxRoutes(this WebApplication) }` builds a `RouteGroupBuilder` under `/api/projects/{projectRef}/...`, attaches `ProjectResolutionEndpointFilter`, and resolves the project via `HttpContext.GetResolvedProject()`. Mounted in `MohistApiRegistration.cs` L14–28.
- **CLI**: `System.CommandLine` `Command` groups assembled in `MohistCliCommands.cs`; `MohistCliApi.Print*Async` is the shared HTTP client; `BodyInputResolver` handles `@file`/stdin input and is body-agnostic (reusable for `--instructions`).

Constraints: proposal and specs forbid touching `IssueGrain` startup path, `IssueVariableBuilder`, `BuildAgentConfig`, and the legacy `IssueInfo.AgentConfig` attribute.

## Goals / Non-Goals

**Goals:**
- Deliver a project-scoped Agent aggregate with create/read/update/soft-delete, persisted via a new `AgentGrain` + `Agents` table.
- Expose CRUD through an HTTP API and a `mo agent` CLI group, both aligned with the existing `Issue` surface conventions.
- Enforce per-project `name` uniqueness that survives archival (archived names stay permanently occupied).
- Reuse established patterns (grain, store, EF JSON-blob, route group, CLI command) to keep the change small and consistent.

**Non-Goals:**
- No "run an agent by name" (execution engine is #126). `maxConcurrentRuns` and `skills` are stored as opaque metadata only; this layer does not enforce or consume them.
- No Web UI, no authority/permission model, no run read-model / board / activity.
- No changes to `IssueGrain`, `IssueVariableBuilder`, `BuildAgentConfig`, or `IssueInfo.AgentConfig`.
- No runner-side skill isolation (ambient filesystem skills remain unchanged).

## Decisions

### D1. Follow `EpicGrain`'s project-scoped grain key, not `IssueGrain`'s bare key

The grain key SHALL be `projectId:agentId` (colon separator), parsed inside each `AgentGrain` method exactly as `EpicGrain` does. Add a `GrainKey.Agent(string projectId, string agentId) => $"{projectId}:{agentId}"` helper next to the existing `GrainKey.Issue`/`GrainKey.Epic`.

- **Rationale**: The spec requires cross-project isolation at activation. `IssueGrain` uses a bare `issueId` and reads `projectId` only from state — insufficient for Agent because two projects could collide if an `agentId` were ever reused. `EpicGrain` already solves this and is the in-repo precedent.
- **Alternative considered**: bare `agentId` with project enforced only by the store/querier. Rejected — loses isolation guarantees and diverges from the established project-scoped pattern.

> Note on separator: the spec prose wrote `projectId|agentId`; this design uses `:` to match the existing `EpicGrain` convention. The separator is an internal detail, not observable via the API/CLI.

### D2. Agent identity = GUID-based `id` + user-facing `name`

- `id` = `agent_{Guid:N}` (system-assigned, stable grain/persistence key).
- `name` = the user-facing natural key (unique per project, incl. archived).
- API routes use `{id}` as the path segment; CLI accepts **name-or-id** and resolves to id via a querier before calling the grain.

- **Rationale**: Decoupling the immutable grain key (`id`) from the human label (`name`) means renames don't invalidate grain activations or historical references, while still giving users a friendly key. This matches `Issue` (number is user-facing, `issue_{guid}` is the grain key).
- **Alternative considered**: use `name` directly as the grain key. Rejected — renames would require grain re-activation/migration and would break archived-name occupancy guarantees.

### D3. EF storage mirrors `IssueRow`: JSON `State` + computed columns + unique index

Add an `AgentRow` (`Id`, `State`, computed `ProjectId`, `Name`, `Status`) and register it in `MohistDbContext` mirroring the `Issues` config (L188–203): store the whole aggregate as JSON `State`; expose `ProjectId`/`Name` as `json_extract` computed columns; enforce uniqueness via `HasIndex(e => new { e.ProjectId, e.Name }).IsUnique()`.

- **Rationale**: This is the codebase standard — it keeps the migration surface tiny (one JSON column + a few computed mirrors) and gives the DB-level uniqueness guarantee the spec demands. Because archived rows stay in the same table (soft delete), the composite index naturally covers them — no extra "occupied names" table needed.
- **Alternative considered**: one relational column per field. Rejected — heavier migrations, diverges from the established JSON-blob pattern, and offers no benefit for a CRUD-only aggregate.

### D4. Route prefix `/api/projects/{projectRef}/agents` (plural, under the project group)

Mount a new `AgentDefinitionRoutes` group at `/api/projects/{projectRef}/agents` using `ProjectResolutionEndpointFilter` and `GetResolvedProject()` — exactly mirroring `issues` and `epics`.

- **Rationale**: A singular `AgentRoutes` already exists at `/api/projects/{projectRef}/agent` (runtime status/sessions). Plural `agents` under the same project group reuses the established project-context machinery and avoids the path clash. The spec's "project scope taken from the current project context" maps cleanly onto the existing filter.
- **Alternative considered**: top-level `/api/agents` reading project from a global context. Rejected — every other project-scoped resource lives under `/api/projects/{projectRef}/...`; diverging would require a parallel resolution path.

### D5. `agentConfig` stored verbatim as opaque JSON; do NOT call `BuildAgentConfig`

`BuildAgentConfig` is `private static` and the proposal mandates it remain unchanged. Therefore the API/CLI accept `agentConfig` JSON from the user and the server persists it as-is, validating only that it is well-formed JSON. No `type=opencode` seeding in v1.

- **Rationale**: Respects the "leave `BuildAgentConfig` / `IssueVariableBuilder` untouched" constraint and keeps this layer a pure define/manage surface. Semantic validation (model format, opencode fields) belongs to the #126 consumption layer.
- **Alternative considered**: extract `BuildAgentConfig` to a public helper and seed defaults on create. Rejected for v1 — adds scope and couples this layer to the workflow-profile internals. Capture as an open question.

### D6. List/show go through an `AgentQuerier`, not `IStateStore<List>`

`IssueStore` leaves `ListAsync`/`DeleteAsync` unimplemented; reads go through `IssueQuerier`. Add an `AgentQuerier` (and a thin `AgentInfo` DTO) that reads `AgentRow`s directly via `IDbContextFactory<MohistDbContext>`, supporting `status` filtering and name/id lookup.

- **Rationale**: Consistency with the existing Issue pattern, and list filtering with computed columns is trivially expressible in EF LINQ.
- **Alternative considered**: implement `ListAsync` on `AgentStore`. Rejected — would diverge from the Issue precedent without benefit.

### D7. Name uniqueness: DB unique index is the source of truth, grain does a best-effort pre-check

The grain checks for an existing row with the target name before writing, but the `UNIQUE(ProjectId, Name)` index is the authoritative guard. Handlers catch the SQLite unique-constraint violation and map it to HTTP 409.

- **Rationale**: Grain-level check alone cannot close the check-then-act race across concurrent activations; the DB index closes it definitively. This matches how `Issues.(ProjectId, Number)` uniqueness is enforced.

### D8. Soft delete = `status` flip in-place; no hard delete, no archived table

`DELETE` sets `Status = "archived"` on the existing row and refreshes `updatedAt`. The row (and its name) remain, so the unique index keeps the name occupied.

- **Rationale**: Simplest implementation that satisfies "archived name permanently occupied." A separate archived table would require a second uniqueness domain and complicate reads.

### D9. CLI mirrors `mo issue`; `--instructions` reuses `BodyInputResolver`

Add `MohistCliCommands.Agent.cs` (`IssueCommands.Build` shape) registering `create/list/show/update/delete`. `--instructions` accepts literal / `@file` / `--instructions-stdin` via the existing `BodyInputResolver` (already body-agnostic). Register with `root.Subcommands.Add(AgentCommands.Build(api))` in `MohistCliCommands.cs`. Name-or-id is resolved client-side by calling the list/show endpoint.

- **Rationale**: Maximizes consistency with `mo issue` and reuses tested input-handling code.

## Risks / Trade-offs

- `[Name-uniqueness race between concurrent creates] -> Mitigation`: the `UNIQUE(ProjectId, Name)` DB index is authoritative; handlers map the constraint violation to HTTP 409 (D7). Grain pre-check reduces common-case latency but is not the guard.
- `[Confusion with existing singular /agent runtime route] -> Mitigation`: use plural `/agents` under the project group (D4); document the distinction in route XML comments and CLI help.
- `[BuildAgentConfig is private; can't reuse to seed defaults] -> Mitigation`: store `agentConfig` verbatim from user input (D5); defer seeding/validation to #126. Trade-off: v1 users must supply the full config blob.
- `[Grain key separator divergence (| vs :)] -> Mitigation`: use `:` to match `EpicGrain`; separator is internal, not API-observable. Documented here to avoid bikeshedding at implementation time.
- `[EF migration rollback must be clean] -> Mitigation`: the `Agents` table has no inbound FKs, so `Down` is a single `DropTable`. Verify both directions in the migration test (Acceptance Criteria requires this).
- `[CLI name-or-id resolution requires an extra round-trip] -> Mitigation`: acceptable for a management command; the querier lookup is cheap. Could later add a dedicated resolver if latency matters.
- `[List query scans JSON via computed columns] -> Mitigation`: the computed `ProjectId`/`Name`/`Status` columns are indexed/filtered as normal columns; no full-JSON scan needed for list filters.

## Migration Plan

1. **Code**: Add `Agent` domain entity, `IAgentGrain`/`AgentGrain`, `AgentStore` (`IStateStore<Agent>`), `AgentRow` + DbContext config, `AgentQuerier`/`AgentInfo`, `AgentDefinitionRoutes` (+ DTOs), CLI `AgentCommands`. Register store in `MohistServiceRegistration.cs` (next to `IStateStore<Issue>`) and mount routes in `MohistApiRegistration.cs`.
2. **EF migration**: `dotnet ef migrations add AddAgentsTable` from the server project. Forward `Up` creates the `Agents` table (`Id` PK, `State` JSON, computed `ProjectId`/`Name`/`Status` columns, `UNIQUE(ProjectId, Name)`). `Down` drops it. No data backfill (new feature). No FK to `Issues`.
3. **Deploy**: standard server restart; the migration applies on startup. No coordination with #126 needed (this layer ships independently).
4. **Rollback**: revert the code, apply migration `Down`. Because there are no inbound FKs and no data of value yet, rollback is lossless. Existing `Issues` schema and all other tables are untouched.

## Open Questions

- **Default `agentConfig` seeding**: Should create seed `{ "type": "opencode" }` when `agentConfig` is omitted, or require the user to always supply it? Current decision: require/allow null in v1 (D5); revisit if #126 expects a default.
- **`agentConfig` format validation**: Should the API reject malformed model strings (non-`provider/model`) here, or defer entirely to #126? Current decision: defer — store as opaque JSON.
- **Agent → Epic-style counter**: Agents use GUID ids and have no monotonic number (unlike Issues). Confirm no listing-ordering requirement depends on a numeric sequence; default list ordering will be `updatedAt DESC` (matching `Issue` list).
