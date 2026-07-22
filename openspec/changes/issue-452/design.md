## Context

Issue 450 delivered the first Pi consumption path — Workflow Inline Agent turns via `uses: mohist/pi`, the in-process `PiRuntime`, runtime-aware Workflow Session binding, and runtime-aware Session command routing (`resolveCommandRuntime`, issue 451). The second path — saved Mohist Agents — was explicitly deferred and still hardcodes OpenCode end to end:

- `AgentLauncher.LaunchAsync` opens the AgentSession with `AgentRuntime: "opencode"` (`AgentLauncher.cs:85`); the routed path does the same in `AgentJobGrain.AdvancePreparedLaunchAsync` (`AgentJobGrain.cs:460`).
- The runner `AgentJobExecutor` hardcodes `runtime: "opencode"` in the turn target (`agent-job-executor.ts:84`) and is wired with only `OpenCodeRuntime` (`host.ts:395`).
- Generic agent-session open/attach hardcode `Runtime: "opencode"` (`RunnerRoutes.cs:377,398`).
- The model catalog is single-runtime: one OpenCode route (`OpencodeRoutes.cs:16`), flat `coderModels`/`coderModelVariants` on `RunnerRegistration` (`core/types.ts:319`) and `RunnerInfo` (`IRunnerGrain.cs`), aggregated flat by `RunnerRegistryGrain`, and consumed by every Web picker via one `getOpencodeModels` call.

What is already runtime-aware and needs **no** change: `AgentSessionGrain.IsRuntimeRegistered` accepts `"pi"`; Session command handlers (Follow-up/Cancel/Compact/Reset) route by the session's current binding runtime; the Pi catalog is already loaded inside `PiRuntime` (`pi/runtime.ts:80`, `catalog()`). The Agent's `agentConfig` is an opaque `JsonElement?` validated by a single shared whitelist, `AgentConfigSchema` (`AgentConfigSchema.cs`), reached from both Agent CRUD (`AgentDefinitionRoutes.cs:24,70`) and issue agentConfig writes.

Constraints: Orleans snapshot fields are append-only (never reorder); `agentConfig` is opaque JSON (no typed column); the two execution paths share Session infrastructure but not Runtime instances and must not call back into Workflow Actions (domain model, `docs/agents.md`); model legality is finally validated by the execution backend, never pre-asserted from catalog presence.

## Goals / Non-Goals

**Goals:**
- Make execution backend (`opencode` | `pi`, default `opencode`) a first-class, snapshot-fixed dimension of Mohist Agent config, with issue-level override precedence.
- Make a Mohist Agent Job execute on its snapshot-selected backend through the existing AgentSession infrastructure.
- Make the model catalog per-runtime (Pi = configured-credential models only) and have Web model selectors present models for the selected backend.

**Non-Goals:**
- Workflow Profile `uses` backend selection (delivered by issue 450).
- Provider credential management UI (add/remove keys).
- Changing the OpenCode catalog/selection experience beyond accepting a runtime dimension.
- A generic `AgentRuntime` interface — `OpenCodeRuntime` and `PiRuntime` remain parallel deep modules (per `design/runtimes/pi.md`).

## Decisions

### D1 — Backend lives in `agentConfig.runtime`, validated by the shared `AgentConfigSchema`
Add `runtime` to `AgentConfigSchema.AllowedKeys` (`{model, variant, runtime}`) and extend `Validate` to reject values other than `opencode`/`pi`. Because `IssueModelMetadata.ValidateAgentConfig` delegates to `AgentConfigSchema.Validate`, Agent CRUD and issue agentConfig accept/reject the key consistently at the API boundary. Absent/unset → `opencode`, so existing Agents are backward compatible with no data rewrite.

`AgentConfigSchema` has **two** key surfaces that both need updating: `AllowedKeys` (used by `Validate`) and the hardcoded list inside `Filter` (`new[] { "model", "variant" }`, `AgentConfigSchema.cs:88`), which is the write-side projection used by `IssueVariableBuilder`, `MohistIssueWorkflowProfileBase`, and `ConfigService`. `Filter` must be derived from `AllowedKeys` (or have `runtime` added to its list) so the key is not silently dropped on those merge paths.

- **Alternative considered:** a typed `Runtime` column on `AgentInfo`. Rejected — it fragments a cohesive options block (model/variant/runtime), requires a DB migration plus a second write/validation path, and breaks the "opaque JSON + one shared schema" pattern.
- **Alternative considered:** storing backend outside config (e.g. on the Agent grain state). Rejected for the same fragmentation reason.

