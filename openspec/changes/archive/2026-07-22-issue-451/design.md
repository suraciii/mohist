## Context

Pi Workflow turns already execute end-to-end (#450): `PiRuntime`, the `mohist/pi` Action, runtime-aware Workflow Session binding, and the runtime-neutral Session transcript/tool/status/compaction/model/usage/cost/lineage views are all landed. The session-command contract, idle-concurrency boundary, expected-binding guard, and stable-identity model shipped in #407 and work end-to-end for OpenCode Follow-up/Cancel.

Pi-bound AgentSessions are nonetheless read-only in the Session page. Four independent gaps cause this:

1. **Server gate** — `AgentSessionGrain.EnsureCommandRuntimeAvailable` (`Sessions/Grains/AgentSessionGrain.cs:534`) throws for `runtime == "pi"`. It is called from `CompactAsync`, `ResetAsync`, `BeginSessionCommandAsync`, and `BeginFollowupAsync`, so all four commands are blocked before they reach the Runner.
2. **Runner Follow-up/Cancel hardcode OpenCode** — `followup-handler.ts` and `cancel-handler.ts` build requests with `runtime: "opencode"` and accept only an `openCodeRuntime` accessor; `RunnerHost.resolveFollowupTarget` (`runtime/host.ts:189`) rejects any binding whose runtime is not `opencode`.
3. **Runner `SessionCommand` handler is scaffolded but unwired** — `session-command-handler.ts:48` registers `conn.on("SessionCommand", …)` inside `registerSessionCommandHandler`, which has **no call site** in `packages/runner/src/` (retired in issue-410 T-004). The live `RunnerSignalRClient.registerHandlers()` registers Follow-up, Cancel, workspace/git, and workflow-status only. So the server's compact/reset dispatch (`RunnerSessionCommandDispatcher` → SignalR `SessionCommand`, 15 s timeout, null → `Unavailable`) has no Runner responder.
4. **`PiRuntime` has no command methods** and `PiSdkSession` (`runtime/pi/sdk.ts`) lacks `compact()`; the Follow-up/Cancel/Compact/Reset channel mapping in `design/runtimes/pi.md` «Session 命令» is unimplemented.

The wire is already runtime-aware where it matters: every command payload carries `runtime` taken from the persisted AgentSession binding (`AgentSessionFollowupRoutes`, `AgentSessionCancelRoutes`, `RunnerSessionCommandDispatcher` stamp it from `session.Runtime.Runtime`). So this is a routing + channel-implementation gap, not a contract gap.

Constraints: the `design/runtimes/pi.md` deep-module rule forbids a generic `AgentRuntime` interface — `PiRuntime` and `OpenCodeRuntime` stay parallel. Tests must not use real Pi, process, network, DB files, or wall clock; all SDK access is behind the existing `PiSdkFactory` seam. The #407 command contract is reused unchanged.

## Goals / Non-Goals

**Goals:**
- Route all four session commands to the bound runtime: server admits `pi`; Runner Follow-up/Cancel/`SessionCommand` handlers dispatch on `binding.runtime`.
- Implement the four Pi channels in `PiRuntime` per `design/runtimes/pi.md`: Follow-up (steer while busy, prompt+preflight while idle), Compact (native `session.compact()`), Reset (new session file + carry model/thinking), Cancel (`abort()` + stop confirmation).
- Wire the existing `SessionCommand` handler scaffolding (dedup, journal, expected-binding and result-shape validation) into the live Runner for Pi compact/reset.

**Non-Goals:**
- New command types; any change to OpenCode command behavior (regression-guarded).
- Runtime-aware model catalog / Web model selector; AgentJob runtime selection (Mohist Agent stays OpenCode).
- Migrating prior Pi conversation context into a Reset session.
- Re-architecting toward a shared `AgentRuntime` interface.

## Decisions

### D1 — Server: drop the Pi throw, keep the contract

Remove the `pi`-only throw in `EnsureCommandRuntimeAvailable`. `IsRuntimeRegistered` already recognises `pi` (`AgentSessionGrain.cs:530`), and the Reset fallback for unregistered runtimes (`BeginSessionCommandAsync`) is unaffected because `pi` is registered. No wire, DTO, or route change — the payloads already carry `runtime`.

- *Alternative*: per-command runtime allowlist. Rejected — the contract is runtime-uniform by design, and an allowlist would re-introduce the per-runtime branching the #407 contract removed.

### D2 — Runner runtime selection: accessor pair, not a registry

Thread a `piRuntime` accessor alongside the existing `openCodeRuntime` accessor into `RunnerSignalRClient` options and `RunnerHost`, and add one pure selector `resolveCommandRuntime(binding, { openCodeRuntime, piRuntime })` returning the runtime whose backend matches `binding.runtime.toLowerCase()` or `null`. Follow-up, Cancel, and the `SessionCommand` handler all call this one selector.

- *Alternative*: a `Map<string, AgentRuntime>` registry or a shared `AgentRuntime` interface. Explicitly rejected by `design/runtimes/pi.md`: the two runtimes are intentionally parallel deep modules with independent boundary types (`runtime: "pi"` vs `runtime: "opencode"`). An accessor pair mirrors the existing `openCodeRuntime: () => OpenCodeRuntime | null` pattern and adds no new abstraction.

### D3 — Follow-up / Cancel handlers dispatch on the binding runtime

In `followup-handler.ts` and `cancel-handler.ts`, replace the hardcoded `runtime: "opencode"` target with the binding's runtime, and invoke the runtime returned by D2. `resolveFollowupTarget` admits both `pi` and `opencode` (drop the `!== "opencode"` rejection). The busy/idle decision stays inside each runtime's `followup()` — the handler remains fire-and-forget (`void runtime.followup(...)` resolving into outbox terminal events), exactly as it is for OpenCode today.

### D4 — `SessionCommand` handler: reuse the scaffold, route by runtime

Wire `registerSessionCommandHandler` into `RunnerSignalRClient.registerHandlers()` with a `SessionCommandHandler` that branches on `request.runtime`:

- `pi` → `PiRuntime.compact` / `PiRuntime.reset` (new).
- `opencode` → returns `{ ok: false, error: "unavailable" }`.

The existing scaffolding (`session-command-handler.ts`) already provides in-flight dedup by `(sessionId, operationId)`, the durable journal (`session-command-journal.ts`), expected-binding validation, and result-shape validation (`isValidSessionCommandResult`: compact must omit `runtimeSessionId`; reset must return a non-empty `runtimeSessionId` differing from `request.runtimeSessionId`). None of this changes; we only supply the `handler`.

- *Why the `opencode` branch returns `unavailable`*: the canonical compact/reset routes already dispatch `SessionCommand` to a Runner that has no handler today, so the effective production behavior for OpenCode compact/reset is already `Unavailable` — a SignalR invocation with no registered client handler resolves to null promptly (the 15 s timeout in `RunnerSessionCommandDispatcher` is only a backstop), and `null` is mapped to `Unavailable` (`RunnerSessionCommandDispatcher.cs:50`) → 503 `runner_unavailable`. Wiring the handler returns that `unavailable` explicitly; the outcome (command unavailable) is unchanged, so OpenCode user-visible behavior is preserved. Implementing OpenCode native compaction on the Runner is out of scope (non-goal) and owned separately.
- *Alternative*: scope the handler to respond only for `pi` and let `opencode` keep falling through to the null/timeout path. Rejected — returning an explicit `unavailable` is the same outcome without relying on the transport backstop.

### D5 — `PiRuntime.followup`: branch on `isStreaming`; idle path projects events via an observer

Follow-up determines busy/idle from the physical Pi session's `isStreaming` (the same field `runTurn` uses for abort confirmation):

- **Busy** (`isStreaming`): `await session.steer(text)`. The running turn's projection is already owned by the active `runTurn` subscription; Follow-up adds nothing to projection. Resolve accepted. (`steer` does not acquire the per-session prompt mutex of D10 — it injects into a running turn.)
- **Idle**: start a new user-initiated turn. Acquire the per-session prompt mutex (D10), set up a `createPiProjector` subscription, call `session.prompt(text, { expandPromptTemplates: false, preflight })`, and **resolve the Follow-up as accepted when the preflight reception callback fires** (or as a failure if preflight rejects — e.g. missing model/credentials). A background continuation holds the mutex and the subscription, projects events through the observer, and tears both down when `prompt()` resolves. The mutex is what prevents a queued workflow turn or a second concurrent Follow-up from issuing a colliding `prompt()`; `isStreaming` alone is not sufficient (see D10).

Because Pi has no OpenCode-style persistent global event stream, the idle Follow-up's turn events need a projector for their lifetime. `PiRuntime.followup(request, observer?)` therefore takes an optional observer (mirroring `runTurn`'s `PiTurnObserver`), and the Follow-up handler builds that observer from the runtime-event outbox it already holds. The request shape still mirrors `RuntimeFollowupRequest`; the observer is a second argument so the boundary type stays aligned with OpenCode.

