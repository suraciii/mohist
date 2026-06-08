# Event Bus & SignalR Audit

**Scope:** `packages/server/src/Mohist.Server/Infrastructure/Events/`, `packages/server/src/Mohist.Server/Events/Hub/`, and the consumer wiring in `Workflow/`, `Issue/`, `Runner/`, `Sessions/`.
**Date:** 2026-06-08
**Status:** Read-only review. No code was modified.
**Reference:** `design/event-mechanism.md` (681 lines; the source of truth for intended behavior).

---

## 0. Executive Summary

| Question | Answer |
|---|---|
| Is the pipeline production-ready? | **No.** The most prominent P0 — the SignalR bridge does not forward the new reverse-DNS CloudEvents to the Web — defeats the entire bus-driven workflow re-design that the rest of the system assumes. Every in-process subscriber (IssueGrain, WorktreeCleanupService, AgentSessionRunnerBridge) and the new reverse-DNS catalog entries are wired, but the user-facing wire path is broken. |
| Top 3 risks | **R1 (P0):** `EventBridge.StartAsync` subscribes to `EventCatalog.All` (legacy snake_case only); the 30 new reverse-DNS `EventCatalog.ReverseDns.*` events are emitted to the in-process bus and consumed by the few grain subscribers, but **never reach SignalR / the Web**. **R2 (P0):** `IEventBus.Emit` is synchronous and runs handlers on the emit thread; a slow or throwing handler blocks the emitting grain (proven by `EventBusSpecs.Emit_SlowSubscriber_DoesBlockCaller`). The design (§Failure Modes) says the opposite: "Handlers must be async-fire-and-forget." **R3 (P1):** 45/54 of `EventCatalog.All` legacy names are dead registrations — no producer emits them, so the EventBridge is forwarding subscriptions to nothing. The Web is not aware of the 30 reverse-DNS names that *do* get emitted. |
| Top 3 quick wins | **W1:** Add `EventCatalog.ReverseDns` entries to the EventBridge subscription loop (`EventBridge.cs:31-34`). One line. **W2:** Delete the 45 dead legacy names from `EventCatalog.All` (or move them to a "Deprecated/Reserved" list) so the EventBridge stops paying per-emit cost for 45 useless subscriptions. **W3:** Wrap the per-handler dispatch in a `Task.Run` (or expose a fire-and-forget `EmitAsync`) so a slow handler can no longer block `WorkflowGrain.CommitAsync` / `WorkflowRunStore.Publish`. |

Counts: **3 P0**, **5 P1**, **6 P2**, **2 P3**.

---

## 1. CloudEvents 1.0.2 Compliance

Each emit goes through `CloudEventFactory.Create` (`CloudEventFactory.cs:14`). The factory populates:

| Attribute | Required? | Set in `Create`? | Notes |
|---|---|---|---|
| `id` (unique) | yes (§3.1) | yes — `Guid.NewGuid().ToString()` (line 36) | Per-emit unique, not replayed. ✓ |
| `source` (URI) | yes (§3.1) | yes — caller-supplied `Uri` (line 37) | OK in `CloudEventFactory`; see P2-1 for the legacy fall-back path. |
| `specversion` | yes (§3.1) | yes — `SpecVersion = "1.0"` set by library, factory declares `const string SpecVersion = "1.0"` (line 12) | ✓ |
| `type` | yes (§3.1) | yes — caller-supplied | ✓ |
| `time` | optional | yes — `DateTimeOffset.UtcNow` (line 39) | ✓ |
| `subject` | optional | yes when supplied | ✓ |
| `datacontenttype` | optional | yes — `"application/json"` (line 41) | ✓ |
| `data` | optional | yes when `data != null` (lines 44-47) | ✓ |
| `dataschema` | optional | **never** | Catalog entries (per `design/event-mechanism.md` §"The catalog is a documentation artifact") should carry a `dataschema` URI; the current `EventCatalog` has no schema-URI field. P3. |

### P2-1. Legacy `Emit(string, object)` emits with `source = "about:blank"`

`Infrastructure/Events/InMemoryEventBus.cs:44-52` — when the legacy `Emit(string eventName, object data)` path receives a non-`CloudEvent` payload and there is a typed subscriber, it synthesizes the envelope with `source: new Uri("about:blank", UriKind.Absolute)`. The CloudEvents 1.0.2 spec says `source` MUST be a non-empty URI-reference; `"about:blank"` is technically valid but is the spec-blessed placeholder for "intentionally absent source" and is widely filtered by downstream tooling. The design §Bus Implementation says "We populate all four on every emit" — this violates the intent. **Severity: P2.** **Fix:** either reject the legacy path at compile time (it is unused in production — only `EventBusSpecs.cs:25, 36, 47, 64, 80, 99` calls it) or supply a real `source` derived from the originating type.

### P2-2. `Id` is a fresh `Guid`, not deduplicated with persistence

`EventStore.cs:32` calls `_eventBus.Emit(evt)` **after** `db.SaveChangesAsync(ct)` (line 23), so the row in `EventRow` and the bus `Id` are not coupled. The persisted `EventRow.Id` is a long auto-increment (assigned by `EventValueGenerators.EventTypeGenerator`/`EventTimeGenerator`/`EventSpecVersionGenerator` — see `EventValueGenerators.cs`); the bus `Id` is a fresh `Guid`. There is no `busId` column on `EventRow`. This means **replay (design §Step 7) cannot correlate a persisted row to its bus event.** **Severity: P2.** **Fix:** stamp the same `Id` onto `EventRow` (or add a `BusId` column) and write it from `CloudEventFactory.Create`.

### Compliant emits (audit-verified)

- `WorkflowGrain.cs:119` — `com.mohist.workflow.lease-expired`, source `/mohist/workflow/{runId}/work/{workId}`. Required attributes set. ✓
- `WorkflowGrain.cs:942` — `stage_changed`, source `/mohist/workflow/{runId}/stage/{stageId}`. ✓
- `RunnerGrain.cs:229` — `com.mohist.runner.disconnected`, source `/mohist/runner/{RunnerId}`. ✓
- `RunnerHub.cs:40` — `com.mohist.runner.disconnected`, source `/mohist/runner/{runnerId}`. ✓
- `WorkflowRunStore.cs:108` — emits per `WorkflowEventSerializer.BusType(payload)`. ✓ (but see §3 for the 4 reverse-DNS names `BusType` will never produce).
- `EventStore.cs:27` — same as `WorkflowRunStore`, used by the API layer (`/api/workflow-runs/{id}/events`). ✓
- `AgentSessionGrain.cs:294,317,330,357,373,397` — 6 emit sites; all required attributes set. ✓