### D2 — Backend resolved as `launchOverride ?? agentConfig.runtime ?? "opencode"`, snapshotted onto the AgentJob
Resolution mirrors the existing model/variant snapshot (`ResolveModelAndVariant`, `AgentLauncher.cs:266`): add a parallel `ResolveRuntime(agentConfig, launchOverride)` helper reused by both launch paths and the routed preflight (`RoutingDispatchHandler.cs:137`). The resolved backend is captured onto the durable snapshot — `AgentJobInput.Runtime` (next free `Id(4)`) and `RoutedAgentLaunchPlan.Runtime` (next free `Id(19)`) — so editing the Agent definition after launch cannot change an in-flight job, and recovery/re-dispatch reads the snapshot rather than re-reading mutable config. This is the same snapshot rationale already documented for model/variant (design D2, #410).

**Source of the launch-time override (OQ1 — resolved: per-launch field):** the override is an optional `runtime` field on the manual launch request body (`AgentSessionLaunchRequest`), supplied by whoever triggers the launch (the Web launch affordance or an API client). The manual launch path passes it to `ResolveRuntime`; the routed (event-driven) launch path passes no override and uses the Agent's configured backend (a per-rule override is a separate, later concern). This gives the override an obvious, owned write path (the request body, accepted by T-001), avoids coupling the launch path to issue-variable reads, and keeps the `vars.agent` surface dedicated to the Workflow Inline Agent path (whose backend is chosen by `uses`, not by `vars.agent.runtime`). The resolution-precedence contract is fixed by the `agent-execution-backend` spec regardless of source.

- **Alternative considered and rejected:** reading the override from the issue's `vars.agent.runtime`. Rejected because `vars.agent` is the Workflow Inline Agent's options surface (bound via `options: ${{ vars.agent }}` and consumed on the `uses`-selected runtime), so overloading it with a Mohist-Agent-launch meaning would be ambiguous, and no path writes `vars.agent.runtime` today — leaving AC #2 without a writer.

### D3 — Launch opens the session with the resolved backend (no hardcode)
`AgentLauncher.LaunchAsync` passes the resolved runtime into `OpenAgentSessionCommand.AgentRuntime` (`AgentLauncher.cs:85`). On the routed path, `LaunchRoutedAsync` populates `RoutedAgentLaunchPlan.Runtime`; `AgentJobGrain.AdvancePreparedLaunchAsync` reads `plan.Runtime` instead of the literal at `AgentJobGrain.cs:460`. The session's persisted runtime (set here) becomes the single source of truth for everything downstream.

### D4 — Dispatch carries `runtime`; the runner executor selects the runtime by dispatch
`AgentJobGrain.BuildDispatch` (`AgentJobGrain.cs:761`) adds `runtime` to the `with` payload. The runner `AgentJobExecutor` reads `runtime` from the payload and selects `PiRuntime` or `OpenCodeRuntime` instead of the hardcoded literal (`agent-job-executor.ts:84`). `host.ts` wires both runtimes to the executor (today only OpenCode at `host.ts:395`), reusing the late-binding accessor shape already used by Session command routing (`command-runtime.ts`).

A chosen runtime that is not ready fails with an actionable `runtime-unavailable` error — no silent fallback to OpenCode — matching the existing OpenCode-not-ready behavior (`agent-job-executor.ts:61-67`). `runtime` is added to the executor's known-keys set so it is not flagged as an unknown option key.

The executor's terminal output must label the run with the runtime that actually executed. `buildAgentJobOutput` currently hardcodes `kind: "opencode"` (`agent-job-executor.ts:250`); it is parameterized from the selected runtime so a Pi-executed AgentJob is not mislabeled. (The server's `ReportResultAsync` reads `failureCategory`, not `kind`, so this is a labeling-correctness fix, not a routing change.)

### D5 — Generic agent-session open/attach derive runtime from the session, not a literal
The generic open/attach routes already fetch `existing = await grain.GetAsync()` and `AgentSessionInfo.Runtime` is already returned to the runner (`RunnerGenericAgentSessionResponse.Runtime`, `RunnerRoutes.cs:478`). Open uses `existing.Runtime ?? "opencode"` and attach uses `existing.Runtime`, replacing the hardcoded literals at `RunnerRoutes.cs:377,398`. Because the runtime was fixed at launch (D3), the session's own value is authoritative and the runner does not need to supply a runtime on attach for this path. `AgentSessionAttachRequest.Runtime`/`ExpectedRuntime` remain for the workflow-session path.

### D6 — Per-runtime model catalog: Runner reports Pi catalog tagged by runtime; API and registry serve by runtime
The Runner reads `piRuntime.catalog()` (`{ models: { provider, id, thinkingLevels }[] }`, `pi/types.ts:36`) alongside the OpenCode discovery and reports both, tagged by runtime. The catalog endpoint becomes runtime-aware **additively**: the existing `/opencode/models` route and its `{models, modelVariants}` response shape are preserved, and an optional `?runtime=opencode|pi` query (default `opencode`) selects the backend's catalog, so legacy callers behave exactly as before.