- *Alternative*: maintain a persistent per-session subscription in `PiRuntime` set up at open. Rejected — it duplicates `runTurn`'s per-turn projection lifecycle and complicates the one-prompt-per-session invariant. A Follow-up-scoped subscription is bounded and matches `runTurn`.

### D6 — `PiRuntime.cancel`: abort + honest stop confirmation that reaches the user

Resolve/open the session (missing file → `missing-session` + `resetDiagnostic`), then reuse the existing `abortAndDiagnose` pattern: `await session.abort()`, then read `session.isStreaming` (and the event sequence) to confirm stop. The cancel result facts carry an explicit `stopConfirmed: boolean` (`true` when `isStreaming` cleared and/or the event sequence confirmed stop; `false` otherwise). Both outcomes return `cancelled: true` (the abort was attempted), but `stopConfirmed: false` is a first-class field, not merely a diagnostic.

`CancelAgentSessionReply` is `{ state: string }` today (`session-target.ts:148-150`) and `cancel-handler.ts:85-88` discards runtime diagnostics, so a diagnostic cannot reach the user. The honesty signal therefore needs a first-class field: add optional `interruptUnconfirmed?: boolean` to `CancelAgentSessionReply`. The cancel handler sets `interruptUnconfirmed: true` when the runtime returns `cancelled: true` with `stopConfirmed === false`; the Server mirrors that field into the cancel HTTP response (as it already mirrors `state`). OpenCode cancel always returns `stopConfirmed: true` (its abort is authoritative), so its reply is unchanged. The user/API can therefore never see a stop-unconfirmed cancel reported as a clean `cancelled`.

