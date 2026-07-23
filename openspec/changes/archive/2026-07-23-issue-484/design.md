## Context

AgentSession is the stable logical conversation; the Runtime Session (OpenCode/Pi) is a mutable physical facet owned by the execution backend. The target model is already specified in `design/agent-execution.md` and `docs/agents.md`; this change closes the documented implementation gaps.

Current state (key facts, with sources):

- **No persisted activity.** Status is derived two ways: a 5-minute time window over `LastDataAt` → `active`/`inactive` (`AgentSessionJsonHelper.StatusName`, `AgentSessionJsonHelper.cs:11-18`), and a transcript scan for the most recent `session.closed` part → `completed`/`failed`/`stopped`/`cancelled` (`AgentSessionQuerier.ReadTerminalStateAsync`, `AgentSessionQuerier.cs:457-494`). The grain only stores `CurrentTurnEndedAt` (`AgentSession.cs:267`), set when a `session.closed` event arrives (`AgentSessionGrain.cs:974-975`).
- **Terminal transcript events.** `session.closed` (`TranscriptEventTypes.cs:7`), `session.followup_completed`, `session.followup_failed` (`:8-9`) are written by the runner (`followup-handler.ts:230`, `workflow-agent-session-reporter.ts`, `pi.ts`) and consumed to derive status (`AgentSessionQuerier.cs:470-476`, web `useWorkflowRunSessions.ts:75-90`).
- **Physical lineage persisted.** `RuntimeSessionLineage` (`AgentSession.cs:261`) is an append-only list inside the serialized grain state, appended on every attach/rebind (`AgentSession.Transitions.cs:79-82,174-180`), exposed in DTOs (`AgentSessionReadModels.cs`, `AgentSessionDtoMapper.cs:117`), and rendered in Web (`useGenericSessionDataSource.ts:92-96`). It is not a separate table — it lives in the `AgentSessions.State` JSON blob.
- **Missing session → caller must Reset.** The runner reports `missing-session` deterministically (OpenCode `runtime.ts:264-276`, Pi `runtime.ts:97-330`) but does **not** auto-create on the task/follow-up path. The server surfaces `RuntimeSessionMissingException` → HTTP 409 `runtime_session_missing` + `hint:"reset"` (`AgentSessionFollowupRoutes.cs:108-111`, `AgentSessionRecoveryRoutes.cs:217-221`). Only the explicit Reset/Compact path (`SessionCommand` SignalR → `dispatchPiReset`) creates a new session.
- **Dispatch topology.** Workflow work is HTTP-poll (`POST /api/runner/{id}/poll`); the runner resolves/creates the Runtime Session inline in `executor.ts:320-354` (creates only when `runtimeSessionId === null`; a stale id fails inside `runTurn`). SignalR is reverse-direction only: `ReceiveFollowup`, `CancelAgentSession`, `SessionCommand`. AgentJob follows the same inline pattern (`agent-job-executor.ts:182-190`). The runtime SDK connection lives on the runner; the server never talks to the runtime directly.
- **CAS already exists** for Reset: `EnsureExpectedRuntimeSession` (`AgentSession.Transitions.cs:188-198`) throws `StaleRuntimeSessionBindingException`; `RebindRuntimeSession` (`:145-186`) is the existing replace transition (it appends lineage and emits `AgentSessionRuntimeBound(previousId)`).

Stakeholders: Workflow (TaskRun), Agent (AgentJob), Session (grain/querier/API), Runner (executor/adapters), Web (Session page), CLI. Per `AGENTS.md` the project is in active development with no version-compatibility constraint.

## Goals / Non-Goals

**Goals:**
- Make AgentSession persistently reusable: an execution outcome only returns activity to `idle`; no terminal session lifecycle.
- Own activity as a single authoritative field on the session aggregate; remove all history-derived status.
- Add confirmed-missing recovery across the TaskRun, AgentJob, and idle Follow-up entry points, with at-most-one empty session creation and exactly-once input submission.
- Collapse to a single current binding; remove lineage.
- Replace `session.closed`/`session.followup_*` with `session.activity` and `session.context_reset`.

**Non-Goals** (from issue):
- No per-execution entity, execution ID, or execution-grouped transcript.
- No physical session history/lineage persistence or display.
- No migration/replay of physical session content (messages, prompts, tool calls) into a new session.
- No OpenCode Compact implementation or Pi Compact behavior change.
- No new CLI command surface (`mo session`, archive/delete) — that is #479.
- No change to TaskRun/AgentJob/Workflow-recovery/retry result adjudication.
- No observability metrics/status API — that is #470.

## Decisions