- **Storage shape (see Open Question OQ2):** recommended is a runtime-keyed catalog map on `RunnerInfo` (`Dictionary<string, RuntimeCatalogEntry>` where each entry is `{ models, variants }`), with the existing flat `coderModels`/`coderModelVariants` becoming the `opencode` entry. `RunnerRegistryGrain` gains a `ListCoderModelsByRuntimeAsync(runtime)` that unions across runners for that runtime only. This avoids duplicating the flat-pair pattern per runtime and keeps the Pi group to configured-credential models (Pi's `getAvailable()` already returns only those).
- **Alternative considered:** parallel flat pairs (`piCoderModels`/`piCoderModelVariants`). Lowest churn and mirrors today's shape exactly, but duplicates the pattern and does not generalize to a third runtime.

### D7 — Web: backend selector drives the model list; `agentConfig` read/write preserves `runtime`
`AgentProfileEditor` gains an execution-backend selector; the model-catalog query becomes runtime-parameterized (`useAvailableModelIds(runtime)` → `getModels(projectId, runtime)`). `IssueModelSelector` and `CreateIssueDialog` parameterize their model list by the selected backend. The Agent config read/write helper (`entities/agent/api/client.ts:106`) is extended to preserve the `runtime` key alongside `model`/`variant` (today it rebuilds only `{model, variant}` and drops every other key).

## Risks / Trade-offs

- **[Override had no obvious write path if sourced from issue variables]** -> Mitigation: OQ1 is resolved to a per-launch `runtime` field on the launch request body, so the override has a clear, owned writer (the API caller / Web launch affordance) and the launch path is not coupled to issue-variable reads.
- **[`agentConfig.runtime` silently dropped on write-merge paths]** -> Mitigation: `AgentConfigSchema` has two key surfaces (`AllowedKeys`/`Validate` and the hardcoded list in `Filter`); both are updated and a round-trip test through `Filter` is added (D1).
- **[AgentJob terminal output mislabels Pi runs as `opencode`]** -> Mitigation: `buildAgentJobOutput` is parameterized from the selected runtime so the output reflects the executing backend (D4).
- **[Catalog endpoint change could break the Web consumer]** -> Mitigation: the `/opencode/models` route and `{models, modelVariants}` shape are preserved; `?runtime=` is additive with an `opencode` default, so legacy callers behave exactly as before (D6).
- **[Pi catalog empty when Pi is not ready]** -> Mitigation: the catalog is a configuration aid only (spec `runtime-model-catalog`); model legality is finally validated by the runtime at turn time, so an empty Pi group does not block configuration or runner readiness (mirrors OpenCode's readiness gate).
- **[Orleans snapshot field ids must stay append-only]** -> Mitigation: use the next free ids (`AgentJobInput` `Id(4)`, `RoutedAgentLaunchPlan` `Id(19)`); never reorder or reuse existing ids; null default preserves older snapshots.
- **[Agent edit silently strips `runtime` if the Web write helper is not updated]** -> Mitigation: extend the Web write helper to preserve `runtime`; add a unit test asserting round-trip of all three keys.

## Migration Plan

Backward compatible by construction; no data rewrite required:

1. `agentConfig.runtime` is additive; absent key resolves to `opencode` everywhere (D1). Existing Agents and issue bundles behave exactly as before.
2. Orleans snapshot fields are append-only with null defaults, so in-flight jobs serialized before this change deserialize with `Runtime = null` → resolved as `opencode`.
3. Ship server + runner + web in one coordinated release: the catalog API generalization and its sole Web consumer land together; the runner reports the Pi catalog only when the Pi runtime is ready.
4. **Rollback:** revert the change. Agents without a `runtime` key continue to launch as OpenCode; Agents that had been configured for Pi fall back to OpenCode (acceptable degradation — no persisted state depends on the new field).

## Open Questions

- **OQ1 — Source of the launch-time override (RESOLVED).** Decided: a per-launch `runtime` field on the manual launch request body, resolved as `launchOverride ?? agentConfig.runtime ?? "opencode"` (D2). The routed path uses the Agent's configured backend. This gives the override an owned write path and avoids overloading the Workflow's `vars.agent` surface.
- **OQ2 — Catalog storage shape.** Runtime-keyed map (recommended, generalizes) versus parallel flat pairs (lower churn). Affects D6 only.
- **OQ3 — Attach runtime provenance on the generic path.** Confirm deriving attach runtime from the session's persisted runtime (recommended, single source of truth) rather than having the runner send it. The session value is already authoritative for command routing.
