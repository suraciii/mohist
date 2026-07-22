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

- **Alternative considered:** a typed `Runtime` column on `AgentInfo`. Rejected — it fragments a cohesive options block (model/variant/runtime), requires a DB migration plus a second write/validation path, and breaks the "opaque JSON + one shared schema" pattern.
- **Alternative considered:** storing backend outside config (e.g. on the Agent grain state). Rejected for the same fragmentation reason.

### D2 — Backend resolved as `issueOverride ?? agentConfig.runtime ?? "opencode"`, snapshotted onto the AgentJob
Resolution mirrors the existing model/variant snapshot (`ResolveModelAndVariant`, `AgentLauncher.cs:266`): add a parallel `ResolveRuntime(agentConfig)` helper reused by both launch paths and the routed preflight (`RoutingDispatchHandler.cs:137`). The resolved backend is captured onto the durable snapshot — `AgentJobInput.Runtime` (next free `Id(4)`) and `RoutedAgentLaunchPlan.Runtime` (next free `Id(19)`) — so editing the Agent definition after launch cannot change an in-flight job, and recovery/re-dispatch reads the snapshot rather than re-reading mutable config. This is the same snapshot rationale already documented for model/variant (design D2, #410).

- **Source of the issue-level override (see Open Question OQ1):** recommended source is the issue's workflow-profile `vars.agent.runtime` — the same surface that already holds per-issue `model`/`variant` (`IssueModelMetadata`, `IssueVariableBuilder`) and which `design/runtimes/pi.md` designates as "供 Mohist Agent 路径读取". The launch reads it only when an issue context is present (manual launch carries `Context.IssueNumber`; routed launch carries issue lineage), falling back to `agentConfig.runtime`. This makes "issue-level override" literal and reuses existing infrastructure.
- **Alternative considered:** a per-launch `backend` field on the manual launch request body (`AgentSessionLaunchRequest`), defaulting to config on the routed path. Simpler and avoids coupling launch to issue-variable reads, but is a per-call parameter rather than a genuine issue-level override.

### D3 — Launch opens the session with the resolved backend (no hardcode)
`AgentLauncher.LaunchAsync` passes the resolved runtime into `OpenAgentSessionCommand.AgentRuntime` (`AgentLauncher.cs:85`). On the routed path, `LaunchRoutedAsync` populates `RoutedAgentLaunchPlan.Runtime`; `AgentJobGrain.AdvancePreparedLaunchAsync` reads `plan.Runtime` instead of the literal at `AgentJobGrain.cs:460`. The session's persisted runtime (set here) becomes the single source of truth for everything downstream.

### D4 — Dispatch carries `runtime`; the runner executor selects the runtime by dispatch
`AgentJobGrain.BuildDispatch` (`AgentJobGrain.cs:761`) adds `runtime` to the `with` payload. The runner `AgentJobExecutor` reads `runtime` from the payload and selects `PiRuntime` or `OpenCodeRuntime` instead of the hardcoded literal (`agent-job-executor.ts:84`). `host.ts` wires both runtimes to the executor (today only OpenCode at `host.ts:395`), reusing the late-binding accessor shape already used by Session command routing (`command-runtime.ts`).

A chosen runtime that is not ready fails with an actionable `runtime-unavailable` error — no silent fallback to OpenCode — matching the existing OpenCode-not-ready behavior (`agent-job-executor.ts:61-67`). `runtime` is added to the executor's known-keys set so it is not flagged as an unknown option key.

### D5 — Generic agent-session open/attach derive runtime from the session, not a literal
The generic open/attach routes already fetch `existing = await grain.GetAsync()` and `AgentSessionInfo.Runtime` is already returned to the runner (`RunnerGenericAgentSessionResponse.Runtime`, `RunnerRoutes.cs:478`). Open uses `existing.Runtime ?? "opencode"` and attach uses `existing.Runtime`, replacing the hardcoded literals at `RunnerRoutes.cs:377,398`. Because the runtime was fixed at launch (D3), the session's own value is authoritative and the runner does not need to supply a runtime on attach for this path. `AgentSessionAttachRequest.Runtime`/`ExpectedRuntime` remain for the workflow-session path.

### D6 — Per-runtime model catalog: Runner reports Pi catalog tagged by runtime; API and registry serve by runtime
The Runner reads `piRuntime.catalog()` (`{ models: { provider, id, thinkingLevels }[] }`, `pi/types.ts:36`) alongside the OpenCode discovery and reports both, tagged by runtime. The catalog API is generalized to accept `?runtime=opencode|pi` (default `opencode`) so the sole consumer receives the requested backend's catalog.

- **Storage shape (see Open Question OQ2):** recommended is a runtime-keyed catalog map on `RunnerInfo` (`Dictionary<string, RuntimeCatalogEntry>` where each entry is `{ models, variants }`), with the existing flat `coderModels`/`coderModelVariants` becoming the `opencode` entry. `RunnerRegistryGrain` gains a `ListCoderModelsByRuntimeAsync(runtime)` that unions across runners for that runtime only. This avoids duplicating the flat-pair pattern per runtime and keeps the Pi group to configured-credential models (Pi's `getAvailable()` already returns only those).
- **Alternative considered:** parallel flat pairs (`piCoderModels`/`piCoderModelVariants`). Lowest churn and mirrors today's shape exactly, but duplicates the pattern and does not generalize to a third runtime.

### D7 — Web: backend selector drives the model list; `agentConfig` read/write preserves `runtime`
`AgentProfileEditor` gains an execution-backend selector; the model-catalog query becomes runtime-parameterized (`useAvailableModelIds(runtime)` → `getModels(projectId, runtime)`). `IssueModelSelector` and `CreateIssueDialog` parameterize their model list by the selected backend. The Agent config read/write helper (`entities/agent/api/client.ts:106`) is extended to preserve the `runtime` key alongside `model`/`variant` (today it rebuilds only `{model, variant}` and drops every other key).

## Risks / Trade-offs

- **[Issue-override plumbing couples the launch path to issue-variable reads]** -> Mitigation: read `vars.agent.runtime` only when an issue context is present; otherwise use `agentConfig.runtime`; keep the read behind the existing `IssueModelMetadata` surface so no new domain coupling is introduced. If OQ1 resolves to a per-launch field instead, this risk disappears.
- **[Catalog API route/response change is BREAKING for `/opencode/models`]** -> Mitigation: the Web app is the only consumer and ships in the same change; the generalized endpoint keeps `opencode` as the default when `?runtime=` is absent, so any legacy caller behavior is preserved.
- **[Pi catalog empty when Pi is not ready]** -> Mitigation: the catalog is a configuration aid only (spec `runtime-model-catalog`); model legality is finally validated by the runtime at turn time, so an empty Pi group does not block configuration or runner readiness (mirrors OpenCode's readiness gate).
- **[Orleans snapshot field ids must stay append-only]** -> Mitigation: use the next free ids (`AgentJobInput` `Id(4)`, `RoutedAgentLaunchPlan` `Id(19)`); never reorder or reuse existing ids; null default preserves older snapshots.
- **[Agent edit silently strips `runtime` if the write helper is not updated]** -> Mitigation: extend the Web write helper to preserve `runtime`; add a unit test asserting round-trip of all three keys.

## Migration Plan

Backward compatible by construction; no data rewrite required:

1. `agentConfig.runtime` is additive; absent key resolves to `opencode` everywhere (D1). Existing Agents and issue bundles behave exactly as before.
2. Orleans snapshot fields are append-only with null defaults, so in-flight jobs serialized before this change deserialize with `Runtime = null` → resolved as `opencode`.
3. Ship server + runner + web in one coordinated release: the catalog API generalization and its sole Web consumer land together; the runner reports the Pi catalog only when the Pi runtime is ready.
4. **Rollback:** revert the change. Agents without a `runtime` key continue to launch as OpenCode; Agents that had been configured for Pi fall back to OpenCode (acceptable degradation — no persisted state depends on the new field).

## Open Questions

- **OQ1 (primary) — Source of the issue-level override.** Confirm the recommended source (issue `vars.agent.runtime`, read at launch when an issue context is present) versus a per-launch request field. Affects D2 plumbing only; the resolution-precedence contract is fixed by the `agent-execution-backend` spec regardless.
- **OQ2 — Catalog storage shape.** Runtime-keyed map (recommended, generalizes) versus parallel flat pairs (lower churn). Affects D6 only.
- **OQ3 — Attach runtime provenance on the generic path.** Confirm deriving attach runtime from the session's persisted runtime (recommended, single source of truth) rather than having the runner send it. The session value is already authoritative for command routing.