### D1. Activity is a persisted authoritative field on the grain, not derived

Add an `Activity` enum (`Idle`/`Active`/`Unknown`) to `AgentSessionStatusSnapshot` (`AgentSession.cs:255`), defaulting to `Idle` on creation. The grain is the sole writer. Delete `StatusName`'s time-window heuristic (`AgentSessionJsonHelper.cs:11-18`) and the `ReadTerminalStateAsync` transcript scan (`AgentSessionQuerier.cs:457-494`); DTOs (`ToWorkflowDto`, `ToSummaryDto`) expose the activity directly.

**Rationale.** The whole failure mode is status being reconstructed from history. A single authoritative field removes the three duplicated derivations (server binary, querier terminal scan, web mappers) and the race between them.

**Alternatives.** (a) Derive activity by replaying transcript `session.activity` events on read — rejected: re-introduces a scan, is non-authoritative under concurrent writes, and forces every consumer to agree on replay semantics. (b) Keep `CurrentTurnEndedAt` + window as a fallback — rejected: it is the bug we are removing.

### D2. The runner reports activity transitions; the server referees and persists

The runner owns the physical facts (turn started, turn ended, stop uncertain); the server persists the authoritative transition and records the `session.activity` transcript event. Specifically:

- `idle → active` is atomic with `session.input` acceptance: the grain transitions and records both in one write (the `session.input` path already exists in `AppendEventsAsync`, `AgentSessionGrain.cs:951-971`; extend it to set `Activity = Active`).
- `active → idle` replaces today's `session.closed`/`session.followup_*` turn-end signals. When `runTurn`/`followup()` resolves (success, failure, or confirmed cancel), the runner publishes a `session.activity:{idle}` event; the grain transitions.
- `→ unknown` is set when a stop or input-acceptance is uncertain: the runner reports it (e.g. cancel with `InterruptUnconfirmed`), or the server sets it on runner disconnect mid-turn (via the existing `RunnerConnectionTracker`) when a session is `active`.

**Rationale.** The design doc fixes ownership: "Session 是 current binding 与 activity 的状态裁判。Runner 只报告物理事实." The runtime SDK connection is on the runner, so only the runner knows when a turn physically ended; the server must not guess from TaskRun completion ("历史执行结果不能推导 activity").

**Alternatives.** (a) Server infers `active → idle` from TaskRun/AgentJob completion — rejected: violates work-result independence and couples session lifecycle to workflow progress. (b) Server polls runner liveness to drive transitions — rejected: extra round trips and the runner already emits the relevant facts.

### D3. Recovery is runner-initiated, server-confirmed (CAS), in one shared routine

Wire confirmed-missing recovery into a single runner-side routine (`resolveOrRecoverBinding`) invoked by all three entry points — workflow task (`executor.ts:320-354`), AgentJob (`agent-job-executor.ts:182-190` / `:124-158`), and idle Follow-up (`followup-handler.ts:89-120`). The ordering follows `design/agent-execution.md` §解析与替换顺序:

```text
expected = currentBinding from server
if expected absent:        create candidate; server replaceBinding(absent, candidate)
else:                      resolve(expected) against runtime
  ready              -> use expected
  definitely-missing -> create candidate; server replaceBinding(expected, candidate)
  uncertain/other    -> fail, preserve binding
server recordInput(selected)        // session.input + idle->active, atomic
runtime submitInputExactlyOnce(selected, input)
```

The runner performs the physical resolve/create (it holds the SDK); the server performs `replaceBinding` and `recordInput` (it is the referee). Input is submitted **only after** the server confirms the new binding is current. The runner journals the submit by `operationId` (`session-command-journal.ts`) so a retry does not double-submit.

This reuses the existing CAS primitive (`EnsureExpectedRuntimeSession` throws `StaleRuntimeSessionBindingException`) and the existing attach/HTTP-report path (`attachWorkflowAgentSession`), extended to carry `expected` for the recovery case.

**Rationale.** The runtime SDK is on the runner; a server-initiated pre-dispatch resolve would add a SignalR round trip that duplicates the resolution the runner already does inline, and the poll model does not fit a synchronous pre-check. Keeping recovery on the runner side, with the server as binding referee, matches both the dispatch topology and the design doc's ownership rule ("Runner 只报告 resolve/create 事实；replaceBinding 与 recordInput 都由 Server 裁决").

**Alternatives.** (a) Server-orchestrated recovery via a new `ResolveBinding` SignalR call before dispatch — rejected: doubles resolution work and conflicts with HTTP-poll dispatch. (b) Runner rebinds directly without server confirmation — rejected: no CAS, so a stale recovery can overwrite a concurrent Reset, violating the expected-binding invariant. (c) Duplicate the logic per entry point — rejected: three copies of the same ordering drift; extract one routine.