---

## 2. EventCatalog Drift

### Catalog inventory

| Set | File | Count |
|---|---|---|
| `EventCatalog.All` (legacy) | `EventCatalog.cs:14-71` | 54 strings (the audit brief says 52 — actual count is 54; the two extras are `coder_recovery_status` and `plan_round_complete` in the `ReverseDns`-only block which appear to be legacy in spirit but live in the legacy array) |
| `EventCatalog.ReverseDns` | `EventCatalog.cs:77-115` | 30 const strings (the audit brief says 24 — actual count is 30) |
| `EventBusEventTypes.All` (legacy, used by nothing) | `EventBusEventTypes.cs:5-52` | 45 strings; **no caller** (`grep` for `EventBusEventTypes\.\|EventBusEventTypes\.All` returns only the type's own line 7) |

### P0-1. `EventCatalog.All` and `EventBusEventTypes.All` are out of sync

`EventCatalog.cs:14-71` lists 54 legacy names. `EventBusEventTypes.cs:5-52` lists 45. Both are claimed to be "all emitted types" in their respective doc comments. `EventBusEventTypes.All` is **dead code** — no caller in the entire `Mohist.Server` source tree references it. It exists only as legacy scaffolding from before the redesign. **Severity: P0** because it misleads readers and any future test that asserts "every emitted type is registered" will pass against the wrong table. **Fix:** delete `EventBusEventTypes.cs` outright (or move it to a `_` folder); have `EventBridge.cs:31` reference `EventCatalog.All` (which it already does) and have a `Specs/SystemSpecs/EventCatalogSpecs` that asserts each `EventCatalog.ReverseDns` constant has a producer (currently false — see §3).

### P2-3. Catalog assertions test only "non-empty" — they do not assert producer/consumer coverage

`CloudEventFactorySpecs.cs:61-78` — both `EventCatalog_AllTypesAreNonEmpty` and `EventCatalog_ReverseDnsConstants_AreNonEmpty` check non-empty strings and the `com.mohist.` prefix. They do **not** assert that each reverse-DNS constant has a producer (`WorkflowEventSerializer.BusType` mapping) or a subscriber. The design §"Test coverage assertion" called for `EventCatalogTests fails if an emit call uses a type not in the catalog, or a catalog entry has no producer`. That test does not exist. **Severity: P2.** **Fix:** add a Roslyn-based test that walks every `_eventBus.Emit(CloudEventFactory.Create(...))` call site, extracts the `type:` argument, and asserts membership in `EventCatalog.All ∪ EventCatalog.ReverseDns`; separately, walk the catalog and assert each entry has a producer.

---

## 3. Dead Registrations

### P0-2. **45 of 54** legacy names in `EventCatalog.All` are never emitted (dead registrations)

Verifying each legacy name against emit sites:

**Emitted (9):** `stage_changed` (WorkflowGrain:943), `coder_session_started`/`_failed`/`_cancelled`/`_completed`/`_status_changed` (AgentSessionGrain:295, 386-388, 374), `coder_text_chunk` (AgentSessionGrain:318), `coder_thought_chunk` (AgentSessionGrain:331), `coder_tool_call` (AgentSessionGrain:358).

**Never emitted (45):** `comment_added`, `agent_started`, `agent_completed`, `agent_paused`, `agent_error`, `approval_requested`, `tool_call`, `agent_text_chunk`, `main_tool_call`, `coder_recovery_status`, `ralph_task_update`, `ralph_loop_progress`, `plan_session_update`, `plan_round_start`, `plan_round_complete`, `agent_liveness_status`, `agent_usage_update`, `merge_queued`, `merge_started`, `merge_completed`, `merge_failed`, `merge_blocked`, `rebase_started`, `rebase_progress`, `rebase_completed`, `rebase_conflict`, `agent_conflict_resolution_started`, `agent_conflict_resolution_completed`, `agent_conflict_resolution_failed`, `agent_blocked`, `check_started`, `check_update`, `check_suite_status_changed`, `stage_task_update`, `integration_started`, `integration_step_updated`, `integration_completed`, `integration_failed`, `integration_preflight_refreshed`, `base_drift_detected`, `rebase_opportunity`, `user_attention_requested`, `schedule_triggered`, `schedule_completed`, `schedule_failed`.

The Web's `LiveTaskProvider.tsx:127-294` *does* subscribe to most of these (it has `case 'merge_queued': case 'merge_completed': case 'rebase_conflict': ...` switches). They will never fire. The user sees a stale UI. **Severity: P0** because the audit's central product failure — "Web receives no signal for 16 of 20 user-visible states" — is still mostly true. **Fix:** (a) prune the dead names from `EventCatalog.All` and from `LiveTaskProvider.tsx` switches; (b) audit each missing emit and either add the producer (likely the right fix) or remove the Web switch.

### P1-1. **4 of 30** reverse-DNS names have no producer (dead catalog entries)

Verified via `grep -E 'WorkflowRunRetrying|WorkflowRunRerunning|TaskStarted|CheckStarted' packages/server/src` — only the catalog declarations themselves match; no producer. Worse, `WorkflowEventSerializer.BusType` (`WorkflowEventSerializer.cs:19-39`) **throws** `InvalidOperationException` for unmapped types (line 38: `_ => throw ...`). So if a future `WorkflowRun.Retry()` returns a `WorkflowRunRetrying` payload and routes through `WorkflowRunStore.SaveAsync`, the bus emit will throw, the in-memory transaction is committed, and the bus has lost the event. **Severity: P1** (latent crash; will only fire when the retry/rerun flow is built, which the design §Step 9 anticipates). **Fix:** add the four missing `BusType` cases (they can map to the existing constants `EventCatalog.ReverseDns.WorkflowRunRetrying` / `…Rerunning` / `…TaskStarted` / `…CheckStarted`); alternatively drop them from the catalog until the producer exists.

### P1-2. **26 of 30** reverse-DNS names have no in-process subscriber

Subscribers via `OnType(...)`:
- `com.mohist.workflow.run.completed` — `IssueGrain.cs:74`, `WorktreeCleanupService.cs:41` (2)
- `com.mohist.workflow.run.stopped` — `IssueGrain.cs:75` (1)
- `com.mohist.workflow.run.failed` — `IssueGrain.cs:76` (1)
- `com.mohist.runner.disconnected` — `AgentSessionRunnerBridge.cs:50` (1)
- The other 26 reverse-DNS names — **0 subscribers**

Catalog entries with no subscriber (26): `WorkflowRunStarted`, `WorkflowRunResumed`, `WorkflowRunPaused`, `WorkflowRunRetrying`, `WorkflowRunRerunning`, all 5 `Stage*`, `TaskStarted`, `TaskCompleted`, `TaskFailed`, `CheckStarted`, `CheckPassed`, `CheckFailed`, `CheckPending`, `RepairScheduled`, all 5 `AgentSession*`, `LeaseExpired`, `IssueCompleted`, `IssueCancelled`.

For `LeaseExpired`: the design §"Runner / session lifecycle" table says "AgentSessionGrain → mark failed". `AgentSessionRunnerBridge` does NOT subscribe to `lease_expired`, only to `runner_disconnected`. The 5-minute `LeaseTimeout` (`WorkflowGrain.cs:95`) emits `lease_expired` every heartbeat tick once expired, but no one is listening. **Severity: P1** (the explicit "stuck session" gap the design is supposed to close). **Fix:** add `_subscriptions.Add(_bus.OnType(EventCatalog.ReverseDns.LeaseExpired, OnLeaseExpired))` to `AgentSessionRunnerBridge` (or to a new `WorkflowLeaseBridge`).

For `IssueCompleted` / `IssueCancelled`: design §"The Issue ← Workflow Example" §5 says `EventBridge → Web → LiveTaskProvider` listens to `com.mohist.issue.completed` to invalidate the kanban. But IssueGrain does not emit `IssueCompleted` (no `Emit` calls anywhere in `IssueGrain.cs`); the bus subscriber is the consumer, not the producer. **No one emits these events.** The IssueGrain completes/fails internally but never publishes a CloudEvent. **Severity: P1** (the canonical example in the design is half-wired). **Fix:** add `Emit` calls in `IssueGrain.CompleteWorkAsync` / `AbortWorkAsync`.

### P0-3. **EventBridge does not forward any reverse-DNS event to the Web**

`Events/Hub/EventBridge.cs:31-34`:

```csharp
foreach (var type in EventCatalog.All)
{
    _subscriptions.Add(_bus.OnType(type, ForwardToHub));
}
```

`EventCatalog.All` is the legacy list (54 names). The reverse-DNS `EventCatalog.ReverseDns.*` constants (30 names) are **not iterated**. The Web therefore receives only legacy `stage_changed`, `coder_session_*`, etc. — never `com.mohist.workflow.run.completed`, `com.mohist.workflow.stage.approval-requested`, etc. The design §"SignalR bridge" and the docstring at `EventBridge.cs:12` both explicitly claim that the bridge forwards "both the legacy snake_case names and the new reverse-DNS names". The implementation does not. **Severity: P0** (the bus-driven Web UI re-design is not delivered). **Fix:** change the loop to iterate the union: `foreach (var type in EventCatalog.All.Concat(EventCatalog.ReverseDns.All))` (after adding a public `EventCatalog.ReverseDns.All` projection), or split into two loops.

---

## 4. Bus Thread Safety

The bus (`Infrastructure/Events/InMemoryEventBus.cs:6`) uses:
- `ConcurrentDictionary<string, List<Action<object>>>` for legacy subscribers
- `ConcurrentDictionary<string, List<(Func<CloudEvent,bool>, Action<CloudEvent>)>>` for typed subscribers
- `lock(list)` on the per-key list to take a snapshot
- `lock(list)` again on `Off` and on the type-add path

### P0-4. Synchronous dispatch blocks the emitting grain

`InMemoryEventBus.cs:130-149` (`DispatchTyped`) and `110-128` (`DispatchLegacy`) iterate the snapshot **synchronously** on the emit thread. A handler that takes 30s blocks the emitter for 30s. In Orleans, this is a grain re-activation: the calling grain cannot answer other requests while waiting.

Empirically confirmed by the existing test: `EventBusSpecs.cs:89-104` (`Emit_SlowSubscriber_DoesBlockCaller`) — the test's name and the body are honest about the current behavior; the design §Failure Modes table says the opposite: "Handlers must be async-fire-and-forget; emit thread returns immediately; handler runs as a background task; the emit's ordering guarantee is per-handler, not cross-handler."

The most damaging call site is `WorkflowRunStore.cs:113` (`_eventBus.Emit(evt)` inside `SaveAsync`, which is called inside the transaction's `try/finally` — line 53) and `EventStore.cs:32` (called after `db.SaveChangesAsync` but before the API call returns). If `IssueGrain.OnWorkflowCompleted` (which calls `_ = CompleteWorkAsync(wrId)`) is fast in itself, but if any future handler throws *or* awaits an Orleans grain call, the workflow grain stalls.

The `WorktreeCleanupService.OnWorkflowCompleted` (`WorktreeCleanupService.cs:54`) is `async void` and does `_git.RemoveWorktreeAsync(...)`. If git is slow or hangs, the bus blocks.

**Severity: P0** because this is the mechanism by which a single misbehaving handler can deadlock the entire workflow pipeline. **Fix:** change `Emit(CloudEvent)` to enqueue handlers as `Task.Run` (with a bounded `Channel` for backpressure) or expose `EmitAsync` and await all handlers with a timeout. Add a per-handler timeout (the design §"Three levels" calls for 10-min task timeout, 60-min stage timeout; the bus has none).

### P1-3. Re-entrant `Emit` on the same `type` would stack-overflow

If a typed handler synchronously calls `_bus.Emit(cloudEvent)` with the same `cloudEvent` (same `Type`), `DispatchTyped` (`InMemoryEventBus.cs:130`) re-locks the same list and re-dispatches the same snapshot. The handler invokes itself. There is no recursion guard. No current production handler does this, but the public API contract permits it and a future "reaction" handler might. **Severity: P1** (latent, will fire on misuse). **Fix:** track in-flight events by `Id` in a `ConcurrentDictionary<string, byte>` and skip dispatch if already in flight.

### P2-4. `OnAny` key derives from `filter.Method.GetHashCode()` — fragile

`InMemoryEventBus.cs:84`:

```csharp
var key = filter.Method.GetHashCode().ToString();
```

Two different lambda expressions with identical bodies, or two lambdas in different compilation units, can collide on `Method.GetHashCode()` (the runtime's implementation is not collision-free). More importantly, calling `OnAny` with the same lambda instance twice will append to the same list — fine — but calling it with a *new* lambda that has the same method body will likely map to a different list, which makes the unsubscribe-by-Dispose semantics depend on a happy collision. No production caller uses `OnAny` today (`grep` shows only the interface declaration and the method itself), so this is dormant. **Severity: P2.** **Fix:** use a `List` of `(filter, handler)` directly without trying to bucket by method hash; `Emit(CloudEvent)` then has to do linear scan over `OnAny` filters, but the cardinality is bounded.

### P2-5. Per-list `lock` is correct, but the same key can be in both dictionaries

`Emit(CloudEvent)` (line 55) checks `_handlers` and `_typedHandlers` independently. A handler added via legacy `On("foo", ...)` AND a handler added via typed `OnType("foo", ...)` will both be invoked for an emit of type `foo`. The legacy handler receives the *whole* `CloudEvent` object (line 61: `DispatchLegacy(legacyList, type, cloudEvent)`) — not the original payload. This silently changes the contract for legacy subscribers. No production legacy subscriber exists today (only `EventBusSpecs.cs:23,46,61-62,77-78,93` in tests). **Severity: P2.** **Fix:** when migrating, keep both dictionaries but document the contract — or fold them into one.

### P3-1. Handler removed during dispatch

Per `DispatchTyped`/`DispatchLegacy`, the snapshot is taken under the per-list lock (`InMemoryEventBus.cs:113-115, 133-135`). A handler removed via `Off`/`Dispose` after the snapshot will still run (the snapshot is a `ToArray()` copy). This is the intended "in-flight semantics" — the test `EventBusSpecs.Off_RemovesSubscriber` (line 42) passes because Off happens between Emits, not during. Documented behavior; flagged as P3 for awareness.

### P3-2. Bus exposes per-handler try/catch but no dead-letter table

`InMemoryEventBus.cs:119-126, 140-147` — each handler's exception is caught and logged at **Warning** level. The design §Failure Modes says "Log at error level + write to `DeadLetter` table; do not retry automatically". Neither the level nor the persistence is implemented. **Severity: P3** (recoverability, not correctness).

---

## 5. SignalR Hub

### P1-4. Per-connection project filtering trusts the query string

`Events/Hub/MohistHub.cs:22-24, 31-33`:

```csharp
var projectId = Context.GetHttpContext()?.Request.Query["projectId"].ToString();
if (!string.IsNullOrEmpty(projectId))
    await Groups.AddToGroupAsync(Context.ConnectionId, $"project:{projectId}");
```

`OnConnectedAsync` adds the connection to `project:{projectId}` based on the **caller-supplied** `?projectId=...` query string. Any client can claim any project and receive that project's events. The design §"Project Scoping" (line 494) acknowledges this: "currently trusted, audit-confirmed as a security gap — see API audit." This audit focuses on the event bus, not auth; flagging as P1 because the bus forwarder trusts whatever the bridge receives. **Fix:** read the project from an authenticated `ClaimTypes.NameIdentifier`/custom claim; reject connections whose `projectId` claim does not match the query string.

### P2-6. Query-string key is case-sensitive

`MohistHub.cs:22, 31` uses `Request.Query["projectId"]` (camelCase). ASP.NET Core preserves the case of query keys, so `?projectid=p1` would NOT match. The Web's `createEventsConnection` (`events-hub.ts:8`) hard-codes `projectId=`. Drift hazard if a proxy or URL rewriter lowercases the parameter. **Severity: P2.** **Fix:** use `Request.Query.TryGetValue("projectId", out var v) || Request.Query.TryGetValue("projectid", out v)` or normalize.

### P2-7. The Web's `useEventsConnection` rebuilds on `projectId` change, but the bridge `Subscribe` happens once at startup

`events-hub.ts:43` — the SignalR connection is closed and re-created when the React `projectId` changes. Server-side, the `EventBridge` is a singleton with a fixed list of subscriptions; this is fine. The hub's per-connection group add/remove is correct (`MohistHub.cs:19-35`).

### Wire forwarding is correct

`EventBridge.ForwardToHub` (`EventBridge.cs:50-63`) builds a `CloudEventEnvelope` via `CloudEventEnvelope.From` (line 94-103) and dispatches via `_hub.Clients.Group(group).OnEvent(...)`. Per-handler try/catch is present (line 52, 59-62). ✓

---

## 6. Wire Envelope Stability

The wire shape is `CloudEventEnvelope` (`EventBridge.cs:83-104`):

```csharp
public sealed record CloudEventEnvelope(
    string Type, object? Payload, string Id, string Source, string SpecVersion,
    string? Subject, string? Time, string? DataContentType,
    Dictionary<string, object?>? Extensions);
```

The Web's `LiveTaskProvider.tsx:25-49` mirrors this shape and `unwrapEnvelope` falls back to a raw object (line 45-48) if the shape does not look like an envelope. The legacy fall-back path is exercised only for un-migrated producers; the only such producer in this codebase would be a test calling `bus.Emit("foo", data)` directly, which the production code never does.

### P1-5. `Extensions` in the wire is a `Dictionary<string, object?>` — TypeScript sees `unknown`

`CloudEventEnvelope.cs` (record) — `Extensions` is `Dictionary<string, object?>?`. When this is serialized to JSON and re-hydrated in the Web, the values are JSON primitives or nested objects. `LiveTaskProvider.tsx:34` declares `extensions?: Record<string, unknown>`. The Web does not consume `extensions` anywhere in the `handleEvent` body (`grep` for `parsed.extensions` in `LiveTaskProvider.tsx` returns 0 hits). The new bus types (`com.mohist.workflow.run.completed`) carry `projectid`, `workflowrunid`, `issueno` in extensions — the Web ignores them, falling back to the URL pathname for issue number (line 56: `getCurrentIssueNumber()`). This means the Web can do everything by URL, but it cannot, e.g., invalidate TanStack queries by `projectid` from the event. **Severity: P1** (the new envelope is plumbed but not consumed). **Fix:** update `LiveTaskProvider.tsx:142-294` switch to also check the new reverse-DNS types (currently only `stage_changed` legacy is handled, line 143); use `parsed.extensions?.projectid` for invalidation keys.

### P3-3. `SpecVersion` is a string in the wire; spec uses `"1.0"` (no major.minor split)

`CloudEventEnvelope.cs:99` — `SpecVersion: evt.SpecVersion?.VersionId ?? "1.0"`. The CNCF `CloudNative.CloudEvents` library parses `specversion` from the envelope. All current emits are `"1.0"` (see `EventValueGenerators.cs:33` and `CloudEventFactory.cs:12`). ✓

---

## 7. Reverse-DNS Migration & Backward Compatibility

### Status: half-migrated

| Producer | Uses reverse-DNS? | Uses legacy string? |
|---|---|---|
| `WorkflowRunStore.Publish` | yes (via `WorkflowEventSerializer.BusType`) | no (it produces 16 of the 30 reverse-DNS names) |
| `EventStore.AppendWorkflowEventAsync` | yes (via `BusType`) | no |
| `WorkflowGrain.EmitStageChanged` | no | yes (`"stage_changed"`, line 943) |
| `WorkflowGrain.CheckLeaseAgeAsync` | yes (`EventCatalog.ReverseDns.LeaseExpired`) | no |
| `AgentSessionGrain` (6 sites) | no | yes (6 legacy names) |
| `RunnerGrain.HandleTimeoutAsync` | yes | no |
| `RunnerHub.OnDisconnectedAsync` | yes | no |

### P1-6. No producer emits `EventCatalog.ReverseDns.AgentSession*` (5 names)

`AgentSessionGrain.cs:294, 317, 330, 357, 373, 397` — all 6 emit sites use legacy snake_case names (`coder_session_started`, etc.) hard-coded as `type: "..."` string literals. The design §"Naming convention" table says `coder_session_started` should be **replaced** by `com.mohist.agent-session.started`. The catalog has both, but only the legacy is emitted. The reverse-DNS AgentSession names are catalog-only and unreachable. **Severity: P1** (the design explicitly calls for this migration; without it, the new envelope cannot be used by AgentSession consumers). **Fix:** introduce `AgentSessionEventSerializer.BusType(payload)` (mirror of `WorkflowEventSerializer.BusType`) and emit via that. Better: leave the legacy emit in place for Web back-compat AND emit a duplicate under the reverse-DNS name (the design §"Backwards compat" §Q6 discusses this).

### P1-7. `WorkflowEventSerializer.Unwrap` has a `null` default — `Unwrap(payload)` can return null

`WorkflowEventSerializer.cs:82-101` — the switch has no `default` case. C# will warn but still compile to "return null". The caller `BusType` (line 19) and `FromData` (line 60) then do `_ => throw InvalidOperationException`. If a new `WorkflowEvent` subclass is added without updating `Unwrap` and `BusType`, every emit on the bus throws and the transaction is rolled back. This is a "footgun" of the type-driven dispatch. **Severity: P1** (latent). **Fix:** make `Unwrap` exhaustive (`default => throw`) and add a Roslyn analyzer or test that enumerates the `WorkflowEvent` hierarchy.

### Backward compatibility

The `WorkflowEventSerializer.Type(payload)` (line 11) returns the C# class name (e.g. `"WorkflowRunStarted"`) — this is what gets stored in `EventRow.Type` via the `EventTypeGenerator` value generator (`EventValueGenerators.cs:15`). The bus type is the reverse-DNS name. So persisted `EventRow.Type` is **legacy PascalCase**, and bus `Type` is **reverse-DNS** — they do not match. `WorkflowEventSerializer.FromData` (line 60) reads back using the legacy PascalCase name. This is **intentional** (per the comment at lines 13-18) and is consistent: the persisted `EventRow.Type` is the storage key, the bus `Type` is the wire name. Fine.

---

## 8. Per-Handler Try/Catch

`InMemoryEventBus.cs:119-126, 140-147` — both `DispatchLegacy` and `DispatchTyped` wrap each handler invocation in `try { } catch (Exception ex) { _log.LogWarning(...); }`. **One bad handler does not break dispatch to others.** ✓

`EventBridge.cs:52-62` — `ForwardToHub` has its own try/catch around the SignalR send. ✓

`IssueGrain.OnDeactivateAsync` (line 79-87) wraps each `sub.Dispose()` in `try { } catch { }` — best-effort cleanup during deactivation. ✓

`WorktreeCleanupService.OnWorkflowCompleted` (line 54-84) is `async void` with a try/catch around the body. Per-event exception does not propagate (good), but `async void` exceptions that escape before the try-catch would crash the process. **Severity: P2-8** (minor; all bodies are wrapped today, but a future refactor that adds code before the `try` would expose `async void` to the bus thread). **Fix:** use `async Task` and `Emit(CloudEvent)` should `await` handlers; or wrap the entire `OnRunnerDisconnected` / `OnWorkflowCompleted` body in a top-level try/catch.

---

## 9. Orleans Grain Subscription Lifecycle

`IssueGrain.cs:63-87`:

```csharp
public override async Task OnActivateAsync(CancellationToken ct)
{
    _issue = await _issueStore.LoadAsync(GrainKey);
    _subscriptions.Add(_eventBus.OnType(EventCatalog.ReverseDns.WorkflowRunCompleted, OnWorkflowCompleted));
    _subscriptions.Add(_eventBus.OnType(EventCatalog.ReverseDns.WorkflowRunStopped, OnWorkflowStopped));
    _subscriptions.Add(_eventBus.OnType(EventCatalog.ReverseDns.WorkflowRunFailed, OnWorkflowFailed));
}

public override Task OnDeactivateAsync(...)
{
    foreach (var sub in _subscriptions)
    {
        try { sub.Dispose(); } catch { }
    }
    _subscriptions.Clear();
    return Task.CompletedTask;
}
```

Pattern is correct: subscribe on activate, dispose on deactivate, swallow disposal exceptions.

`WorktreeCleanupService.cs:39-52` and `AgentSessionRunnerBridge.cs:48-61` are `IHostedService` with the same pattern. ✓

### P0-5. **Lost events during transition are unhandled**

If a `WorkflowRunCompleted` is emitted while the IssueGrain is **deactivated** (a common Orleans state — grain deactivates after 5 minutes of inactivity by default), no subscriber is registered. The event is gone. The design §"Lost Event Recovery" §Step 6 introduces a lazy reconciliation on read and a daily `IHostedService` walker. **Audit-verified: the lazy reconciliation does not exist in the codebase.** `grep -r 'MohistDefaultWorkflowProjection\|WorkflowReconciliation' packages/server/src` shows the type is mentioned in the design doc but no source file defines it. The daily hosted service is also absent. **Severity: P0** because the canonical example "IssueGrain reacts to `com.mohist.workflow.run.completed`" is racy — it works if the grain is active, and silently fails if it isn't. The user's "issue is stuck InProgress" failure mode is unchanged from the pre-redesign state. **Fix:** implement Step 6 of the design (lazy reconciliation in `GetWorkflowStatusAsync` + daily walker) before claiming the canonical example works.

### P1-8. Hosted service `StartAsync` runs once at app startup, not on each activation

`EventBridge.cs:29-37` — `StartAsync` subscribes to all catalog types. `StopAsync` is a no-op. If a future deployment needs per-tenant event bridges (e.g., one bridge per project silo), the current singleton shape will not work. **Severity: P1** (architectural; not blocking today). **Fix:** document the singleton assumption, or move subscriptions to a per-silo `IHostedService`.

---

## 10. Self-Loop Hazard

A grain subscribing to its own events is a self-loop hazard (the event triggers a handler that emits the same event, ad infinitum).

**Audit-verified:**

- `WorkflowGrain` — emits `LeaseExpired` and `stage_changed`. Does **not** subscribe to any event (no `OnType`/`On` calls in the file). No self-loop. ✓
- `AgentSessionGrain` — emits 6 legacy events. Does **not** subscribe. ✓
- `IssueGrain` — subscribes to 3 reverse-DNS events (`WorkflowRunCompleted/Stopped/Failed`). Does **not** emit (no `Emit` calls in the file). No self-loop. ✓
- `WorktreeCleanupService` (hosted) — subscribes to `WorkflowRunCompleted`. Does not emit. ✓
- `AgentSessionRunnerBridge` (hosted) — subscribes to `RunnerDisconnected`. Does not emit. ✓
- `EventBridge` (hosted) — subscribes to all. Does not emit. ✓

**No self-loop hazard.** The design's invariant "Domain decides / Owning grain emits / Subscriber reacts by calling a command" is honored — grains are either emitters or subscribers, never both. ✓

---

## 11. Design vs Implementation Comparison

`design/event-mechanism.md` is explicit about what should be in the system. This section enumerates each design step and audits the implementation.

| Design § | Claimed | Audit-verified status |
|---|---|---|
| §Step 1 (Adopt CloudEvents 1.0.2 envelope) | All emits use `id`, `source`, `type`, `specversion` | ✓ via `CloudEventFactory` |
| §Step 1 (Web parses the envelope) | Web uses `@cloudevents/sdk-typescript` | **Not verified** — `LiveTaskProvider.tsx:25-49` has its own `CloudEventEnvelope` interface and `unwrapEnvelope`; no `@cloudevents/sdk-typescript` in `packages/web/package.json` per its absence in `LiveTaskProvider.tsx` imports. The Web has its own minimal parser. **Severity: P3** — works, but contradicts the design's "Library" promise. |
| §Step 2 (Wire `WorkflowRunFailed`/`Stopped` to hook chain) | Issue transitions to `Cancelled` on fail/stop | **Partially**: `IssueGrain` subscribes to `WorkflowRunFailed/Stopped` (`IssueGrain.cs:75-76`) and calls `AbortWorkAsync` (line 113). Whether `AbortWorkAsync` actually transitions to `Cancelled` is out of this audit's scope (Issue grain). **Marked done.** |
| §Step 3 (Add `runner_disconnected`) | `RunnerGrain` + `RunnerHub` emit it; `AgentSessionRunnerBridge` reacts | ✓ — emits at `RunnerGrain.cs:229` and `RunnerHub.cs:40`; bridge at `AgentSessionRunnerBridge.cs:50`. ✓ |
| §Step 4 (Rename `IWorkflowCompletionHook` → `IWorkflowLifecycleHook`) | Add `OnFailed`, `OnStopped` | `WorkflowGrain.cs:1060-1066` — `DispatchLifecycleHooksAsync` is a **no-op shim**: "hook dispatch was removed; terminal-side effects flow through the bus". The hook chain has been bypassed, not renamed. **Severity: P2** (technically achieves the design goal via a different path — the bus; but the comment at lines 1021-1024 confirms intentional removal). |
| §Step 5 (IssueGrain subscribes to bus) | `IssueGrain.OnActivateAsync` registers subscriptions | ✓ (`IssueGrain.cs:74-76`); **but** StageApprovalRequested subscription is missing (the design §Subscription Model has it at lines 240-243; the implementation does not). **Severity: P1.** The "needs approval" badge flow is half-wired. **Fix:** add `_subscriptions.Add(_eventBus.OnType(EventCatalog.ReverseDns.StageApprovalRequested, OnStageApprovalRequested))` and the `OnStageApprovalRequested` handler. |
| §Step 6 (Lazy reconciliation + daily hosted service) | Outbox-less recovery via read + walker | **Not implemented** — see P0-5. |
| §Step 7 (Persisted outbox + replay) | `Outbox` table, `OutboxReplayService` | **Not implemented** — design says "in-process bus is a synchronous in-memory fan-out"; `grep -r 'Outbox' packages/server/src` returns 0. Consistent with current scope; **flagged** as a known gap, not a regression. |
| §Step 8 (Replace in-grain hooks with bus-driven reactions) | IssueGrain subscribed, hooks removed | ✓ — `DispatchLifecycleHooksAsync` is a shim; IssueGrain subscribes (see Step 5). |
| §Step 9 (Runner / Session symmetry) | WorkflowGrain subscribes to session-failed, AgentSessionGrain subscribes to run-lifecycle, `runner_dispatched` allocates row, `lease_expired` recovers stuck leases | **Partially**: `lease_expired` emit exists (`WorkflowGrain.cs:119`) but **no subscriber** (see P1-2). `runner_dispatched`, `runner.registered/unregistered/reported-result` are absent from `EventCatalog.ReverseDns`. **Severity: P2** (the design anticipates these as future work, but the catalog advertises the names, see §3 P1-1). |
| §Step 10 (TS runner per-session spawn) | Out of scope | n/a |
| §Failure Modes (handler throws) | Per-handler dead-letter row | Per-handler try/catch is in place; **no dead-letter table** (P3-2). |
| §Failure Modes (silo restart drops events) | Outbox replay | Not implemented. **Severity: P1** as a known gap. |
| §Failure Modes (cross-grain call blocks) | Handlers must be fire-and-forget; emit thread returns immediately | **Violated** — see P0-4. The bus is sync-in-emit-thread; handlers block. |
| §Failure Modes (out-of-order) | Outbox + watermark | Not implemented. n/a in-process. |
| §Failure Modes (project id mismatch) | Log + `project:global` fallback | ✓ — `EventBridge.cs:55` does `$"project:{projectId ?? "global"}"`. ✓ |
| §SignalR bridge (forwards CloudEvent JSON) | `JsonEventFormatter.EncodeStructuredModeMessage` | **Different shape** — the implementation uses a custom `CloudEventEnvelope` record (`EventBridge.cs:83-104`) and serializes via SignalR's default JSON serializer, **not** `JsonEventFormatter`. The wire is not CloudEvents 1.0.2 JSON; it is a SignalR-serialized `CloudEventEnvelope` object. The Web's `unwrapEnvelope` (`LiveTaskProvider.tsx:37-49`) detects the shape. **Severity: P2** — functional, but the wire is a non-standard variant. Any "standard CloudEvents-aware" consumer (Knative, webhooks) cannot consume the SignalR stream directly. |

---

## 12. Additional Findings

### P2-9. The `EventBridge.cs:57` falls back to `envelope.Type` but the variable is in scope

`EventBridge.cs:57`:

```csharp
_ = _hub.Clients.Group(group).OnEvent(cloudEvent.Type ?? envelope.Type, envelope);
```

`cloudEvent.Type` is `string?` and `envelope.Type` is `string` (record non-nullable). The null-coalesce is dead code; `envelope.Type` is always non-null (set from `evt.Type ?? string.Empty` at line 95). **Severity: P2 (cosmetic / dead code).** **Fix:** simplify to `cloudEvent.Type ?? string.Empty`.

### P2-10. `EventBusEventTypes.cs` is dead code

See P0-1. The file declares an array of 45 strings that is never read. Deleting it is safe.

### P2-11. The `Time` field on `CloudEvent` is `DateTimeOffset.UtcNow` (in factory), but `EventRow.Time` is `DateTime.UtcNow` (in `EventTimeGenerator`)

`CloudEventFactory.cs:39` — `Time = DateTimeOffset.UtcNow`. `EventValueGenerators.cs:26` — `DateTime.UtcNow`. The bus event and the persisted row have **different timestamps** (different types, different precision). Replay (Step 7) cannot order them deterministically. **Severity: P2.** **Fix:** share a single timestamp at the boundary (`WorkflowRunStore.SaveAsync`) and write it to both.

### P3-4. `CloudEventEnvelope.Extensions` is rebuilt on every emit

`EventBridge.cs:111-119` — `BuildExtensions` walks `evt.GetPopulatedAttributes()` and builds a fresh `Dictionary<string, object?>` for every emit. The CNCF library's `GetPopulatedAttributes()` materializes a new list each call. For chatty events (e.g. `coder_text_chunk` on every agent token), this is per-chunk allocation. **Severity: P3** (perf, not correctness). **Fix:** cache the extensions on the `CloudEvent` once via the CloudEvents SDK's `GetExtensions()` API, or use a struct-based envelope.

---

## Findings Index

| ID | Severity | File:line | One-line |
|---|---|---|---|
| P0-1 | P0 | `Infrastructure/Events/EventBusEventTypes.cs:5-52` | `EventBusEventTypes.All` is dead code, out of sync with `EventCatalog.All`. |
| P0-2 | P0 | `Infrastructure/Events/EventCatalog.cs:14-71` | 45/54 legacy names are never emitted (dead registrations). |
| P0-3 | P0 | `Events/Hub/EventBridge.cs:31-34` | Bridge subscribes to `EventCatalog.All` (legacy) only; reverse-DNS events never reach Web. |
| P0-4 | P0 | `Infrastructure/Events/InMemoryEventBus.cs:130-149` | Synchronous dispatch blocks the emitting grain on slow handlers. |
| P0-5 | P0 | `Issue/Grains/IssueGrain.cs:63-87` (interaction with no `IssueWorkflowReconciliationService`) | Lazy reconciliation + daily walker (design §Step 6) is unimplemented; events emitted during grain deactivation are lost. |
| P1-1 | P1 | `Infrastructure/Events/WorkflowEventSerializer.cs:19-39` | 4 reverse-DNS names (`WorkflowRunRetrying`/`Rerunning`/`TaskStarted`/`CheckStarted`) have no `BusType` mapping — throws on emit. |
| P1-2 | P1 | `Sessions/Services/AgentSessionRunnerBridge.cs:48-61` | `lease_expired` has no subscriber; the "stuck session" gap is not closed. |
| P1-3 | P1 | `Infrastructure/Events/InMemoryEventBus.cs:130-149` | Re-entrant `Emit` of same-type event from a handler would stack-overflow. |
| P1-4 | P1 | `Events/Hub/MohistHub.cs:22-24` | Per-connection project filter trusts caller-supplied query string. |
| P1-5 | P1 | `web/src/app/providers/LiveTaskProvider.tsx:142-294` | Web ignores the new `extensions` field on the envelope; reverse-DNS events have no UI handler. |
| P1-6 | P1 | `Sessions/Grains/AgentSessionGrain.cs:294-397` | No `AgentSession*` reverse-DNS name is emitted; all 5 are catalog-only. |
| P1-7 | P1 | `Infrastructure/Events/WorkflowEventSerializer.cs:82-101` | `Unwrap` switch missing `default`; new `WorkflowEvent` subclasses cause emit-time crash. |
| P1-8 | P1 | `Events/Hub/EventBridge.cs:29-37` | Singleton `EventBridge` cannot be per-silo/tenant; architectural constraint unstated. |
| P2-1 | P2 | `Infrastructure/Events/InMemoryEventBus.cs:44-52` | Legacy `Emit(string, object)` synthesizes envelopes with `source = "about:blank"`. |
| P2-2 | P2 | `Infrastructure/Data/Events/EventStore.cs:32` | Bus `Id` (Guid) is not persisted on `EventRow`; replay cannot correlate. |
| P2-3 | P2 | `tests/.../SystemSpecs/CloudEventFactorySpecs.cs:61-78` | No assertion that each catalog entry has a producer or a subscriber. |
| P2-4 | P2 | `Infrastructure/Events/InMemoryEventBus.cs:84` | `OnAny` key from `filter.Method.GetHashCode()` is fragile. |
| P2-5 | P2 | `Infrastructure/Events/InMemoryEventBus.cs:55-67` | Legacy `On()` and typed `OnType()` both fire on `Emit(CloudEvent)`; contract drift. |
| P2-6 | P2 | `Events/Hub/MohistHub.cs:22, 31` | Query-string key `projectId` is case-sensitive. |
| P2-7 | P2 | `web/src/shared/api/events-hub.ts:43` | Web rebuilds connection on `projectId` change; server-side is fine but undocumented. |
| P2-8 | P2 | `Sessions/Services/AgentSessionRunnerBridge.cs:63`, `Issue/Services/WorkflowProfiles/WorktreeCleanupService.cs:54` | `async void` handlers — top-level exceptions would crash the bus thread. |
| P2-9 | P2 | `Events/Hub/EventBridge.cs:57` | `cloudEvent.Type ?? envelope.Type` is dead-code; `envelope.Type` is non-null. |
| P2-10 | P2 | `Infrastructure/Events/EventBusEventTypes.cs` | Whole file is dead code. |
| P2-11 | P2 | `CloudEventFactory.cs:39` vs `EventValueGenerators.cs:26` | Bus and DB timestamps use different types (`DateTimeOffset` vs `DateTime`). |
| P3-1 | P3 | `Infrastructure/Events/InMemoryEventBus.cs:113-115, 133-135` | Handler removed during dispatch still runs (snapshot semantics) — documented but worth noting. |
| P3-2 | P3 | `Infrastructure/Events/InMemoryEventBus.cs:124, 145` | Per-handler catch logs at Warning; design calls for Error + DeadLetter table. |
| P3-3 | P3 | (no `dataschema` URI on `CloudEvent` envelopes) | Catalog lacks `dataschema` field; design promised schema-URI for self-describing events. |
| P3-4 | P3 | `Events/Hub/EventBridge.cs:111-119` | `BuildExtensions` allocates a fresh `Dictionary` per emit; per-chunk events (`coder_text_chunk`) cause GC pressure. |

---

## Top 3 Risks (re-stated)

1. **The new bus is wired in-process but invisible to the Web.** P0-3 (EventBridge subscription loop) means that the canonical "issue done by workflow event" reaction works for `IssueGrain` and `WorktreeCleanupService` but the Web's kanban and approval badge never see `com.mohist.workflow.run.completed` or `com.mohist.workflow.stage.approval-requested`. This is the highest-impact correctness gap.
2. **Deactivated grains lose events.** P0-5 (no lazy reconciliation) means the IssueGrain can miss `WorkflowRunCompleted` and remain stuck in `InProgress`. The design §Step 6 is the documented mitigation and is not implemented.
3. **Synchronous, blocking dispatch.** P0-4 (sync handlers on emit thread) means one misbehaving handler can stall an entire workflow run. The design's "fire-and-forget" promise is unmet.

## Top 3 Quick Wins

1. **Fix the EventBridge subscription loop.** `Events/Hub/EventBridge.cs:31` → iterate `EventCatalog.All.Concat(EventCatalog.ReverseDns.All)` (after exposing `ReverseDns.All`). One-line change, closes P0-3.
2. **Prune dead legacy names.** Delete the 45 entries in `EventCatalog.All` and `EventBusEventTypes.All` that have no producer. Closes P0-1 and P0-2 partial.
3. **Wrap dispatch in `Task.Run` or expose `EmitAsync` with per-handler timeout.** `InMemoryEventBus.cs:130-149` → `Task.Run(() => handler(evt))` with `Task.WhenAll` and a `CancellationTokenSource` timeout. Closes P0-4.

---

## Verification Notes

- `codegraph_callers` was used to confirm all `EventCatalog.ReverseDns.*` constant references; only 4 of 30 have any caller (`IssueGrain` ×3, `WorktreeCleanupService` ×1, `AgentSessionRunnerBridge` ×1, `RunnerGrain` ×1, `RunnerHub` ×1, `WorkflowGrain` ×1).
- `grep -E '_eventBus\.Emit|bus\.Emit|Emit\(' packages/server/src` returns 13 emit sites — all audited above.
- `grep -E 'OnType\(|OnAny\(' packages/server/src` returns 6 production sites (5 of which use `OnType`); all audited above. `OnAny` has zero production callers.
- `grep -E 'agent_liveness_status|comment_added|approval_requested|merge_.*|rebase_.*|...' packages/server/src` confirms none of the 45 dead legacy names are emitted.
- `Web/src/app/providers/LiveTaskProvider.tsx:25-49` and `EventBridge.cs:83-104` were cross-checked field-by-field; the wire is stable.
