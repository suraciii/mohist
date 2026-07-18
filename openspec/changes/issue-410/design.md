## Context

Issue #410 finishes the ACP→OpenCode migration that #407, #408, and #409 set up:

- **#407** delivered the canonical AgentSession (stable id, immutable source, `runtime` + `runtimeSessionId` wire, Compact-keeps-binding, Reset expected-binding guard, idle-only boundary, missing-runtime-session taxonomy).
- **#408** delivered the Workflow task contract (`expect`/`failIf` at task top-level, `mohist/opencode` Input/Output, first-slash model parsing, no hidden `vars.agent` fallback).
- **#409** delivered the deep `OpenCodeRuntime` module (`packages/runner/src/runtime/opencode/`) and switched the Workflow source to it end-to-end: `client.session.create/prompt/abort/summarize`, the single `client.global.event()` subscription, readiness + catalog gate, restart/reconnect reconciliation, provider-error failure policy, executor-owned deadline. **#409 explicitly left the AgentJob path on ACP** so the two sources could migrate independently.

What remains today, all scoped to the AgentJob path:

- `AgentLauncher.cs:97` and `AgentJobGrain.cs:521` hard-code `Uses: "mohist/acp-agent"`; `ComposePromptWithEntry`/`ApplyAgentRuntimeConfig` build the legacy `with.prompt = { "agent-launch": {...} }` envelope and `with.agent` config blob.
- The runner's `executor.ts:386–387` gate `runtimeHandle = ownerKind === "agent-job" ? null : openCodeRuntime` keeps the AgentJob off the shared runtime; the dispatch resolves `mohist/acp-agent` from the Action registry, which spawns `opencode acp` via `@agentclientprotocol/sdk`, runs the ACP liveness/probe/compaction machinery, and resolves the generic session through `actions/acp/session-strategies.ts`.
- `runtime/host.ts` owns a shared `SharedAcpConnection` + `AcpSessionManager`, an ACP `resumeSession`-based reconnect path, and a per-work ephemeral fallback at `:834–865`. The transitional readiness-gate caveat (`host.ts:513`) ties AgentJob readiness to OpenCode health purely because the runtime is shared infrastructure, not because AgentJob uses OpenCode yet.
- `server/session-target.ts`, `server/followup-handler.ts`, `server/cancel-handler.ts`, `server/followup-failure-outbox.ts` all carry a live `ClientSideConnection` on `FollowupTarget`; Follow-up calls `connection.prompt`, Cancel calls `connection.cancel`.
- `ActionContext` (`core/types.ts:243–244`) exposes `acpSessionManager`/`acpConnection`; `WorkExecutor` threads them through every dispatch.
- The issue-level `agentConfig` surface leaks ACP-era keys: web project-layer writers (`entities/settings/api/client.ts:64, 200`) stamp `type: 'opencode'`; `ReadAgentConfig` (`IssueQuerier.cs:480–488`) returns every key verbatim; `AgentDetailPage.tsx:159–166` reads `config.type`; agent-definition `AgentConfig` is a free-form `JsonElement?` blob with no schema.
- `WorkflowItemTranslator.IsInlineAgentUses`, `WorkflowYamlSerializer.IsInlineAgentUses`, `TaskRun.DeriveClassification`, `IssueRoutes.Helpers.cs:165`, plus every remaining `acpSessionId` wire/test fixture and the ACP scenario in `specs/workflow-agent/spec.md:6–9`.

This change deletes all of it and routes the AgentJob through the same `OpenCodeRuntime.runTurn` deep module the Workflow source already uses — without introducing a `mohist/agent` Action, without going through the `mohist/opencode` Action contract, and without any fallback or alias.

## Goals / Non-Goals

**Goals:**

- Route AgentJob execution through `OpenCodeRuntime.runTurn` directly via an Agent-owned request, sharing the runtime with the Workflow source but not the Action contract.
- Keep the unified launch pipeline (`IAgentLauncher.LaunchAsync`) for manual and event-subscription launches; preserve bidirectional event↔subscription↔AgentJob↔AgentSession traceability.
- Preserve the AgentJob as the sole terminal authority; AgentSession events stay non-adjudicating.
- Replace the ACP-flavored Follow-up/Cancel/outbox handlers with runtime-targeted equivalents; remove `ClientSideConnection`, `SharedAcpConnection`, `AcpSessionManager`, the ACP Action tree, the `@agentclientprotocol/sdk` dependency, and every remaining `acpSessionId` term.
- Converge the issue-level `agentConfig` surface (API/CLI/Web) to `model` + `variant` (+ `stageModels`/`stageModelVariants`); stop the write path from producing legacy keys; filter the read-back to model/variant.
- Preserve legacy transition behaviour with no stored-data rewrite: ACP-bound AgentSessions fail Session operations with a Reset hint; pre-cutover WorkflowRuns fail subsequent agent task dispatches with an actionable, rerun-recoverable error.