### D4. One server-side CAS rebind operation, shared by Reset, runtime-change, and recovery

Generalize `RebindRuntimeSession` (`AgentSession.Transitions.cs:145-186`) into the single binding-replacement path. It: requires `Activity == Idle`; compares the full expected binding (runnerId + runtime + runtimeSessionId) against current; on match, replaces the binding, resets Runtime context to empty, and atomically writes a `session.context_reset` event carrying only the reason (`reset`/`runtime-change`/`missing-recovery`). Reset, runtime-change, and recovery all route through it. Remove the lineage append and the `AgentSessionRuntimeBound(previousId)` event from this path.

**Rationale.** The spec requires Reset, runtime-change, and recovery to use "the same binding-replacement path." Today Reset already uses `RebindRuntimeSession`; extending it to recovery avoids a second mutation path and guarantees one CAS semantics. The `session.context_reset` event replaces lineage as the user-visible reset marker (the design doc: "只需记录一次 session.context_reset…不记录物理 Session 沿革").

**Alternatives.** (a) Keep a separate recovery mutation — rejected: two CAS code paths to keep consistent. (b) Write `session.context_reset` non-atomically after rebind — rejected: the spec requires atomic ordering before the next `session.input`.

### D5. Drop lineage entirely; single current binding only

Remove `RuntimeSessionLineage`/`RuntimeSessionLineageEntry` from `AgentSessionStatusSnapshot` (`AgentSession.cs:249-261`), the `AppendLineageEntry` helper (`AgentSession.Transitions.cs:304-326`), the lineage DTO + mapper (`AgentSessionReadModels.cs`, `AgentSessionDtoMapper.cs:117`), and the Web history view (`useGenericSessionDataSource.ts:92-96`, `SessionDataSource.ts`). The snapshot keeps only `AgentRuntimeSessionId` (current binding). Because lineage is inside the Orleans `[GenerateSerializer]` state blob, removing the field deserializes cleanly from old state (nullable, ignored) with no schema step.

**Rationale.** Lineage is the physical-session-history the target model explicitly excludes ("AgentSession 只保存 current binding，不保存物理会话沿革"). Keeping it hidden would maintain a dual source of truth and invite re-leakage.

**Alternatives.** (a) Keep lineage internally, hide from DTOs — rejected: dead state with no consumer, plus the invariant ("no history") is only enforceable by removing it. (b) Move lineage to a separate audit table — rejected: explicitly a non-goal ("保存、查询或展示物理 Runtime Session history").

### D6. Transcript contract: add two events, remove three

- **Add** `session.activity` (payload: `activity`, `observedAt`) and `session.context_reset` (payload: `reason` ∈ {`reset`,`runtime-change`,`missing-recovery`}, `observedAt`; no session IDs) to `TranscriptEventTypes`/`TranscriptPartTypes` (`TranscriptEventTypes.cs`) and the web catalog (`canonical-event-types.ts:58-75`).
- **Remove** `session.closed`, `session.followup_completed`, `session.followup_failed` from writers and consumers. The grain's `session.closed` special-casing (`AppendEventsAsync` `:974-975`, `ClassifySessionClosedPayload` `:1153`, `AppendSystemEventsAsync` `:751-756`, `AppendTerminalCloseAsync` `:777`) and the followup-terminal handling (`CompleteFollowupTerminalsAsync`, `TerminatesFollowupLease` `:1343-1345`) are replaced by the activity-transition path (D2).
- **Compaction stays separate.** Pi/OpenCode Compact (context compaction within the *same* binding) keeps its existing `compaction`/`compaction_event` events and is out of scope. `session.context_reset` is only for binding replacement.

**Rationale.** `session.input` already exists as the input boundary; `session.activity` gives the continuous-state signal that `session.closed`/`followup_*` incorrectly modeled as terminal lifecycle facts. The design doc names exactly these two additions and three removals.

**Alternatives.** (a) Keep `session.closed` as a deprecated alias — explicitly rejected by the design doc ("实施不保留 session.closed 别名"). (b) Fold compaction into `session.context_reset` — rejected: compaction preserves the binding and is a different operation.

### D7. API and Web consume the single activity field