### D7 — `PiRuntime.compact`: native compaction, idle-guarded, no synthetic fallback

Resolve/open the session (missing → `missing-session`). Guard `session.isStreaming` → return `{ ok: false, error: "conflict" }` (the grain already enforces logical idleness; this is the physical-session backstop). Subscribe a projector, `await session.compact()`, project compaction events, return `{ ok: true }` with **no** `runtimeSessionId` (per `isValidSessionCommandResult`'s compact rule). On any compact failure, return a failure carrying the underlying error — never synthesize a summary or fabricate a compaction record.

### D8 — `PiRuntime.reset`: create empty session, carry model/thinking, return new path

Best-effort open the current session to read its model/thinking level ("if available"); if the bound file is missing, Reset still proceeds (it is the recovery operation) and skips carry-over. Call `services.createSession(workDir)` to get a new empty session file, apply the carried model/thinking level onto it, cache the new `PiSdkSession` under the new path, and return `{ ok: true, runtimeSessionId: <new path> }`. The new path naturally differs from `request.runtimeSessionId`, satisfying `isValidSessionCommandResult`'s reset rule. **The server-side grain performs the binding replacement and lineage append** (`CompleteResetAsync` consuming the returned id) — `PiRuntime.reset` does not touch lineage itself, matching the existing contract where the Runner returns a replacement id and the grain commits it. The prior session file stays on disk for audit.

### D9 — Boundary types and SDK surface

Add Pi request/result types mirroring the OpenCode shapes: `PiFollowupRequest`/`PiFollowupResult`, `PiCancelRequest`/`PiCancelResult`, `PiCompactRequest`/`PiCompactResult`, `PiResetRequest`/`PiResetResult` in `runtime/pi/types.ts`. `PiCancelResult` facts carry `stopConfirmed: boolean` (D6). Extend `PiSdkSession` (`runtime/pi/sdk.ts`) with `compact()` and extend `prompt`'s options with the `preflight` reception callback; wire both through the real SDK factory. Like #450, **smoke-verify the `preflight` and `compact()` SDK surface on the pinned `@earendil-works/pi-coding-agent` version before implementation**, recording the result under `openspec/changes/issue-451/sdk-smoke-verification.json` (precedent: `issue-450/sdk-smoke-verification.json`).

### D10 — Per-physical-session prompt mutex in `PiRuntime`

The Pi SDK permits one in-flight `prompt()` per physical session. Workflow turns are serialized externally by `WorkflowSessionTurnCoordinator`, but that coordinator is keyed by logical workflow session and is acquired only by the workflow/check executor — the Follow-up path (`followup-handler` → `runtime.followup`) and any concurrent Follow-up bypass it. Relying on `isStreaming` (D5) is insufficient: a workflow turn queued in the coordinator, or a second concurrent idle Follow-up, can observe `isStreaming === false` in the gap before another prompt starts and issue a colliding `prompt()`.

`PiRuntime` therefore owns a per-physical-session async mutex (a `Map<sessionPath, Promise-chain>`). `runTurn`, the idle Follow-up path, and `compact` acquire it for the duration of their mutating SDK call (`prompt` / `compact`); `steer` (busy Follow-up) does not acquire it (it injects into a running turn), and `reset` operates on a newly created session so it is unaffected. This enforces the one-prompt-per-session invariant regardless of caller path. Retrofitting `runTurn` to acquire the mutex is safe: the external coordinator already serializes workflow turns, so the internal lock is a redundant serialization for that path and a load-bearing one for the Follow-up path.

The mutex serializes execution but does not impose FIFO ordering across caller types: an idle Follow-up may run before a workflow turn that was queued in the coordinator but had not yet started. That is acceptable — the session is not actively executing, the product permits Follow-up as the exception, and the invariant (no double prompt) holds.

## Risks / Trade-offs

- **[Idle Follow-up needs a turn-length projector subscription]** → Mitigation: D5's Follow-up-scoped observer, torn down on `prompt()` resolve. Residual: an idle user Follow-up has no Workflow-style 60 min deadline; a hung turn could hold a subscription. Mitigation is user-initiated Cancel (D6) plus `shutdown()` disposing all sessions. Whether to apply an automatic Follow-up turn budget is captured as an open question.
- **[Concurrency between a user Follow-up turn and a Workflow turn on the same session]** → Mitigation: D10's per-physical-session prompt mutex in `PiRuntime` serializes `runTurn`, idle Follow-up, and `compact`; `isStreaming` is no longer the sole guard. Residual: an idle Follow-up may run before a coordinator-queued workflow turn (acceptable; the invariant is no double prompt, not strict FIFO).
- **[`preflight` and `compact()` SDK surface unverified on the pinned version]** → Mitigation: smoke-verify first (D9); if `preflight` is unavailable, idle-Follow-up reception confirmation cannot be implemented as specified and blocks that scenario — escalate before implementation rather than substituting a non-confirming prompt.
- **[Wiring `SessionCommand` must not change OpenCode's semantic outcome]** → Mitigation: today's production outcome is already `Unavailable` (an unhandled `SessionCommand` invocation resolves to null promptly; the 15 s timeout is a backstop), so the router's explicit `unavailable` is the same outcome. Implementing real OpenCode native compact/reset on the Runner is explicitly out of scope.
- **[Reset returns a new path; server commits the rebind]** → Already required by the existing contract and `isValidSessionCommandResult`. No new risk; covered by the existing recovery spec tests once the Pi branch returns a valid replacement path.
- **[interrupt-unconfirmed must reach the user honestly]** → Mitigation: D6 makes `stopConfirmed` a first-class result field and adds `interruptUnconfirmed` to `CancelAgentSessionReply`, mirrored by the Server into the HTTP response; a stop-unconfirmed cancel can never be reported as a clean `cancelled`. The spec scenario "Cancel reports interrupt-unconfirmed when stop is unknown" is the regression guard.

## Migration Plan

This change is additive behind the existing contract — no persisted schema change, no data rewrite. The only wire/DTO addition is an optional `interruptUnconfirmed` field on the cancel reply (D6), mirrored end-to-end (runner reply type → server DTO → HTTP response); it defaults to absent/false, so older callers are unaffected.

- **Order of rollout**: ship the Runner change first (D2–D9), then the Server gate lift (D1). The Runner handler is dormant for OpenCode (returns `unavailable`, same as today) and only activates for `pi`; lifting the server gate afterward enables Pi commands. If the Runner change ships alone, Pi commands remain blocked at the grain (safe).
- **Coordination**: Server and Runner should release together; if a Pi command reaches a Runner that lacks the handler, the result is `unavailable` (graceful degradation, no state corruption, no silent new session).
- **Rollback**: revert D1 (re-blocks `pi` → commands fail with the pre-change error); the Runner handler becomes dormant again. No data migration to reverse. Existing Pi-bound AgentSessions and their lineage are untouched.
- **Docs**: remove the "Pi Session 命令仍未实装" gap entries in `docs/actions/pi.md` (line 186) and `design/runtimes/pi.md` (line 403) once landed.

## Open Questions

1. **SDK `preflight` shape** — exact callback signature/option name for the reception hook on the pinned version. Blocker for the idle-Follow-up reception-confirmed requirement; resolved by the D9 smoke.
2. **SDK `session.compact()` signature and compaction event payload** — needed for D7 projection. Resolved by the D9 smoke.
3. **Idle user Follow-up turn budget** — Workflow turns get a fixed 60 min deadline; the design does not assign one to a user-initiated Follow-up. Decide whether to apply a budget or rely on user Cancel (leaning: rely on Cancel, matching OpenCode Follow-up).
4. **OpenCode compact/reset product ownership** — confirmed non-functional through the canonical Runner-dispatched path today (no Runner handler; the grain's server-side summary `CompactAsync(CompactAgentSessionCommand)` has no route caller). This issue intentionally leaves OpenCode behavior unchanged; a separate issue should own OpenCode native compact/reset on the Runner. Confirm with the issue author that this scoping is intended.