**Non-Goals (per issue):**

- A `mohist/agent` Action.
- Letting Workflow tasks reuse predefined Mohist Agent configuration.
- Redesigning Agent subscription filtering/arbitration/priority semantics.
- A new AgentTask/AgentThread/Session model.
- Pi or any other execution backend.
- A feature flag, compatibility alias, or ACP fallback.
- Stored-data rewrite or migration.

## Decisions

### D1. Add an Agent-owned execution entry above `executeOne`, not a new Action

The runner branches on `work.ownerKind === "agent-job"` **before** Action resolution: a new `executeAgentJob(work, signal, log)` method on `WorkExecutor` reads the Agent-owned payload from `work`, builds a `RuntimeTurnRequest`, calls `openCodeRuntime.runTurn(...)`, reports the runtime binding back through the existing attach endpoint, and returns an `ActionResult` whose `output` JSON carries the same `{ kind, status, runtimeSessionId, model, variant, text, error, diagnostics }` shape the legacy `acpAgentAction` produced (so `AgentJobGrain.ReportResultAsync`'s success/failure parsing and `FailureCategoryFrom` keep working unchanged). Workspace preparation continues to skip the workflow clone path and reads `variables.workspace.path` exactly as today (`workspaceFromVariables`).

The `OpenCodeRuntime` handle is passed uniformly to both branches via `baseContext.openCodeRuntime`; the `ownerKind === "agent-job" ? null : openCodeRuntime` gate at `executor.ts:387` is removed.

**Rationale:** preserves the domain rule "Workflow Action adapter and AgentJob executor can share the OpenCode backend but cannot depend on each other" and the Non-Goal of no `mohist/agent` Action. **Alternatives considered:** (a) register a `mohist/agent` Action — rejected (Non-Goal); (b) drive the AgentJob through the `mohist/opencode` Action contract — rejected (couples Agent identity to Workflow Action Input/Output/recovery, breaks AC5); (c) keep `acpAgentAction` as a thin shim around `OpenCodeRuntime` — rejected (keeps the ACP bridge alive in everything but name).

### D2. Replace the legacy `with.agent` payload with a stable Agent-owned contract on `WorkDispatch`

`AgentJobGrain.BuildDispatch` stops writing `Uses`, `with.model`, and `with.agent`. It writes a single Agent-owned payload into `With`:

```json
{ "prompt": "<user prompt>",
  "instructions": "<agent instructions, optional>",
  "model": "<provider/model-id, optional>",
  "variant": "<variant, optional>" }
```

The `WorkDispatch.Uses` field is left null for AgentJob dispatches (the runner dispatches on `OwnerKind: "agent-job"`, never on `Uses`). `AgentJobInput.Uses` is removed (it has only ever carried the legacy literal). `ComposePromptWithEntry` and `ApplyAgentRuntimeConfig` are deleted.

The runner's new `executeAgentJob` reads `with.prompt`, `with.instructions`, `with.model`, `with.variant` and composes a single text prompt by concatenating instructions and the user prompt (the existing `{agent-launch}` envelope is retired; the runtime does not need the JSON envelope because the runner now owns the composition).

**Rationale:** the AgentJob payload is the contract between the server and the new runner entry; it never reaches an Action, so it must not look like Action Input. Keeping a small flat shape avoids re-introducing `vars.agent` semantics in disguise. **Alternatives considered:** (a) keep the `{agent-launch: {...}}` envelope and have the runner unwrap — rejected (preserves a shape whose only consumer was the deleted ACP bridge); (b) add typed fields to `WorkDispatch` instead of using `With` — rejected (every new field enlarges the Orleans surrogate and the wire surface; `With` already exists for arbitrary per-dispatch data).

### D3. Replace `FollowupTarget.connection` with a runtime-targeted shape

`FollowupTarget` becomes `{ runtimeSessionId: string, workDir: string, projectId: string }`. The resolver returns this from the persisted binding (the same source the Workflow path already uses); no live connection is held. `followup-handler` and `cancel-handler` consume the `OpenCodeRuntime` from their handler deps and call:

- Follow-up → `openCodeRuntime.followup({ target, prompt, options })` (a new public method that wraps `client.session.promptAsync` and returns immediately).
- Cancel → `openCodeRuntime.cancel({ target })` (wraps `client.session.abort`).

If #409 did not already expose these as public methods on `OpenCodeRuntime` (it exposed the Workflow source's session commands through some surface), #410 promotes them to first-class public API with the same Mohist-owned request/result shape used by `runTurn`. The `SessionTarget` discriminator loses its ACP flavor and becomes a pure `{ kind: "workflow" | "generic", projectId, workflowRunId?, sessionName?, sessionId?, binding? }` shape; `runtimeBindingFromWireTarget` stays. The followup-failure-outbox branches on `kind` only; no `ClientSideConnection`.

`resolveFollowupTarget`, `restoreSessionTarget`, `resumeSessionTarget`, `installRestoredSessionHandlers` and the `sessionRestorations` dedup map are deleted. Restart/reconnect reconciliation is owned entirely by `OpenCodeRuntime` per #409 D7 (persisted binding + `session.status/get/messages` snapshot); the runner host no longer participates.

**Rationale:** a live per-session connection was an ACP artifact; the OpenCode runtime already owns event routing and physical-session reconciliation, so the runner only needs to address a target. **Alternatives considered:** (a) keep a per-session live-connection cache alongside the runtime — rejected (re-introduces the deleted `AcpSessionManager` in a new name); (b) route Follow-up/Cancel through the SignalR back-channel only and let the runtime poll — rejected (Follow-up is a user command with a synchronous reply).

### D4. Single readiness gate, single backend

`runtime/host.ts` drops `initializeSharedConnection`'s `createSharedAcpConnection` call, the `sessionManager`/`sharedAcpConnection` fields, `shutdownSharedConnection`, and the per-work ephemeral fallback at `:834–865`. `isOpenCodeReadyForClaim()` becomes the single gate; the transitional caveat comment at `:513` is removed. `WorkExecutor`'s constructor loses `sessionManager`/`acpConnection`; `updateAcpConnection` is deleted. `ActionContext.acpSessionManager`/`acpConnection` are removed; only `openCodeRuntime` remains. The runner host still owns the runtime lifecycle (start/diagnostic/catalog/shutdown) exactly as today.

**Rationale:** one shared backend per runner, one readiness truth — matches #409 D3's end-state. **Alternatives considered:** keep a parallel session-manager for "fast path" lookups — rejected (the runtime already maintains the physical-session map).

### D5. Configuration surface: validate at the write boundary, project at the read boundary

- **Issue DTO**: drop the dead `CreateIssueRequest.AgentConfig` and `UpdateIssueRequest.AgentConfig` fields (parsed but never consumed by `IssueRoutes.Crud.cs`).
- **Validation**: extend `IssueModelMetadata.Validate` to also receive the raw `agentConfig` (when the route layer still accepts it on the agent-definition surface) and reject any key outside `{model, variant}`. The same whitelist applies to `AgentCreateRequest.AgentConfig`/`AgentUpdateRequest.AgentConfig` on `AgentDefinitionRoutes.cs`.
- **Project-layer writers** (`entities/settings/api/client.ts:64, 200`): stop stamping `type: 'opencode'`; write `{model, variant}` only. Web `writeAgentModelAndVariant` (`entities/agent/api/client.ts:106–124`) stops preserving legacy keys via spread — it returns `{model, variant}` only.
- **T1 dispatch merge** (`IssueVariableBuilder.Build` with `agentConfig` + `MohistIssueWorkflowProfileBase.MergeAgentConfig` + `IssueWorkflowProfileStorageIntegrity.FoldAgentDataIntoBundle` + `ConfigService.GetAgentConfigAsync`): whitelist-filter at the read-in point so only `model`/`variant` enter `vars.agent` from any source.
- **Read-back**: filter `IssueQuerier.ReadAgentConfig` to surface only `model`/`variant`. This is the single response-side chokepoint.
- **`AgentDetailPage.tsx:159–166`**: drop the `agentType` derivation; the "Agent Config" card shows model/variant only.
- **Web milestone classifier** (`widgets/issue-workflow/ui/milestones.ts:26–33`): replace `isAcpAgentTask` with an OpenCode-based or runtime-neutral classifier (e.g., based on `uses === "mohist/opencode"` for workflow-source tasks; agent-job tasks were never rendered through this classifier anyway).

**Rationale:** the issue-level write path is already converged (`IssueModelMetadata.MutateAgentDict` only touches model/variant); the leaks are the dead DTO field, the read-back, the project-layer stamping, and the agent-definition blob. A whitelist at the API boundary plus a projection at the read-back chokepoint covers every active path without rewriting stored data. **Alternatives considered:** (a) filter only on read — rejected (still accepts and persists `type`/liveness silently, contradicting the spec); (b) rewrite historical `vars.agent` on read — rejected (violates Non-Goal of no data rewrite).

### D6. Legacy transition: rely on the existing runtime discriminator; add one actionable error for the removed Action

- **Legacy ACP-bound AgentSessions**: already handled by #407. `AgentSessionGrain.IsRuntimeRegistered` accepts only `"opencode"`; any persisted `runtime: "acp"` binding surfaces as `RuntimeSessionMissingException` with the existing "Reset the session to establish a new binding" message. No new code required.
- **Pre-cutover WorkflowRuns**: the runner's generic `"No action found for '${work.uses}'"` at `executor.ts:135` is upgraded to an actionable, named error when `work.uses === "mohist/acp-agent"` (and a small `RemovedActions` set in general): the message names the removed Action and points the user to rerun the affected stage with a `mohist/opencode` profile. Existing rerun/rerun-from-stage routes (`WorkflowRoutes.WorkflowControl.cs`, `IssueRoutes.WorkflowControl.cs`) recover the run once the profile is updated.
- **Custom profiles naming `mohist/acp-agent`**: surface the same actionable error at profile load (the workflow YAML loader already rejects unknown actions for inline-agent tasks through `IsInlineAgentUses`); collapse `IsInlineAgentUses` to `mohist/opencode`-only so a profile naming the removed Action fails validation rather than reaching dispatch.

**Rationale:** the legacy-transition behaviour is mostly already in place; #410 only adds one actionable error message and removes the silent Action miss. **Alternatives considered:** auto-rewrite persisted `uses: mohist/acp-agent` to `mohist/opencode` on rerun — rejected (the Non-Goal of no stored-data rewrite applies to profile data too; the user reruns with an explicitly updated profile).

### D7. Tests inject a fake runtime and a fake SignalR connection; no real process/network/fs/clock

Default runner tests inject the existing `setOpenCodeRuntimeFactoryForTest` seam (or a new `setAgentJobExecutorForTest` seam if D1 lands as a separate dispatcher). Coverage:

- AgentJob execution: launch-time-fixed snapshot (instructions/model/variant edits during run are ignored); new physical session when binding is null, restore when non-null; model/variant parsing; result success/failure mapping; failure category; report-then-close ordering; concurrent work prompts on the same session; binding-report idempotency and rejection of mismatched reports.
- Follow-up / Cancel over the new runtime-targeted FollowupTarget; unavailable-runtime and missing-session taxonomy; outbox routing by `kind`.
- Readiness gate: stops both owner kinds on OpenCode-down; resumes after re-pass; no ACP-init fallback path remains.
- Configuration: reject `type`/`livenessQuietThresholdMs`/`probeTimeoutMs`/`sessionStartTimeoutMs`/`compaction` on issue and agent-definition writes; read-back returns model/variant only; legacy persisted keys tolerated by the runtime's `unknownKeys` diagnostic path.
- Legacy transition: ACP-bound session → Reset hint; pre-cutover dispatch with `uses: "mohist/acp-agent"` → actionable error; custom profile naming the removed Action → load-time rejection.

Server spec tests cover: AgentJobGrain.BuildDispatch no longer emits `Uses: mohist/acp-agent`/`with.agent`; AgentLauncher passes the converged AgentJobInput; IsInlineAgentUses collapses; recovery fixture no longer names the ACP Action; `ReadAgentConfig` filtering. Web tests cover: form payloads contain no ACP/liveness keys; `agentType` derivation removed; milestone classifier rename.

## Risks / Trade-offs

- **`OpenCodeRuntime` may not yet expose Follow-up/Cancel as public methods after #409** → verify early; if missing, promote them in this change with the same request/result shape as `runTurn`. Confined to the runtime module.
- **FollowupTarget API change touches every runner handler and many tests** → keep the new shape as a pure Mohist-owned value object (`{runtimeSessionId, workDir, projectId}`); pass the runtime through handler deps so tests inject a fake.
- **Removing `Uses: mohist/acp-agent` from BuildDispatch must land in lockstep with the runner's new `executeAgentJob`** → sequence the migration in two phases within the same change: add the runner entry first (gated on a new `WorkType`/`OwnerKind` it can dispatch), then flip the server, then delete the ACP code. The project is in active development with no version compatibility, so a single atomic PR is acceptable.
- **Read-back filtering may hide keys that debug tooling relies on** → the historical audit trail of `vars.agent` stays in storage; filtering applies only to the API read model. Storage inspection remains available through developer tooling.
- **Project-layer writer change may break in-flight Web sessions that cached `type: 'opencode'`** → accepted; the Web is updated atomically; re-fetch re-reads the filtered shape.
- **Custom profiles naming `mohist/acp-agent` break at load** → intended; the failure is actionable and recovery is a profile edit.
- **The `worktree-enforcement.ts` cleanup hook (`setCleanupAgentActionForTest`) imports `acpAgentAction`** → replace with an OpenCode-backed cleanup entry; the dirty-worktree cleanup contract (no push, only `git add`/`commit`/`restore`/`clean`) is preserved.

## Migration Plan

1. **Add the runner entry (D1)** behind the existing `ownerKind === "agent-job"` branch — keep `mohist/acp-agent` registered and the ACP path live so the existing server dispatch still works.
2. **Promote Follow-up/Cancel to public `OpenCodeRuntime` methods (D3)** if not already present; add the runtime-targeted `FollowupTarget` alongside the legacy `ClientSideConnection` shape temporarily.
3. **Switch the server (D2)**: `AgentLauncher`/`AgentJobGrain.BuildDispatch` emit the new Agent-owned payload and drop `Uses`. Flip the runner's `executeAgentJob` to consume it and drive `OpenCodeRuntime.runTurn`.
4. **Rework Follow-up/Cancel/outbox handlers (D3)** to target the runtime; delete `resolveFollowupTarget`/`restoreSessionTarget`/`resumeSessionTarget`/`installRestoredSessionHandlers`.
5. **Converge configuration (D5)**: drop the DTO field, extend validators, stop stamping `type` in the Web, filter `ReadAgentConfig`, drop `agentType` derivation.
6. **Collapse server recognition of the removed Action (D6)**: `IsInlineAgentUses` → `mohist/opencode`-only; `TaskRun.DeriveClassification` drops the ACP literal; recovery fixture updated.
7. **Delete the ACP code**: runner `actions/acp-agent.ts`, `actions/acp/` tree, `runtime/acp-connection.ts`, `runtime/acp-command.ts`, `runtime/acp-session-command.ts(.test)`, `tests/acp/**`, `tests/support/fake-acp.ts`; `actions/registry.ts` keeps only `mohist/opencode`; `actions/opencode.ts` relocates `buildPromptLoaderContext`/`sessionNameFromContext` out of the deleted subtree; `core/types.ts` drops `acpSessionManager`/`acpConnection`; `runtime/host.ts` drops the shared ACP connection, the per-work ephemeral fallback, and the readiness-gate caveat; `runtime/worktree-enforcement.ts` and `runtime/worktree-cleanup.ts` drop the ACP literal; `system/timeout-signal.ts` comment updated.
8. **Remove `@agentclientprotocol/sdk`** from `packages/runner/package.json`; remove `MOHIST_AGENT_ARGS` reads; review `MOHIST_AGENT_COMMAND` surface (kept as the verbose-inspector source of truth).
9. **Sweep tests and fixtures** (legacy `'mohist/acp-agent'` literals, `'acpSessionId'` fixtures renamed, fake-acp removed).
10. **Docs/specs**: close the gap sections in `design/agent-execution.md` and `design/runtimes/opencode.md`; align `docs/actions/opencode.md` and `docs/agents.md`; rename the ACP scenario in `specs/workflow-agent/spec.md`.

**Rollback:** `git revert`. No feature flag/alias is provided (Non-Goal). Acceptable because the project is in active development with no version-compatibility constraint.

## Open Questions

- Does `OpenCodeRuntime` already expose Follow-up (`promptAsync`) and Cancel (`abort`) as public methods after #409, or only as an internal session-command dispatch? If internal, #410 must promote them with a Mohist-owned request/result shape (D3).
- Should the new Agent-owned runner entry live as a method on `WorkExecutor` or as a separate `AgentJobExecutor` class composed by the host? Preferable: a separate class, so `WorkExecutor` returns to being Workflow-only and the Action contract stays explicitly tied to Workflow source. Decide during implementation based on how much shared workspace/log/upload machinery the two paths reuse.
- Should `WorkDispatch.Uses` carry a stable sentinel like `"mohist/agent-job"` for observability/diagnostics, or stay null? Leaning null + `WorkType: "agent-job"` discriminator (already present), to avoid implying an Action.