- API: `AgentSessionSummaryDto`/`WorkflowSessionDto` expose `activity` (`idle`/`active`/`unknown`) instead of the derived `status`. The `409 runtime_session_missing` + `hint:"reset"` responses on the follow-up/cancel/recovery routes become either automatic recovery (where D3 conditions hold) or a binding-preserving failure with no Reset hint.
- Web: replace the three duplicated status mappers (`useGenericSessionDataSource.ts:33-50`, `useIssueSessionDataSource.tsx:97-116`, `buildGenericSessionMetadata.ts`) with one activity-based derivation; stop subscribing to `session.closed`/`session.followup_*` for optimistic status patches (`useWorkflowRunSessions.ts:75-90`, `useCoderSessions.ts:113`, `useSessionTranscript.ts:482,514,528`). The runtime-session history view is removed with lineage (D5).

**Rationale.** A single field consumed uniformly is the contract in the activity spec ("API responses, command eligibility, and the Session page SHALL determine state from the current activity value only").

**Alternatives.** Map activity back onto the old `completed`/`failed`/`running` vocabulary — rejected: leaks terminal semantics back into the API and re-couples consumers to execution outcomes.

## Risks / Trade-offs

- **[Runner crash between turn-start and turn-end leaves a session stuck `active`] →** Recovery requires `idle`, so the stuck state surfaces on the next input. Mitigation: the server sets `unknown` on runner disconnect mid-turn (D2) via `RunnerConnectionTracker`; `unknown` rejects new input until resolved, so it cannot silently mask a hung session as idle.
- **[Double input submission during recovery under network partition] →** The runner journals the submit by `operationId` and submits only after the server confirms the binding (D3); `session.input` + `idle→active` is atomic on the server, so a second attempt sees `active`/stale-expected and is rejected. Residual risk is a partition after server-ack but before runtime-submit — the journal makes the retry idempotent.
- **[Breaking API/Web contract] →** Coordinated server+web change in one deployment; no external API consumers exist today (single self-hosted deployment). The CLI does not derive session status from history, so it is unaffected.
- **[Removing legacy transcript rows loses audit history] →** Trade-off vs. a clean model. Mitigation: old `session.closed`/`followup_*` rows can remain as inert transcript parts (no consumer reads them post-change); a destructive cleanup migration is optional and only drops `Type IN (session.closed, session.followup_completed, session.followup_failed)` parts if audit retention is not required.
- **[OpenCode Reset was not implemented (`command-runtime.ts:169-171` returned `unavailable`)] →** This change wires the OpenCode `SessionCommand` reset through `OpenCodeRuntime.createSession` (`runtime.ts:120-154`) + the unified rebind (D4), mirroring the Pi reset path. T-003 covers this; no new OpenCode `reset()` method is needed. OpenCode Compact remains out of scope.

## Migration Plan

Active development, single deployment, no version-compatibility constraint — so the migration is code-driven:

1. **Grain state.** Removing `RuntimeSessionLineage` from the `[GenerateSerializer]` snapshot deserializes old state cleanly (the field was nullable). No EF column change (lineage was inside the `State` JSON blob). Existing sessions lose lineage on next load — intended.
2. **Transcript rows.** New code stops writing `session.closed`/`session.followup_*` and starts writing `session.activity`/`session.context_reset`. Historical legacy rows are inert (no consumer). An optional idempotent migration deletes `AgentSessionTranscriptParts` with `Type IN ('session.closed','session.followup_completed','session.followup_failed')` if audit retention is unnecessary; otherwise they remain unread.
3. **Activity backfill.** Existing sessions have no `Activity` field; default to `Idle` on deserialization (the snapshot default). A session mid-execution at cutover will appear `idle`, which is safe (recovery/input re-establishes state).
4. **Rollback.** Revert the code. Lineage is already gone from old state (acceptable — it is being removed). No forward-only data format is introduced. Re-adding the field repopulates from `null`/empty.

## Resolved Questions

- **`active → idle` signal granularity.** The runner emits one `session.activity:{idle}` per accepted input's turn end, matching the `session.input` boundary. Long inter-tool gaps within a turn stay `active`; only turn completion transitions to `idle`.
- **OpenCode Reset enablement.** OpenCode Reset is included in this change. The spec requires uniform Reset semantics (AC10) and the runner-side `SessionCommand` reset for OpenCode currently returns `unavailable` (`command-runtime.ts:169-171`). T-003 wires the OpenCode reset command through `OpenCodeRuntime.createSession` + the unified rebind (D4), mirroring the Pi reset path (`dispatchPiReset`), so an OpenCode session that is `idle` and receives a Reset creates a new empty session instead of failing.
- **`unknown` watchdog scope.** The runner-disconnect → `unknown` server watchdog is included in this change (T-001). Without it, a runner crash mid-turn leaves a session `active` until the next input. A minimal disconnect watcher via `RunnerConnectionTracker` transitions active sessions to `unknown` when their runner disconnects.

No open questions remain.
