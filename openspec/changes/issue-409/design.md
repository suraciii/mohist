## Context

Issue #409 replaces the Workflow Inline Agent execution backend. Today the `mohist/opencode` Action (landed by #408) is only a **bridge**: it validates the new input contract, translates `options` into `with.agent`, and delegates to `runAcpWorkflowAgentSession` — which still spawns an OpenCode **ACP** process via `@agentclientprotocol/sdk`, runs a quiet-threshold liveness probe, scans OpenCode log files for provider errors, parses `opencode models --verbose` output, and carries private ACP compaction metadata.

The contracts the runtime needs are already in place:
- **#407** delivered the AgentSession identity/command contract (`runtime` + `runtimeSessionId`, Compact-keeps-binding, Reset expected-binding guard, idle-only boundary, operation dedup, `missing`/`conflict`/`unavailable` taxonomy). Today the runner returns `unavailable` for Compact/Reset because the real SDK calls are deferred to this issue.
- **#408** delivered the Action Input/Output contract (`prompt`/`session`/`options{model,variant}`, first-slash model split, minimal `null | { promise }` output, `turnFact.finalAssistantText`, executor-owned completion). Built-in profiles already use `mohist/opencode`.

The authoritative product/architecture spec is [`design/runtimes/opencode.md`](../../../design/runtimes/opencode.md). This document is the **implementation design** for the change: module structure, integration seams, sequencing, testing, and rollback. Per the issue, **ACP cleanup is limited to the Workflow source** — the AgentJob execution path, `mohist/acp-agent`, the shared ACP connection, and the `@agentclientprotocol/sdk` dependency remain until #410.

