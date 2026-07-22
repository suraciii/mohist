## Why

Pi already executes Workflow Inline Agent turns (issue 450 delivered `mohist/pi`), but the second Pi consumption path — saved Mohist Agents — still hardcodes OpenCode: every launch opens its AgentSession as `opencode`, and the model catalog and every model selector are OpenCode-only. Users therefore cannot configure a Mohist Agent to run on Pi, cannot override the backend per issue launch, and see only OpenCode models regardless of which backend an Agent uses. This closes the gap by making execution backend a first-class, snapshot-fixed dimension of Mohist Agent config and by making the model catalog and selectors backend-aware.

## What Changes

- Add execution backend (`opencode` | `pi`, default `opencode`) as a dimension of Mohist Agent config, carried in the existing `agentConfig` alongside `model`/`variant`.
- Support issue-level launch override of the backend; when not overridden, the Agent's configured backend is used.
- Fix the chosen backend to the AgentJob snapshot at launch, so editing a running Agent's backend does not change any execution already started.
- Launch reads the backend from Agent config / issue override instead of the hardcoded `opencode` literal, on both the manual and routed (event-driven) launch paths.
- Execute the AgentJob on the snapshot-selected backend: the server work dispatch carries `runtime`, the runner `AgentJobExecutor` selects `PiRuntime` or `OpenCodeRuntime`, and generic agent-session open/attach use the snapshot runtime rather than a hardcoded literal.
- Reuse the existing AgentSession infrastructure for Pi Agent execution — transcript, tools, usage, cost, compaction, and lineage render through the same Session views; session commands (Follow-up / Cancel / Compact / Reset) already route by the session's current binding runtime.
- Launching with a model whose provider has no configured credentials fails with an actionable error; model legality is finally validated by the execution backend.
- Runner reports the Pi model catalog (only models with configured credentials) alongside the OpenCode catalog, tagged by runtime.
- Generalize the OpenCode-only model catalog endpoint to serve models by runtime. The existing `/opencode/models` route and its `{models, modelVariants}` response shape are preserved (additive): an optional `?runtime=opencode|pi` query selects the backend's catalog, defaulting to `opencode` when omitted, so legacy callers behave exactly as before.
- Web Mohist Agent editor gains an execution-backend selector that drives which catalog feeds the model picker; issue-level and per-stage model selection list models grouped by the selected backend, with the Pi group showing only configured-credential models.
- **No change** to Workflow Profile `uses` backend selection (delivered by issue 450), provider credential management UI (add/remove keys), or the OpenCode catalog/selection experience beyond accepting a runtime dimension.

## Capabilities

- `agent-execution-backend`: Execution backend as a snapshot-fixed dimension of Mohist Agent config and issue-level launch override — config field and default, override resolution, snapshot fixation so in-flight edits do not alter started executions, and the launch paths reading the resolved backend instead of hardcoding OpenCode.
- `agent-job-runtime-execution`: An AgentJob executes on its snapshot-selected backend — runtime on the work dispatch, runner executor selection between `PiRuntime`/`OpenCodeRuntime`, runtime-aware generic agent-session open/attach, shared Session infrastructure for Pi Agent runs, and actionable failure when the chosen model has no configured credentials.
- `runtime-model-catalog`: A per-runtime model catalog — Runner reports the Pi catalog (configured-credential models only) alongside OpenCode tagged by runtime, the catalog API serves models by runtime, and the Web Agent editor and issue/stage model selectors present models grouped by the selected backend.

## Impact

- **Server** (`packages/server`): `AgentLauncher` and `AgentJobGrain` read the backend from Agent config / issue override and thread it into `OpenAgentSessionCommand` (manual path `AgentLauncher.cs:85`, routed path `AgentJobGrain.cs:460`); `AgentJobInput` and `RoutedAgentLaunchPlan` gain an append-only `Runtime` snapshot field; `BuildDispatch` (`AgentJobGrain.cs`) emits `runtime` on the work envelope; generic agent-session open/attach in `RunnerRoutes.cs:377,398` use the snapshot runtime instead of a literal; the OpenCode catalog route (`OpencodeRoutes.cs`) becomes runtime-aware and `RunnerRegistryGrain` aggregates runtime-tagged catalogs; `AgentSessionGrain` runtime registry already accepts `pi` (no change).
- **Runner** (`packages/runner`): `AgentJobExecutor` reads `runtime` from the dispatch and selects `PiRuntime`/`OpenCodeRuntime` (today hardcoded at `agent-job-executor.ts:84`); `host.ts` wires both runtimes to the executor; registration (`host.ts:825`) reports the Pi catalog (`piRuntime.catalog()`) alongside OpenCode, runtime-tagged; the `RunnerRegistration`/`RunnerInfo` catalog shape gains a runtime dimension.
- **Web** (`packages/web`): `AgentProfileEditor` gains a backend selector driving the model picker; `IssueModelSelector` and `CreateIssueDialog` list models by selected backend; the `settings` model-catalog query/client become runtime-parameterized; `agent` config read/write (`entities/agent/api/client.ts`) preserves the `runtime` key alongside `model`/`variant`.
- **Persistence and APIs**: Agent `agentConfig` gains an additive `runtime` key (default `opencode`, backward compatible); Orleans snapshot fields are append-only (no reorder); AgentSession identity, lineage, `runtimeSessionId`, and immutable work directory remain authoritative — a backend change on a started job never mutates an in-flight AgentSession binding.
- **Verification**: product tests use injected runtimes/fake dispatch and fake Server connections; no real Pi, provider, network, process, or wall clock. The no-credential-model launch failure and the per-backend catalog grouping are covered by assertions on the reported catalog and dispatch/runtime selection, not on provider state.
