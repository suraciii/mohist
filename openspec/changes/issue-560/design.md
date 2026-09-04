## Context

Agent authoring and launch today are infrastructure-shaped (see `proposal.md` for motivation and `specs/` for requirements):

- **Definition model** (`packages/server/src/Mohist.Server/Agent/Domain/Agent.cs`): carries `Name`, `Description`, `Instructions`, `AgentConfig` (whitelisted to `model`/`variant`/`runtime` by `Infrastructure/AgentConfigSchema.cs`), `Skills`, `AllowedSubagentAgentIds`, `MaxConcurrentRuns`. There is no `Purpose` field and no permission declaration anywhere. Persisted as JSON `State` on `AgentRow` via the `AgentGrain` + `IStateStore`.
- **Authoring parity gap**: `AgentDefinitionRoutes` already accepts `description`, `maxConcurrentRuns`, `allowedSubagentAgentIds` — but the Web `AgentProfileEditor` sends only name/instructions/skills/agentConfig, while the CLI sets everything. Neither surface has purpose or permissions.
- **Readiness** (`Agent/Services/AgentReadinessService.cs`, `AgentReadiness.cs`): a three-conclusion projection (`Ready` / `Needs setup` / `Unknown`) that already combines structural gaps (instructions, model, runtime) with latest matching execution evidence, and already folds execution-config failures into "Needs setup". `Availability` is a separate service and endpoint. The launch gate (`EnsureLaunchableAsync`) rejects only "Needs setup".
- **Launch** (`Api/AgentSessionLaunchRoutes.cs`, `Agent/Services/AgentLauncher.cs`): the body binder already rejects undeclared fields (only `prompt`/`context`/`attachments`), resolves a workspace name (explicit > CLI default > Web per-session), validates issue/epic/workspace references, and snapshots model/runtime/instructions/skills/subagents into the immutable `AgentJobInput`. Context refs land in `GenericAgentSessionMetadata` and `AgentJobInput` fields — there is no single confirmed-scope projection, no caller confirmation step, and the repository reference is never resolved or validated. The CLI describes `--issue/--epic/--repo` as "record the issue number on the session metadata".
- **Model selection**: Web `ModelSelect` + `entities/settings` (`/opencode/models`) and CLI `mo agent model list` expose a bare model-id catalog; no purpose-keyed guidance exists.
- **Surfaces**: Web `AgentListPage` renders `Readiness: <conclusion>` as inline text; `AgentDetailPage` shows readiness and availability; the composer dispatches immediately on submit. CLI `mo agent view` already prints the Server-authoritative projection.

Constraints: monorepo, single atomic release (server + web + CLI ship together; AGENTS.md forbids backward-compat layers). Orleans records use append-only `[property: Id(n)]` fields. #555 (external Agent API) keeps its own public projection; #556 (CLI launch preview) builds on the launch-scope contract produced here.

## Goals / Non-Goals

**Goals:**

- Task profile (purpose, description, instructions, permissions, collaborators, concurrency) is first-class on the definition, settable and clearable from both the Web editor and `mo agent create/edit`, with identical persisted results.
- A closed permission vocabulary, validated at the definition write boundary, projected on every definition read, and echoed as a fact of each launch.
- Executability is exactly four Server-derived states (`not-configured`, `not-executable`, `unknown`, `executable`) with per-gap next actions and fix entry points, rendered from one projection in Web and CLI, and gating dispatch.
- Executability and Availability stay two separately rendered signals; no synthesized badge.
- One Server confirmed-scope projection (repository, workspace, Issue/Epic context, permission scope) consumed by the Web composer and the CLI launch path; the resolved scope is persisted as immutable per-launch facts owned by the AgentJob, readable from the launch/observation surfaces, and stable across later definition edits and idempotent replays.
- Purpose-aware model recommendations during authoring, advisory only, with the full catalog still reachable.
- Saving states effective-time semantics, and running Jobs provably keep their launch facts.

**Non-Goals:**