Current integration points in `packages/runner/`:
- `runtime/host.ts:804` `connectRunner` — calls `discoverOpencodeModels()` (CLI) then `connection.connect()` + `signalR.start()`.
- `runtime/host.ts:407` `initializeSharedConnection` — calls `createSharedAcpConnection(cwd)` then constructs `WorkExecutor(..., sessionManager, sharedAcpConnection)`.
- `runtime/host.ts:435` `runWorkerPool` / `:486` `pollOnce` — `connection.poll(...)`, dispatch → `executeAndTransition` → `:627` `executeWork` → `WorkExecutor`.
- `runtime/host.ts:167` `handleSessionCommand` → `executeAcpSessionCommand`; `:181` `resolveFollowupTarget` / `:204` `restoreSessionTarget` (ACP `resumeSession`).
- `actions/opencode.ts` — the bridge being replaced.
- `actions/acp/session-strategies.ts:74` `runAcpWorkflowAgentSession` — branches on `ownerKind === "agent-job"` (generic path retained) vs Workflow (retired here).
- `core/types.ts:209` `ActionContext` carries `acpSessionManager`/`acpConnection`/`serverConnection`/`ownerKind`/`agentSessionId`; `:250` `ActionResult.turnFact`.
- `runtime/executor.ts` — `WorkExecutor`: completion evaluation, `projectTaskOutput`, `PROMISE_PROJECTED_ACTIONS` (unchanged by #409).
- `package.json:23` — `@agentclientprotocol/sdk@0.21.0`; no `@opencode-ai/sdk` yet.

## Goals / Non-Goals

**Goals:**
- Deliver the `OpenCodeRuntime` deep module that drives `@opencode-ai/sdk/v2` directly for the Workflow Inline Agent path, with no SDK types leaking past its boundary.
- Make the Runner claim work only when OpenCode is healthy and its model catalog loads; rebuild and resume after loss.
- Execute turns by awaiting `client.session.prompt()`; fulfil Follow-up/Compact/Cancel/Reset, session binding, and restart reconciliation over the native SDK.
- Retire ACP liveness probing, log heuristics, CLI model parsing, private compaction metadata, and `.opencode` lockfile cleanup for the Workflow path.
- Preserve the #407 command semantics and the #408 Action/completion contract from the runtime side.

**Non-Goals:**
- Migrating the AgentJob execution path off ACP; removing `mohist/acp-agent`, the shared ACP connection, the generic session strategy, or `@agentclientprotocol/sdk` (#410).
- A `mohist/agent` Action, a Pi runtime, or a pre-built generic `AgentRuntime` interface.
- Installing, upgrading, or version-locking the OpenCode CLI; mapping OpenCode permission prompts to Workflow Approvals; calling `client.v2.session.wait()`/`compact()`.
- A feature flag, compatibility alias, or ACP fallback for the Workflow source.

## Decisions

### D1. Introduce an `OpenCodeRuntime` deep module, do not extend the ACP connection

A new module (e.g. `packages/runner/src/runtime/opencode/`) owns: shared Server/Client lifecycle, the `client.global.event()` subscription, readiness + catalog, the physical-session map, turn execution, Follow-up/Compact/Reset/Cancel, event routing + snapshot reconciliation, model-string parsing, SDK DTO construction, and error normalization. Callers depend only on Mohist-owned request/result types. The runtime is constructed through an injectable **factory** (mirroring the existing `setAcpProcessFactoryForTest` seam) so tests inject a fake runtime or fake generated Client/Server.

**Rationale:** SDK drift, call ordering, and error interpretation are confined to one module; migrating later to a full V2 session core changes only this module. **Alternative considered:** extend `acp-connection.ts`/`SharedAcpConnection` to host both backends — rejected because it couples two execution backends behind one shallow wrapper and violates the deep-module boundary the upstream design requires.

### D2. Pin `@opencode-ai/sdk`, import via `/v2`, and use the mature `client.session.*` surface

Add pinned `@opencode-ai/sdk` to `packages/runner/package.json`; retain `@agentclientprotocol/sdk` until #410. Implement against `client.session.create/prompt/promptAsync/abort/summarize/get/messages/status`, `client.global.event()`, and read-only `client.v2.model.list()`/`client.v2.provider.list()`. **Before any other implementation**, lock the version and smoke-verify the asserted call surface against a real OpenCode; record the result and reconcile the design table on any drift.

**Rationale:** OpenCode's own Web/TUI use `client.session.*`; the generated `client.v2.session.wait/compact` report `operation unavailable` today. **Alternatives considered:** (a) call `client.v2.session.wait/compact` — rejected (unavailable); (b) spawn the OpenCode CLI per call — rejected (the SDK exists precisely to avoid this).

### D3. Wire readiness into the host lifecycle and gate `pollOnce` on it

In `connectRunner`/`initializeSharedConnection`, start the shared OpenCode Server via `createOpencodeServer()`/`createOpencodeClient()`, run the health check, and load the catalog via the v2 list APIs (replacing `discoverOpencodeModels`). The runtime exposes a `ready()` boolean and a diagnostic. `runWorkerPool` checks `ready()` before each `pollOnce`: when not ready it skips claiming, emits the actionable diagnostic, and waits one interval; it still drains `awaitingAck` reports. On Server exit, the runtime sets not-ready, fails in-flight Workflow turns, and rebuilds Server/Client/event-subscription in the background; `ready()` re-passes only after health + catalog re-load.

**Transitional behavior (accepted per issue):** because there is one readiness gate, AgentJob work (still on ACP until #410) is also paused while OpenCode is down. **Rationale:** one shared backend per Runner, one readiness truth. **Alternatives considered:** (a) per-source readiness — rejected as transitional over-engineering that #410 removes anyway; (b) claim-then-fail — rejected because it wastes slots and surfaces failures as work results rather than a runner-level diagnostic.

### D4. Switch the Workflow seam only; keep the AgentJob seam on ACP

Both Actions stay registered. `opencodeAction` (`actions/opencode.ts`) stops calling `runAcpWorkflowAgentSession` and `restoreAgentToolNoise` (`.opencode` lockfile restore) and `withOpencodeAgentBinding`; instead it resolves the prompt, validates input via the existing `parseOpencodeInput`, and calls `OpenCodeRuntime` to run the turn, returning `ActionResult` with `turnFact.finalAssistantText`. In `session-strategies.ts`, the `ownerKind === "agent-job"` early-return (generic path) stays untouched for #410; the Workflow branch is removed.

**Rationale:** honours the issue's Workflow-only cleanup scope and the domain rule that the shared runtime must not create a Workflow→Agent dependency. **Alternative considered:** migrate AgentJob in the same change — rejected (explicit Non-Goal; #410 owns it).

### D5. Route session commands and follow-up by source through the runtime

`handleSessionCommand` and `resolveFollowupTarget` dispatch by source: Workflow-source targets route to `OpenCodeRuntime`; AgentJob-source targets keep the existing ACP path until #410. The runtime fulfils the #407 contract over native calls — Compact → `summarize({sessionID, providerID, modelID})` after reading current model; Reset → `create()` in the same work directory under the expected-binding guard, replacing the binding and appending lineage only on success; Cancel → `abort()`; Follow-up → `promptAsync()`. Restart/reconnect reconciliation reads the persisted binding and a `session.status()`/`session.get()`/`session.messages()` snapshot instead of ACP `resumeSession`. The existing `SessionTarget` shape and `sessionManager` cache are reused; Workflow entries are backed by the OpenCode physical session id.

**Rationale:** preserves #407's stable identity and the `notStarted`/`unavailable`/`missing`/`conflict` taxonomy without re-specifying it. **Alternative considered:** a parallel session-command module per backend — partially adopted (source-keyed dispatch) but the request/result shape stays the single Mohist-owned one so the runtime handler needs no Workflow Action Input or Agent definition.

### D6. Reuse the executor-owned deadline; remove liveness for the Workflow path

Turn completion is the awaited `client.session.prompt()` response. The per-work abort signal the `WorkExecutor` already constructs is the sole deadline (default 60 min, overridable — the existing `DEFAULT_TIMEOUT_MS`). On deadline the runtime calls `client.session.abort()` and returns `interrupted`. The ACP liveness probe, quiet-threshold, and probe timeout are removed for the Workflow path (retained for the AgentJob ACP path until #410). `OpenCodeRuntime` performs no silent detection.

**Rationale:** one backstop, matching the upstream decision. **Trade-off:** a silently hung provider now consumes the full deadline instead of being probed at 5 min — accepted; mitigated by stage-level timeout tuning, not by reintroducing detection. **Alternative considered:** an in-runtime quiet detector — rejected by the design.

### D7. One global event subscription; idempotent projection; messages-snapshot reconciliation

The runtime maintains one `client.global.event()` subscription and routes by Session ID + directory. Known typed events normalize to Mohist transcript/tool/usage/model/status/compaction facts; projection is idempotent by OpenCode message/part ID; unknown events go to diagnostics only. After prompt completion (and on reconnect), the runtime reconciles against `session.messages()` when events are missing or the final visible transcript must be confirmed. OpenCode log-file scanning (`opencode-log-diagnostics.ts`) is retired for the Workflow path.

**Rationale:** events reduce display latency only; the snapshot is the reconciliation authority, so transient loss/duplication cannot complete or duplicate a turn. **Alternative considered:** a V2 history cursor / replay state — explicitly rejected.

### D8. Normalize SDK errors to a small runtime result set; no global Workflow enum

At the boundary, SDK errors map to a small `RuntimeTurnResult` discriminated set: `invalid input`, `unavailable runtime`, `missing Session`, `incompatible runtime`, `permission required`, `interrupted`, `turn failed`. Provider detail is diagnostics only. Each caller (`mohist/opencode` → TaskRun; later AgentJob executor → AgentJob contract) reports failure through its own channel; no global Workflow error enum is introduced. Unsatisfiable interactive permissions abort the turn with a `permission required` result; Mohist never auto-approves and never creates a Workflow Approval.

**Rationale:** keeps the runtime backend-neutral and preserves the #408 rule that runtime/completion facts stay out of Action Output. **Alternative considered:** a shared cross-caller `ErrorKind` enum — rejected.

### D9. Removal scope for this change (Workflow path only)

Remove for the Workflow path: `restoreAgentToolNoise` + `.opencode` lockfile restore and `withOpencodeAgentBinding` in `opencode.ts`; the Workflow branch of `runAcpWorkflowAgentSession`; CLI catalog discovery (`runtime/opencode-models.ts`) replaced by the runtime catalog call; Workflow-path usage of `opencode-log-diagnostics.ts`, `actions/acp/liveness.ts`, `actions/acp/compaction.ts`, and `actions/acp/model-resolution.ts`/`session-events.ts`. **Retain for #410:** `@agentclientprotocol/sdk`, `mohist/acp-agent` registration, `runtime/acp-connection.ts`, `runtime/acp-command.ts`, `actions/acp/process.ts`, and the generic/AgentJob session strategy (including its ACP liveness). `ActionContext` gains a runtime handle field; the existing ACP fields remain for the AgentJob path.

### D10. Tests inject a fake runtime; no real process/network/fs/clock

Following `design/testing.md`, default Runner tests inject a fake `OpenCodeRuntime` (or fake generated Client/Server factory) that deterministically drives events, snapshots, completion, process loss, and errors. Coverage mirrors the spec testing list: input expansion + no hidden `vars.agent` fallback; multi-slash model with sibling variant; shared runtime but separate work/session identity; physical-session reuse/rotation; model/variant non-rotation; global event routing + duplicate suppression + snapshot reconciliation; prompt completion/interruption/uncertain-admission/no-replay; async Follow-up, native summarize, Reset, restart routing, stale-binding rejection; permission/missing-session/incompat/process-loss failures; minimal `{ promise }` output via existing executor semantics.

## Risks / Trade-offs

- **SDK drift between pin time and OpenCode upgrades** → pin the version; smoke-verify the asserted surface before implementation; confine all SDK access to `OpenCodeRuntime` so drift is a one-module fix.
- **Transitional readiness gating pauses AgentJob (still on ACP) when OpenCode is down** → accepted per issue; mitigate with a clear diagnostic distinguishing the OpenCode-readiness cause; fully resolved by #410.
- **Two backends coexisting (ACP for AgentJob + OpenCode for Workflow) raises coupling risk** → source-keyed dispatch with a single Mohist-owned request/result shape; no shared mutable cross-backend state; the Workflow→Agent domain-dependency invariant is preserved.
- **Duplicate turns on redelivery within the crash window** → accepted; in-process dispatch deduplication retained; no deterministic Prompt ID/replay state added.
- **Event loss yields a stale transcript** → reconcile via `session.messages()` snapshot on completion and reconnect; idempotent projection by message/part ID.
- **Removed liveness lets a hung provider occupy a slot for up to the 60-min default** → accepted trade-off (explicit design decision); tuned via per-stage timeouts, not by reintroducing detection.
- **Legacy persisted Workflow bindings carry `acpSessionId`** → treated as "current Runtime Session missing" → explicit Reset hint; no data rewrite, alias, or fallback.
- **`client.session.create()`/model DTO shape assumptions may be wrong** → resolved by the D2 smoke verification before implementation; drift fixes the design table first.

## Migration Plan

1. **Pin + smoke-verify (gate):** add pinned `@opencode-ai/sdk`; verify `session.create/prompt/promptAsync/abort/summarize/get/messages/status`, `global.event`, and `v2.model/provider.list` against a real OpenCode; record the result. Reconcile the call table on drift before proceeding.
2. **Build the runtime behind a fake:** implement `OpenCodeRuntime` + factory seam and the full fake-driven test suite (D10) before touching the host.
3. **Rewire the Workflow seam:** switch `opencodeAction` to the runtime; add the runtime handle to `ActionContext`; route Workflow-source commands/follow-up through the runtime (D4, D5).
4. **Wire readiness + catalog:** replace `discoverOpencodeModels` with the runtime catalog; gate `pollOnce` on `ready()` (D3).
5. **Retire Workflow-path ACP artifacts (D9);** leave AgentJob ACP untouched.
6. **Deploy:** built-in profiles already use `mohist/opencode` (#408), so no coordinated profile change is required. Persisted Workflow-source bindings with legacy ACP identity surface as "missing runtime session" with a Reset hint; any custom Workflow profile still on `mohist/acp-agent` must be updated (the Action remains available only for the AgentJob path).
7. **Rollback:** no feature flag/alias is provided (per Non-Goals); rollback is `git revert`. This is acceptable because the project is in active development with no version-compatibility constraint. The AgentJob ACP path remains functional throughout, so a revert restores the pre-#409 Workflow bridge without affecting AgentJob.

## Open Questions

- Exact pinned `@opencode-ai/sdk` version (resolved by the D2 smoke step).
- Whether `client.session.create()` accepts the model DTO directly or model is applied only per-prompt — confirmed by smoke verification before implementation.
- `createOpencodeServer()`/`createOpencodeClient()` signature: shared server working directory vs explicit per-call `cwd` — confirmed by smoke verification.
- Acceptable severity/duration of the transitional AgentJob readiness pause — confirm against the #410 timeline; if disruptive, narrow the gate to Workflow-only as a short-lived interim (still removed by #410).
