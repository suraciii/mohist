## Context

AgentSession already models a stable logical identity (`AgentSession.Id`, the grain key) separate from the mutable runtime facet (`AgentSessionStatusSnapshot.AgentRuntimeSessionId`). Source metadata is already tagged — `WorkflowAgentSessionMetadata` stamps `source-kind=workflow` with `workflowRunId + sessionName` labels; `GenericAgentSessionMetadata` stamps `source-kind=agent-launch` with `agentId` labels. `AgentSessionResolver` / `AgentSessionQuerier` already resolve the canonical `sessionId` from those labels. So the *identity* and *source* pillars of the target model largely exist.

What leaks the old "rotate the id" model and blocks the ACP→OpenCode swap (#409):

- **Compact and Reset both rebind.** `IssueRoutes.Sessions.cs:151` mints a fresh GUID via `BuildNewAgentSessionId()` for *both* commands and hands it to the grain as `NewAgentSessionId`. `AgentSessionGrain.ApplyRecoveryTransitions` runs `RebindRuntimeSession` (rotates `AgentRuntimeSessionId`, appends lineage, emits `AgentSessionRuntimeBound`) **plus** `RecordCompaction` for every recovery. Compact and Reset are indistinguishable at the domain level except for the `strategy` string.
- **Responses advertise the rotated id.** `AgentSessionRecoveryResult.AgentSessionId` carries the freshly minted runtime id; CLI help literally says "return a new session id" (`MohistCliCommands.Issue.Session.cs:141,181`).
- **No expected-binding guard.** A Reset result is applied unconditionally — a stale replacement can overwrite a newer binding.
- **Named-agent CLI lacks compact/reset.** `mo agent session` has `launch/followup/cancel/show/transcript/list` but no recovery commands, and there is no generic-scoped compact/reset HTTP route.
- **Wire still carries `acpSessionId`/`coderSessionId`.** `AgentSessionReadModels.cs`, `RunnerRoutes.cs`, runner `followup-handler.ts`, and ~100 web references use the legacy alias instead of `runtimeSessionId` (+ `runtime`).
- **No `runtime` persisted on the binding.** `AgentSessionRuntime` holds only `RunnerId` + `WorkDir`; the execution-backend name is never stored, so a legacy ACP binding cannot be distinguished from an OpenCode one after replacement, and lineage cannot record which backend a historical entry belonged to.
- **No explicit "runtime session missing" failure.** Today a dead backend is silently tolerated; the spec demands an explicit Reset hint.

Constraints: SDK mechanics (`summarize`/create/`abort`) are owned by #409 — this issue lands only the command contract, routing, and server-side guards they must satisfy. No stored data may be rewritten.

## Goals / Non-Goals

**Goals:**
- Split Compact (keeps current Runtime binding, records compaction only) from Reset (guarded replacement) at the domain, grain, command, route, and CLI layers.
- Make `sessionId` the sole identity surfaced by every command response; remove id rotation and its wording.
- Add the expected-binding guard so a stale Reset result cannot overwrite a newer binding.
- Add `mo agent session compact|reset` and the matching generic HTTP routes, routing through the same canonical path as the workflow commands.
- Persist `runtime` on the current binding and on lineage entries; migrate the wire from `acpSessionId`/`coderSessionId` to `runtimeSessionId` (+ `runtime`).
- Make a missing/dead current Runtime Session fail explicitly with a Reset hint, for both sources.
- Define the Mohist-owned runner command contract (expected binding in, replacement/missing-session out) that #409 will fulfil with real SDK calls.
- Keep Follow-up (join-active / start-idle, no new TaskRun/AgentJob) and Cancel (interrupt turn only, never delete) consistent across both sources.

**Non-Goals:**
- Implement OpenCode SDK calls (`promptAsync`, `summarize`, `abort`, session creation) — #409.
- Rewrite or migrate stored session state, lineage, or transcript rows.
- Redesign the AgentSession evidence/transcript page.
- Migrate context across runtimes or across sources.
- Change AgentJob / Workflow / Agent lifecycle or ownership semantics.

## Decisions

### D1 — Split Compact from Reset in the domain and grain

Today `CompactAsync` and `ResetAsync` both funnel through `ApplyRecoveryTransitions` → `RebindRuntimeSession` + `RecordCompaction`. Split them:

- **Compact** calls only `RecordCompaction` (already exists at `AgentSession.Transitions.cs:151`). It must **not** call `RebindRuntimeSession`, must not append a lineage entry, and must not emit `AgentSessionRuntimeBound`. `AgentSessionContextCompacted` is the only domain event.
- **Reset** keeps calling `RebindRuntimeSession` (appends lineage, emits `AgentSessionRuntimeBound`) but now under the expected-binding guard (D2). It no longer calls `RecordCompaction` — Reset is a clean replacement, not a compaction.

`ApplyRecoveryTransitions` is removed; the two grain methods inline their distinct transitions. The shared pieces that remain are `EnsureSessionIdleForRecovery` (D6) and `PersistRecoveryAsync` (the event-aware save + transcript flush + fan-out, which is genuinely shared).

*Alternatives considered:* Keep one method with a `mode` flag. Rejected — the two transitions diverge (one rebinds, one does not; one is guarded, one is not) and a flag reproduces the current "indistinguishable except strategy string" smell the spec explicitly breaks.

### D2 — Expected-binding guard on Reset

`ResetAgentSessionCommand` gains an `ExpectedRuntimeSessionId` field (the runtime id the caller believes is current). The grain compares it to `session.Status.AgentRuntimeSessionId`:

- Match (or both empty) → proceed: request replacement, apply via `RebindRuntimeSession`, append lineage.
- Mismatch → reject with a conflict (`stale_binding`) naming the stable `sessionId` and the actual current binding. No mutation, no lineage append.

The replacement `runtimeSessionId` is supplied by the runner command result (D7). Because #409 does not yet produce real replacements, the server-side guard + rejection path is the testable surface of this issue; the happy-path replacement is wired to the runner contract that #409 fulfils.

*Alternatives considered:* Optimistic-concurrency version counter on the session. Rejected — the runtime binding id *is* the version token for this domain (it changes on every rebind), so a separate counter duplicates an existing invariant. Carrying the whole expected binding tuple (`runtime` + `runtimeSessionId`) instead of just the id. Rejected as over-specification: the id alone uniquely identifies a binding for a given session, and `runtime` is immutable per backend swap that Reset itself triggers.

### D3 — Stable sessionId in every response; remove client-side id minting

- Delete `BuildNewAgentSessionId()` from `IssueRoutes.Sessions.cs`. Compact takes no id argument; Reset takes `ExpectedRuntimeSessionId` (sourced from the session's current binding server-side, not minted by the route).
- `AgentSessionRecoveryResult` drops the rotated `AgentSessionId` field from its public meaning; responses address the session by `Id` (the stable grain key) only. The result still carries context-window before/after and `Operation` for the UI.
- CLI table shape `SessionRecovery` prints the stable `sessionId`; help text for `mo issue session compact|reset` is rewritten to "Compact/Reset the session in place" with no "new session id" wording.

*Alternatives considered:* Keep returning the runtime id in a differently-named field for diagnostics. Rejected — it re-creates the "two ids" confusion the spec removes. Diagnostics belong on the session read model (`runtimeSessionId`), not on the command response.

### D4 — Persist `runtime` on the binding and lineage

Add a `Runtime` (execution-backend name, e.g. `"opencode"`) to:

- `AgentSessionRuntime` (current binding: `RunnerId`, `WorkDir`, `Runtime`).
- `RuntimeSessionLineageEntry` (`AgentRuntimeSessionId`, `BoundAt`, `Runtime`).
- `AgentSessionRuntimeBound` event (carry `Runtime` so realtime consumers render the backend).

The grain stamps `Runtime` when the runner opens/attaches a session (the runner knows which backend it is; today `OpenAgentSessionCommand.AgentRuntime` already carries it but it is discarded in `CreateSession`). Legacy rows with no `Runtime` degrade to absent/hidden on the wire — they surface as "runtime session missing" on the next command (D5), which is exactly the spec's legacy-binding behaviour.

*Alternatives considered:* Infer the backend from the runner's registered capabilities instead of persisting it. Rejected — a Runner process can serve one backend today, but persisting the binding is what lets a *restarted* or *different* runner address the live session, and is what lets lineage record history across a backend swap. Keep a single `coderType`-style legacy alias. Rejected — `coderType` is a DTO-only field with no domain meaning and no lineage presence.

### D5 — Explicit "runtime session missing" failure

Introduce a domain-level check: when a command requiring a live runtime session (compact, reset, follow-up) targets an AgentSession whose current binding is absent or whose `Runtime` no longer matches any registered runner backend, the command fails with an explicit error:

- Error code `runtime_session_missing`, HTTP 409.
- Message names the stable `sessionId` and prompts Reset.
- No synthetic transcript, no fabricated continuous conversation.

This is what makes a legacy ACP binding (after the #409 backend replacement) fail loudly rather than silently. The check is owned by the grain (it holds the persisted binding) and mirrored by the route into the `runtime_session_missing` response shape. Cancel already short-circuits terminal/no-runner sessions honestly (`AgentSessionCancelRoutes.cs:62-75`); the same honesty principle extends to compact/reset/follow-up.

### D6 — Shared idle-only concurrency boundary (already present, formalised)

`EnsureSessionIdleForRecovery` (`AgentSessionGrain.cs:183`) already throws when the session is active, and both routes already map that to a `session_active` conflict. This issue:

- Keeps the single boundary for Compact and Reset (identical check, identical conflict shape).
- Makes the conflict reference the stable `sessionId` (already does via the route's `new { sessionId }`).
- Documents that Follow-up is intentionally *outside* this boundary (it joins an active turn), and Cancel is *outside* it (it requires an active turn to interrupt).

No new concurrency primitive is introduced; the grain's single-threaded activation is the serialiser.

### D7 — Generic (named-agent) compact/reset routes and CLI parity

Add two HTTP routes under the existing `/api/projects/{projectRef}/agent-sessions/{sessionId}` prefix (alongside followup/cancel):

- `POST .../compact` — resolve canonical `sessionId` (path param is already the canonical id for generic sessions), call `CompactAsync`.
- `POST .../reset` — same, call `ResetAsync` with `ExpectedRuntimeSessionId` read from the session's current binding.

Both reuse the exact grain commands and response shapes as the workflow routes — no source-specific branching in the grain. Add `mo agent session compact <session-id>` and `mo agent session reset <session-id>` to `MohistCliCommands.Agent.cs`, mirroring the workflow CLI builders but addressing the generic path.

*Alternatives considered:* A single source-agnostic route set keyed only by `sessionId`, dropping the workflow-scoped `issue/number/sessions/name` routes. Rejected for this issue — the workflow entry point is a product surface (users know issue+name, not the minted id) and removing it is a larger UX change. Both entries resolve to the same canonical routing, which is what the spec requires.

### D8 — Mohist-owned runner command contract (placeholder for #409)

Define the server→runner request/result shape for compact and reset, independent of Workflow Action Input and Agent definitions:

```
SessionCommandRequest  = { sessionId, runtime, runtimeSessionId, runnerId, workDir, command, expectedRuntimeSessionId? }
SessionCommandResult   = { ok, runtimeSessionId?, error? }   // error ∈ {conflict, missing, unavailable}
```

- The runner handler fulfils the command from this shape alone — it does not read Workflow Action Input or Agent definitions (satisfies the "source-independent handler" requirement).
- For **Compact**, the result carries no new `runtimeSessionId` (binding unchanged); #409 wires `result.ok` to a successful `summarize`.
- For **Reset**, the result carries the replacement `runtimeSessionId`; #409 wires it to `client.session.create()`.
- A `missing` error maps to D5's `runtime_session_missing` response.

The handler registration mirrors the existing `registerFollowupHandler` / `registerCancelHandler` pattern (free function, explicit deps). This issue lands the types, the server-side dispatch, and contract tests with a fake handler; #409 swaps the fake for the real SDK calls inside `OpenCodeRuntime`.

*Alternatives considered:* Reuse the existing `ReceiveFollowup` / `CancelAgentSession` SignalR methods by overloading their payload. Rejected — followup is fire-and-forget and cancel needs a reply, whereas compact/reset need a reply *and* a server-side state mutation (the guard + rebind). A dedicated `SessionCommand` invocation keeps each contract honest about its reply semantics.

### D9 — Wire migration: `acpSessionId`/`coderSessionId` → `runtimeSessionId` (+ `runtime`)

Rename the JSON property on every DTO that carries the physical session id:

- `AgentSessionMetadataDto.AgentRuntimeSessionId`: `acpSessionId` → `runtimeSessionId`.
- `AgentSessionSummaryDto.AgentRuntimeSessionId`: `acpSessionId` → `runtimeSessionId`.
- `WorkflowSessionDto.AgentSessionId`: `acpSessionId` → `runtimeSessionId`.
- `RuntimeSessionLineageEntryDto.AgentRuntimeSessionId`: `agentRuntimeSessionId` → `runtimeSessionId` (and add `runtime`).
- `RunnerAgentSessionResponse` / `RunnerGenericAgentSessionResponse`: `acpSessionId` → `runtimeSessionId`.
- Runner `followup-handler.ts` payload field `acpSessionId` → `runtimeSessionId`.
- Web `event-envelope.ts` normalisation and ~10 model/test files: read `runtimeSessionId`, stop emitting `acpSessionId`/`coderSessionId`.

The C# property names (`AgentRuntimeSessionId`) stay as the internal domain handle; only the serialised wire name changes, so grain state and stored events are untouched (no data migration). `ProjectEventsRoutes.cs:103` drops `coderSessionId` from the event-property allow-list.

*Alternatives considered:* Keep `acpSessionId` as a deprecated alias during transition. Rejected — the spec mandates removal, and the codebase is pre-1.0 with no external API consumers to keep compatible.

### D10 — Follow-up and Cancel semantics confirmed across both sources

Follow-up already joins the active turn (runner `connection.prompt`) and emits a `session.input` event without creating a TaskRun/AgentJob. Cancel already interrupts only the current turn and returns honest state. This issue:

- Ensures the workflow-scoped followup and the generic followup share the same "join-active / start-idle, no new work unit" wording and the same `sessionId`-stable response.
- Confirms Cancel never deletes the AgentSession (it already doesn't) and documents it as outside the idle boundary.
- Drops `acpSessionId` from the followup event payload (D9).

No behavioural change is needed beyond the wire rename; the design explicitly records that these two commands are already spec-compliant so #409 and reviewers do not re-litigate them.

## Risks / Trade-offs

- **[Reset happy-path untestable end-to-end until #409]** -> The server-side guard, idle boundary, stable-id response, and `runtime_session_missing` path are fully testable with a fake runner handler; the real replacement creation is deferred to #409 by design. Contract tests pin the shape #409 must satisfy.
- **[`runtime` absent on legacy rows]** -> Legacy lineage/binding rows have no `Runtime`. They degrade to hidden on the wire and surface as "runtime session missing" on the next command — exactly the spec's legacy behaviour. No backfill is performed (non-goal).
- **[Wire rename is breaking across server/runner/web]** -> All three packages ship together from one repo; the rename lands atomically in the same change. No partial-upgrade window exists in the deployment model.
- **[Expected-binding guard rejects legitimate retry after a concurrent Reset]** -> This is the intended behaviour: a retried Reset whose view is now stale must re-read the current binding. The conflict names the actual current binding so the caller can recover in one round-trip.
- **[Removing `BuildNewAgentSessionId` changes the grain command signature]** -> Existing spec tests (`AgentSessionGrainPersistenceSpecs`, `AgentSessionContextEventPublishingSpecs`) construct `CompactAgentSessionCommand`/`ResetAgentSessionCommand` with `NewAgentSessionId`; these are updated as part of the change. The `NewAgentSessionId` parameter becomes `ExpectedRuntimeSessionId` (Reset) / removed (Compact).
- **[Web has ~100 `acpSessionId`/`coderSessionId` references]** -> The migration is mechanical but wide. Mitigated by the normalisation layer (`event-envelope.ts`) being the single read site for most consumers; tests are updated alongside.

## Migration Plan

This is a code-only change with no stored-data migration (explicit non-goal). Deployment is atomic across server/runner/web/cli from the monorepo.

1. **Domain + grain first.** Add `Runtime` to the binding/lineage/event; split `CompactAsync`/`ResetAsync`; add expected-binding guard; add `runtime_session_missing` check. Update server spec/unit tests.
2. **Routes + commands.** Remove `BuildNewAgentSessionId`; rewrite workflow compact/reset routes; add generic compact/reset routes; update `AgentSessionRecoveryResult` and response shapes.
3. **Runner contract.** Land the `SessionCommand` request/result types, handler registration, and contract tests with a fake handler.
4. **CLI.** Rewrite `mo issue session compact|reset` help/output; add `mo agent session compact|reset`. Update `CliIssueSessionSpecs`.
5. **Wire rename.** Migrate DTOs, runner payloads, and web references in one pass; update affected web tests.
6. **Docs.** Align `design/agent-execution.md` / `design/runtimes/opencode.md` 实装差距 and `docs/cli-reference.md` gap notes with landed behaviour.

**Rollback:** Revert the commit set. Because no stored data is rewritten, rollback restores the previous id-rotation behaviour without data inconsistency — legacy rows produced before the change remain queryable under either code version (the only observable difference is the response id and the wire field name).

## Open Questions

- Should the generic compact/reset routes live under `/agent-sessions/{sessionId}` (alongside followup/cancel) or under a new `/agents/{agentRef}/sessions/{sessionId}` path to match the launch surface? Proposal leans `/agent-sessions/{sessionId}` for parity with followup/cancel; confirm during implementation.
- Does the `SessionCommandResult` need to carry context-window counters post-compact, or does the existing `usage.updated` event stream remain the source of truth? Current design keeps events as the source of truth; the result only signals ok/missing/conflict + the replacement id for Reset.
- The workflow-scoped followup route still emits both top-level `workflowRunId`/`sessionName` *and* the unified `target` shape for legacy-runner compatibility. Once ACP runners are fully removed (#409/Epic 46), the top-level fields can be dropped — track as a follow-up, not in this issue.