- Runtime *enforcement* of the permission declaration (the declaration is validated, projected, echoed, and recorded — the runner does not yet police tools against it).
- New model providers or runtimes; Server-side concurrency claim/release; Slack install flow inside the create form (proposal boundaries).
- Making launch-scope confirmation a configuration override surface — the launch body stays `prompt`/`context`/`attachments`.
- Post-execution history surfaces (#558), external Agent API projection (#555), CLI preview UX beyond consuming the shared projection (#556).
- Changing Availability semantics or its endpoint.

## Decisions

### D1. Task-profile fields are top-level definition fields; permissions get their own field, not an `agentConfig` key

Add `Purpose` (`string?`) and `Permissions` (`IReadOnlyList<string>`) to `Domain.Agent`, `AgentCreateData`/`AgentUpdateData`, `AgentInfo` (append-only Orleans ids), and the `AgentDefinitionRoutes` binders (`purpose`, `permissions`, plus presence-tracked `Fields` entries and `--clear-*` parity). `Description`, `AllowedSubagentAgentIds`, `MaxConcurrentRuns` already exist end-to-end — the Web editor starts sending them; no server change needed for those.

`agentConfig` stays the *execution backend* bundle (`model`/`variant`/`runtime`). Permissions are task language, not execution config: `agentConfig` is snapshotted into job dispatch envelopes, and folding a declaration into it would blur the boundary the launch-body binder and readiness deriver rely on.

*Alternative:* add `permissions` to the `AgentConfigSchema.AllowedKeys` whitelist. Rejected: mixes task language with execution config and forces the deriver/snapshot paths to special-case one more agentConfig key; a top-level field mirrors how `AllowedSubagentAgentIds` (also a capability-ish list) is already modeled.

Clearing semantics: the update path is presence-based (`Fields`), so `purpose: null` / `permissions: []` clears durably; both surfaces use the same PATCH, guaranteeing "cleared in one surface stays cleared in the other" (agent-task-profile spec).

### D2. Closed permission vocabulary: seven `object:access` terms, owned by a schema class beside `AgentConfigSchema`

Vocabulary (the closed set named by the agent-task-profile spec, defined once in `Infrastructure` next to `AgentConfigSchema`, which the proposal names as the vocabulary home):

- `repo:read`, `repo:write` — read vs. modify/push repository working copies
- `issue:read`, `issue:write` — read vs. create/edit issues and comments
- `epic:read`, `epic:write` — read vs. modify epics
- `artifact:publish` — upload launch artifacts

Objects are the things the platform mediates for an Agent (code, project content, artifacts); `object:access` reads as task language and extends by adding terms. Validation at the write boundary (same pattern as `ValidateMaxConcurrentRuns`): reject with an error naming the offending term and the accepted vocabulary, persist nothing. Omitted declaration is valid and projects as `null`/absent. Collaborators are *not* vocabulary — they stay `allowedSubagentAgentIds`, one mechanism per concern.

*Alternative:* free-form permission strings. Rejected: the spec mandates a closed vocabulary validated at the boundary; free text cannot be echoed as a trustworthy launch fact.

*Alternative:* tool-level terms (`bash`, `edit_file`). Rejected: YAGNI — the runner tool surface differs per runtime; object-level terms survive runtime changes and are meaningful to the caller confirming a launch.

### D3. Four-state executability: replace the readiness conclusion vocabulary; keep the service, rename the projection

`AgentReadinessConclusions` (`Ready`/`Needs setup`/`Unknown`) becomes four wire states — `not-configured`, `not-executable`, `unknown`, `executable` — derived with this precedence in `AgentReadinessService.Evaluate` (class names stay; the proposal's impact names `AgentReadinessService`/`AgentReadinessDeriver`):

1. Any structural gap (instructions missing; model missing/malformed; runtime unsupported) → `not-configured`, one gap per defect.
2. Structurally complete + latest evidence matching the current definition (existing `MatchesCurrentDefinition`) failed with a configuration category (existing `IsConfigurationFailure`) → `not-executable`, with a gap that identifies it as an execution-configuration failure, distinct from setup gaps.
3. Structurally complete + latest matching evidence completed → `executable`.
4. Otherwise (no matching evidence, or non-config failure) → `unknown`, with projection copy stating that a launch is accepted and waits for Runner verification — never rendered as an error.

`AgentReadinessResult` becomes `ExecutabilityResult { State, Gaps[], PendingLaunchNote }`; `AgentReadinessGap` gains `FixEntryPoint` (web path `/agents/{id}` + `mo agent edit <ref>` — the "entry point where the fix is made" the spec requires; the per-result `Setup` hint folds into per-gap entry points). The projection is exposed as `AgentInfo.Executability` (the old `Readiness` member is removed, not deprecated — no compat layers). Derivation stays on-read (never persisted), so an edit that fixes a gap re-derives on the next read, satisfying the re-derivation scenario.

The gate (`EnsureLaunchableAsync`, `AgentConnectionDispatchDecision`, route error mapping) rejects `not-configured` and `not-executable` before any session/job exists, with distinct error codes `agent_not_configured` / `agent_not_executable` so rejections "distinguish the execution-configuration failure from missing setup" (executability spec); both carry the full gap list.

*Alternative:* keep three conclusions and add a sub-flag for config failures. Rejected: the spec's product states are exactly four and every surface renders them; a sub-flag recreates the merged badge problem inside the projection.

*Alternative:* rename `AgentReadinessService` → `AgentExecutabilityService`. Rejected for diff noise; the proposal pins the current names. The public projection and error codes use the new vocabulary, which is what surfaces and specs consume.

Availability stays exactly as is (`AgentAvailabilityService`, `/availability`). Separation is enforced at render time (D8): each signal keeps its own label, and neither service reads the other.

### D4. Model guidance: Server-owned recommendation catalog + endpoint, keyed by a small purpose-archetype set

Add a static `AgentModelRecommendations` catalog in the Server (content: per `(runtime, purposeKind)`, an ordered list of `{ modelId, fit }` where `fit` is task language — e.g. "Strong at multi-file code changes; good default for build agents"). Expose `GET /api/projects/{projectRef}/agent-model-recommendations?runtime=&purpose=` returning entries for the runtime. Both surfaces consume it: the Web editor shows a "Recommended for <purpose>" group above the full catalog in `ModelSelect`; `mo agent model list` gains `--purpose` and prints recommended entries with fit text, then the full catalog.

Purpose is free text, so matching keys on a closed archetype set (`coding`, `review`, `research`, `writing`, `general`) selected in the authoring UI ("What is this agent for?"). The archetype is advisory UI state driving the recommendation query — it is not persisted on the definition; the persisted definition keeps only free-text `Purpose` plus the chosen model.

Guidance is advisory (agent-model-guidance spec): no model is required to save, no catalog membership is validated on save, and an unrecommended model is never a gap. A missing model surfaces only through `not-configured` (D3). The full catalog endpoint and picker remain untouched; runtime switching re-queries recommendations for the new runtime (recommendations for the previous runtime are replaced, per spec).

*Alternative:* bake recommendations into each client. Rejected: two copies of "which model fits which purpose" drift immediately; the Server catalog keeps Web and CLI identical by construction.

*Alternative:* keyword-match free-text purpose to recommendations. Rejected: fragile implicit magic; an explicit archetype chip is understandable without catalog knowledge (spec scenario).

### D5. Launch scope: one resolver, one preview projection, consumed by both surfaces

Extract the resolution already embedded in `AgentSessionLaunchRoutes` into an `AgentLaunchScopeResolver` service that, for `(project, agent, context refs, origin, idempotencyKey)`, produces:

```
ConfirmedLaunchScope {
  AgentId, AgentName,
  Runtime,                       // echo of definition (informational)
  Repository { Name, GitUrl, BaseBranch }?,   // null when none referenced
  WorkspaceName?,                // explicit > CLI default > Web per-session
  Issue { Number, Title }?, Epic { Number, Title }?,
  Permissions: string[]?         // definition's declared scope, echoed
}
```

Resolution rules (identical for preview and dispatch, because both call the resolver):
- Workspace: existing `ResolveCliWorkspaceNameAsync`/`ResolveWebWorkspaceNameAsync` (resolve-only). The Web composer passes the same idempotency key it will dispatch with, so the per-session workspace name is identical at preview and dispatch.
- Issue/Epic: resolve number → title (existing queriers); unknown reference fails the scope with an actionable error naming it.
- Repository: validate the `repository` reference against the project's repositories and resolve `{name, gitUrl, baseBranch}` from the workspace snapshot logic already used for `WorkspaceRepositories`. Today the repository ref passes through unvalidated; scope resolution makes it a resolved fact (an unknown repo is an unresolvable reference per the spec).
- Workspace archived / not found → actionable failure (existing `ValidateContextAsync` rules move into the resolver).

New route: `POST /api/projects/{projectRef}/agents/{agentRef}/launch-scope/preview` (body = the launch `context` block; POST because it mirrors the launch body; it creates nothing and is idempotent-safe). It also returns the agent's current executability so the composer can block dispatch without a second call. The dispatch route itself calls the same resolver instead of its inline copies — "neither surface derives its own resolution" holds by construction, and the CLI launch path (#556) gets preview for free.

*Alternative:* scope resolution only inside dispatch, with clients rendering the 201 echo. Rejected: the spec requires the caller to see and confirm the scope *before* dispatch.

### D6. Confirmation is a surface behavior; dispatch persists the resolved scope as AgentJob-owned facts

- **Web composer**: submit opens a confirmed-scope panel (fetched from the preview projection); the Launch button dispatches the same `context` refs only after the panel is shown. Dispatch is not gated by any new server token.
- **CLI**: `mo agent launch` resolves the scope first, prints it, and requires confirmation (interactive prompt, or `--yes` for scripts) before POSTing.

At dispatch, the resolver output is captured as a first-class `LaunchScope` snapshot: a new append-only field on `AgentJobInput` (and therefore the AgentJob's immutable ledger state / `DispatchJson`). The AgentJob — not session metadata — owns launch facts (launch-scope spec); `GenericAgentSessionMetadata` keeps only what session rendering already needs. The scope is surfaced on the 201 `AgentSessionLaunchResponse` and on `AgentLaunchObservationDto` (assembler joins job-owned facts with session state, its existing composition pattern), so it is readable after launch from both the Web session view and the CLI observation path.

Immutability comes from the existing snapshot discipline: `AgentJobInput` is captured once at admission and never rewritten; idempotent replay already returns the coordinator's canonical plan, so a replay surfaces the originally recorded scope and creates nothing new. Definition edits cannot rewrite it because the launcher copies definition values (now including `Permissions`) at launch time — the same mechanism that already pins model/runtime/instructions, which is also what makes the effective-time semantics statement true rather than aspirational (agent-task-profile spec).

Override rejection is already enforced by the body binder (`AllowedTopLevelFields`); it stays `prompt`/`context`/`attachments` with the actionable 400 — confirmation does not become a configuration surface.

*Alternative:* a two-phase token (preview mints a scope token; dispatch must present it and the server compares). Rejected: stronger than any spec requirement ("a launch MUST NOT dispatch a scope the caller has not seen" is a surface obligation); adds coordinator state and a new failure mode for no product gain. Noted as a future hardening if server-verifiable confirmation is ever required.

### D7. Task-first authoring surfaces

- **Web editor** reorders into *Task* (name, purpose, description, instructions, permissions multi-select, collaborators, concurrency) then *Execution* (runtime, model + variant with purpose-keyed recommendations, skills). Runtime/model become secondary choices; the raw `provider/model` string is never the leading prompt.
- **CLI** `create`/`edit`: task options (`--name`, `--purpose`, `--description`, `--instructions[-file]`, `--permissions`, `--allowed-subagent`, `--max-concurrent-runs`) are registered before execution options (`--runtime`, `--model`, `--variant`, `--skills`), so help output leads with task language; add `--purpose`/`--permissions` and their `--clear-*` counterparts for parity. `--agent-config` stays retired.
- **Effective-time statement**: both surfaces print it at save ("applies to Jobs launched afterwards; running Jobs keep their launch facts") — the Web editor's existing dialog copy, extended to permissions; CLI prints it in the save success output. The behavior it states is D6's snapshot discipline.

### D8. Rendering: one projection, two labeled signals, no synthesis

Web `AgentListPage` replaces the `Readiness:` inline text with an executability state chip (four states) plus the leading gap action that links to the fix entry point; `AgentDetailPage` shows the full diagnosis (state, gaps, actions, entry points) *and* the Availability signal as its own labeled row. CLI list gains an executability column; `mo agent view` prints state + gaps + next actions and Availability separately. Both surfaces render only what the Server projections return — no client-side verdicts (the web `entities/agent` model drops its local conclusion fallback; `launch-feedback.ts` maps the new error codes `agent_not_configured` / `agent_not_executable`). Availability rendering stays grounded in the existing `/availability` projection.

## Risks / Trade-offs

- [Vocabulary rename breaks in-tree consumers of `readiness` (Web types/tests, `launch-feedback.ts`, CLI table shapes, `AgentConnectionDispatchDecision`)] -> All in-tree consumers are updated in the same change; no external consumers exist yet (#555 ships its own projection later). Monorepo atomic release makes partial breakage impossible.
- [New `not-executable` gate could reject launches that previously succeeded] -> It cannot: config-failure evidence already maps to today's gating "Needs setup" conclusion; the change is representational (distinct state + error code), verified by porting the existing readiness service tests.
- [Surfaces render `unknown` as an error, violating the spec] -> The projection carries the pending-launch note; list/detail/composer tests pin that `unknown` renders neutrally and states that launch is accepted and waits for Runner verification.
- [Preview/dispatch TOCTOU — the confirmed scope could differ from the dispatched scope (workspace changed between preview and dispatch)] -> Both call the same resolver; the Web composer reuses one idempotency key (stable per-session workspace name); dispatch persists the *dispatch-resolved* scope, so the recorded fact is always the true one. Mismatch risk is confined to the brief preview window and is visible by re-previewing.
- [Permission declaration read as enforcement by users] -> Copy everywhere says "declared scope, echoed at launch"; runtime enforcement is an explicit non-goal and open question. The vocabulary is deliberately object-level so future enforcement points can adopt it without a breaking rename.
- [Recommendation catalog goes stale vs. the live model catalog] -> Recommendations that name models absent from the runtime's catalog are filtered against the catalog at query time (an entry can demote itself; it never blocks choosing any catalog model).
- [Per-agent executability hydration in list reads is O(agents × latest-execution query)] -> Existing behavior (readiness already hydrates per agent in `AgentQuerier.ListAsync`); no new cost class is introduced. Optimize only if list latency regresses.
- [`AgentJobInput` Orleans record grows another serialized field] -> Append-only `[property: Id(n)]` per repo convention; old persisted state deserializes with the field absent (null), and old code ignores new fields — safe under the repo's no-compat policy.

## Migration Plan

1. **Server — definition**: `Domain.Agent` fields (`Purpose`, `Permissions`), vocabulary + validation in `Infrastructure`, `AgentGrain` create/update, `AgentDefinitionRoutes` binders, `AgentInfo` projection. No DB migration: `Agents.State` is schema-less JSON; old rows deserialize with `Purpose = null`, `Permissions = []`.
2. **Server — executability**: four-state `Evaluate`, gap entry points, gate + error codes, `AgentInfo.Executability`; update `AgentConnectionDispatchDecision`.
3. **Server — launch scope**: extract `AgentLaunchScopeResolver`, add preview route, capture `LaunchScope` on `AgentJobInput`, expose on 201 + observation assembler (append-only Orleans ids; no column changes — observation reads ledger state).
4. **Server — model guidance**: catalog + recommendations endpoint (filtered against the live catalog).
5. **Web**: `entities/agent` model/API (executability, purpose, permissions, scope preview), editor restructure + parity fields, list/detail rendering, composer confirmation step, `launch-feedback` error mapping.
6. **CLI**: create/edit options (+ clears), view/list renderers, launch scope print + confirm, `model list --purpose`.
7. **Verify**: `npm run verify` (server + web + CLI builds and tests); spec scenarios ported to tests per capability.

**Rollback**: revert the release. New JSON fields in stored rows are ignored by old deserializers and default-populated for new rows read by old code; no destructive migration exists. Because the executability rename is atomic with the surface updates, rollback restores the previous consistent pair.

## Open Questions

- **Permission enforcement**: when (and where — runner tool layer vs. dispatch envelope) should the declaration become enforced rather than declared? The vocabulary is chosen to survive that step, but it is not designed for per-tool granularity.
- **Purpose archetypes**: is the initial set (`coding`, `review`, `research`, `writing`, `general`) the right granularity, and should the selected archetype eventually persist on the definition (e.g. for #558 history grouping)?
- **Epic reference typing**: the API binds `epicNumber` as an integer while the CLI `--epic` accepts a string; the scope resolver should normalize this — confirm whether epic refs ever need non-numeric identifiers.
- **Repository defaulting**: when no repository is referenced, should the confirmed scope name the project's default repository (making scope always concrete) or explicitly state "no repository context"? Current design: explicit absence.
- **#556 handoff**: exact CLI flag shape (`--yes` vs. `--confirm`) and whether `mo agent launch` should support a preview-only mode reusing the same projection.
