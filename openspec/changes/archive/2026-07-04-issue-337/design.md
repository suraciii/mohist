# Design — Task 执行过程日志（Phase 2）：执行中实时流式查看

## Context

Phase 1 (issue #336) shipped an ops task's execution log as a **single terminal
batch**: the runner buffers every captured line in a per-work `TaskLogCollector`,
flushes once right before `report()`, the server `delete-then-insert`s the whole
batch, and the Web fetches it (one `limit=5000` GET) only after the task reaches a
terminal state. A long-running task (big rebase, slow openspec) is a blind wait —
the user cannot see which step is stuck or whether it is already emitting errors
until it finishes.

Phase 2 adds a **second, best-effort real-time rail** alongside Phase 1's
authoritative terminal store: the runner flushes incrementally *during* execution
(攢批, never one-request-per-line), the server persists each batch *before*
fanning it out over a dedicated SignalR method, and the Web live-appends those
increments then reconciles to the authoritative store on terminal. The terminal
batch remains the authoritative reconciliation; the real-time rail is allowed to
drop, truncate, or arrive out of order — whatever it loses is backfilled from the
store. See `design/task-log.md` ("两期模式" + "工业参考 §5: SLA 分离") for the
contract this design concretizes.

**Current state (load-bearing facts, verified in code):**

- Runner collector (`runtime/task-log.ts:206`) — `seq` is work-scoped monotonic
  from 1 and **never reused** (even after head-drop truncation); `flush()`
  returns a **non-clearing snapshot** (no `drain()`/watermark primitive exists
  yet). `TASK_LOG_UPLOAD_TIMEOUT_MS = 250` (`host.ts:459`) is sized for one
  terminal batch.
- Runner host (`host.ts:338` `flushTaskLog`) — the **only** upload driver, called
  exactly twice (`:392`, `:416`), both immediately before `report()`. Best-effort
  (errors `console.error`'d, never block report). Upload goes to
  `POST /api/{workflow-runs|agent-jobs}/{ownerId}/work/{workId}/task-log` with body
  `{ entries:[{seq,timestamp,source,text}], truncated }` (`connection.ts:180`).
- Server store (`TaskLogStore.cs:34` `AppendAsync`) — **delete-then-insert**: on
  every call it removes all existing entry rows for the work item, upserts one
  `TaskLogBatchRow`, re-inserts. Correct for a single terminal batch;
  **destructive under incremental flushes**.
- Server unique index `IX_TaskLogEntries_Owner_WorkId_Seq` on
  `(OwnerKind, OwnerId, WorkId, Seq)` is `unique: true` (migration
  `20260703091225_AddTaskLogTables.cs:48`, DbContext `:622`). **This already
  supports incremental dedup-by-seq inserts — no EF migration is needed.**
- Server service (`TaskLogService.cs:34` `AppendAsync`) — the single facade the
  upload route calls; already validates the work item is `Outstanding` and already
  injects `WorkflowRunQuerier`. **This is the one place to add persist-then-publish.**
- Server real-time precedent — `ITranscriptEventPublisher` /
  `SignalRTranscriptEventPublisher` (`Infrastructure/Events/`) is the exact
  pattern to mirror: iterate connections, `ShouldNotify(type)`, per-send failure
  isolation, no domain-event-bus coupling.
- Hub (`Events/Hub/MohistHub.cs`) — `IEventsClient` has two channels today
  (`OnEvent` domain, `OnTranscriptEvent` non-domain). The class doc explicitly
  invites a third dedicated channel.
- Subscription registry (`UserNotificationDispatcher.cs:39`
  `ConnectionSubscriptionRegistry`) — flat `connectionId → HashSet<eventType>`
  plus a `connectionId → projectId` affinity map. `ShouldNotify` is **type-only**
  (no per-task scope). The projectId affinity map is the template for a scoped
  filter.
- Web panel (`widgets/issue-workflow/ui/TaskLogPanel.tsx`) — renders purely from
  the `useIssueWorkflowTaskLog` cache (queryKey
  `[issueNumber, taskId, projectId, workflowRunId, 'workflow-task-log', params]`).
  No live wiring, no cursor use, no invalidation on terminal.
- Web hub (`shared/api/events-hub.ts` `useEventsConnection`) — binds `OnEvent` +
  `OnTranscriptEvent`; adding a third client method is the established pattern.
  `LiveTaskProvider`'s transcript path (`app/providers/LiveTaskProvider.tsx:60`)
  is the live channel that must stay untouched.

**Constraints:** single-machine daemon assumption (SQLite); no real
network/process/git/DB/wall-clock in tests (per `design/testing.md`); runner uses
`vi.useFakeTimers`, server uses injectable `TimeProvider`; `report` /
`WorkResult` / `WorkflowRun` untouched; the agent-session transcript channel is
physically separate and must not be affected.

## Goals / Non-Goals

**Goals:**

- Runner flushes captured lines in **incremental batches during execution**
  (interval + count trigger, 攒批), tracking a sent-seq watermark so each batch
  carries only new lines; the terminal batch is retained as authoritative
  reconciliation.
- Server accepts incremental appends **non-destructively** (no delete), deduping
  by `seq` against the existing unique index, so a failed/timed-out increment is
  restored by the later terminal batch.
- Server persists every batch **before** any real-time fan-out, over a
  **dedicated** best-effort channel that is physically separate from the
  agent-session transcript channel.
- Web live-appends increments while the task runs and **reconciles to the
  authoritative store on terminal**, so dropped real-time lines are backfilled.
- Real-time fan-out is **on-demand**: no push when no client has the task
  expanded.

**Non-Goals:**

- No per-line pushing (攒批; second-level latency is acceptable).
- No real-time reliability/retry (best-effort; the store is authoritative).
- No merging task-log into the agent-session transcript channel.
- No change to `report` / `WorkResult` / `WorkflowRun`, the upload endpoint
  shape, or the issue-path GET contract.
- No log search/download/filter (Phase 3a) or agent-task milestone rows (Phase 3b).
- No strict real-time ordering guarantee (terminal query guarantees order).

## Decisions

### D1 — Runner flush trigger: interval timer + count threshold, watermark-based drain

Add a **sent-seq watermark** to `TaskLogCollector` and a new **`drain()`**
primitive: returns the entries whose `seq > watermark` (defensive copy) and
advances the watermark; returns `null`/empty when nothing is new. `flush()`
(s full snapshot) is **retained unchanged** for the terminal reconciliation batch
— it re-sends everything and relies on server dedup, so a failed increment is
always recovered.

The flush trigger fires on **either** (a) an elapsed interval since the last
flush, or (b) a reached count of *new* (un-drained) lines — checked on each
`append`. When it fires, the host calls `drain()` and uploads the result only if
non-empty. The interval is driven by `setInterval`/`setTimeout`, which is
controllable by `vi.useFakeTimers` (satisfying the injectable-clock requirement
without a custom timer abstraction). New constants (e.g.
`TASK_LOG_FLUSH_INTERVAL_MS`, `TASK_LOG_FLUSH_LINE_THRESHOLD`,
`TASK_LOG_INCREMENTAL_UPLOAD_TIMEOUT_MS`) live next to the existing
`TASK_LOG_UPLOAD_TIMEOUT_MS`; the incremental timeout is **separate from and
larger than** the 250 ms terminal timeout, since incremental batches are smaller
but the rail tolerates more slack.

`executeAndReport` starts the flush trigger alongside `executeWithLog` and stops
it before the terminal flush. The terminal `flushTaskLog` call site is unchanged
in position (still immediately before `report()`), only its meaning shifts from
"the only upload" to "final reconciliation".

**Why not check-on-append only (no timer):** a quiet period after some lines
accumulate would leave them un-flushed until the next line arrives; the spec's
"interval elapsed" trigger implies time-based firing independent of new appends.
`setInterval` does not violate the collector's single-producer doc — in
single-threaded JS a timer callback never runs concurrently with a synchronous
`append`; the only interleaving is logical (append between drain and upload
completion), which is safe because that append gets a higher `seq` and is picked
up by the next drain.

**Alternative considered:** a per-line push (rejected — spec explicitly forbids
one-request-per-line); a debounce keyed on `now` injected into `append` (works
but duplicates timer logic the host already owns).

### D2 — Server store: insert-or-ignore-by-seq replaces delete-then-insert

Replace the `AppendAsync` delete-then-insert with a single **idempotent
append-by-seq** path used by both incremental and terminal batches:

1. (transaction) query the set of `Seq` already present for
   `(OwnerKind, OwnerId, WorkId)`;
2. filter the incoming batch to entries whose `Seq` is absent;
3. `AddRange` only those; upsert the `TaskLogBatchRow` (`Truncated` +
   `UploadedAt`).

This is **dialect-agnostic** (pure EF, no SQLite/Postgres `ON CONFLICT`), runs in
the existing transaction, and is naturally idempotent: a terminal batch that
re-sends seqs already supplied by an increment inserts nothing for them; a
failed increment's seqs are absent and get inserted when the terminal batch
arrives. The unique index `IX_TaskLogEntries_Owner_WorkId_Seq` already enforces
the dedup invariant at the DB level as a backstop.

`QueryAsync` (the issue-path cursor read) is **unchanged** — it already orders by
`Seq` and returns `{ lines, nextCursor, truncated }`.

**Alternative considered:** dialect-specific `INSERT ... ON CONFLICT DO NOTHING`
(faster but couples to provider and EF translation quirks); catching
`DbUpdateException` on the unique violation (messy, partial-failure prone).

### D3 — Persist-then-publish in the service facade (best-effort isolation)

`TaskLogService.AppendAsync` becomes the single orchestration point:

```
store.AppendAsync(...)              // await — authoritative, must succeed
try { publisher.PublishAsync(...) } // best-effort — swallow + log
catch { log; }
return true
```

Persistence completes and is committed **before** fan-out is attempted, so a
publish throw (no subscribers, per-connection send error, network drop) can never
corrupt or block the authoritative log. This is the concrete form of the
"落库权威 + 实时分发 best-effort" invariant. The publisher is injected (DI) so
tests substitute a throwing/no-op publisher to demonstrate isolation.

**Alternative considered:** fire-and-forget publish (`_ = publisher...`) — harder
to assert the persist-before-publish ordering in tests; publish-then-persist —
directly violates the spec.

### D4 — Dedicated real-time channel: new publisher + hub method + envelope

Mirror the transcript publisher exactly, as a **sibling**, not a reuse:

- `ITaskLogDeltaPublisher` + `SignalRTaskLogDeltaPublisher` (registered
  `Singleton`), iterating `registry.ConnectionIds`, filtering, per-send
  try/catch — identical shape to `SignalRTranscriptEventPublisher`.
- `IEventsClient.OnTaskLogDelta(TaskLogDeltaEnvelope)` — a **third** hub client
  method, distinct from `OnEvent` and `OnTranscriptEvent`.
- `TaskLogDeltaEnvelope` — a **distinct record** carrying `{ ownerKind, ownerId,
  workId, taskId, entries:[{seq,timestamp,source,text}], truncated }`. Distinct
  type, distinct method, distinct subscription filter ⇒ physical channel
  separation (transcript traffic never lands on the task-log method and vice
  versa).

The envelope carries `taskId` (resolved server-side, see D5) so the Web can match
it against the key it natively holds.

**Alternative considered:** riding the generic `OnEvent(eventName, data)` channel
— couples task-log runtime data to the domain `EventBridge`/`CloudEventBus` (the
transcript publisher's doc explicitly forbids this for non-domain runtime data);
reusing `OnTranscriptEvent` — violates the channel-separation acceptance
criterion.

### D5 — Work/task-scoped on-demand subscription filter

Extend `ConnectionSubscriptionRegistry` with a **second filter dimension** keyed
by `(workflowRunId, taskId)`, mirroring the existing `projectId` affinity map:

- `ConcurrentDictionary<string, HashSet<(string workflowRunId, string taskId)>>
  _byConnectionTaskLog`;
- `SetTaskLogSubscriptions(connectionId, set)` /
  `SubscribeTaskLog` / `UnsubscribeTaskLog` / `ShouldNotifyTaskLog(connectionId,
  workflowRunId, taskId)`;
- new hub methods `SubscribeTaskLogAsync(workflowRunId, taskId)` /
  `UnsubscribeTaskLogAsync(workflowRunId, taskId)` on `MohistHub` (and durable
  counterparts on `IConnectionSubscriptionGrain` for reconnect replay, matching
  the existing subscription-set durability).

The `SignalRTaskLogDeltaPublisher` fan-out checks **both**: the connection's
type-subscription contains the task-log event type **and** the connection's
task-log scope contains the delta's `(workflowRunId, taskId)`. No match ⇒ skip
(no invalid push). The Web calls `SubscribeTaskLogAsync` on expand (task running)
and `UnsubscribeTaskLogAsync` on collapse/terminal.

**workId → taskId resolution:** the upload carries `workId`; the Web holds
`taskId`. The server resolves `workId → taskId` in the publish path and stamps it
on the envelope. `TaskLogService` already injects `WorkflowRunQuerier` (which has
the workflow-run task/work state); the resolution is one read per batch and can
be cached per `workId`. *Open detail to confirm at implementation:* whether
`RunnerWorkStore.FindAsync` (already called in `AppendAsync`) carries the task
reference directly, which would make the resolution free.

**Alternatives considered:** scope by `workId` only (the Web lacks `workId`
natively — it would need its own resolve on every expand); coarse per-`workflowRunId`
scope (would push logs for tasks the user hasn't expanded — violates the on-demand
criterion); encode scope into the event-type string (hacky, collides with the flat
type-subscription model).

### D6 — Web: live-append into the query cache, reconcile by invalidation on terminal

Wire `OnTaskLogDelta` as a new optional callback on `useEventsConnection` (4th
arg), bound via `connection.on('OnTaskLogDelta', ...)` — parallel to the existing
`OnTranscriptEvent` binding, leaving `LiveTaskProvider`'s transcript path
untouched.

`TaskLogPanel` owns its live lifecycle:

- While the task is **running and expanded**: call
  `SubscribeTaskLogAsync(workflowRunId, taskId)`; on each delta, **merge into the
  `useIssueWorkflowTaskLog` query cache** by `seq` (dedup — a delta may overlap
  cached lines), appending entries with `seq > maxCachedSeq` and updating
  `truncated`. Rendering continues to read from the single cache, so source
  label, timestamp, truncation indicator, and empty state are unchanged.
- On **collapse or terminal**: `UnsubscribeTaskLogAsync`.
- On **terminal**: `queryClient.invalidateQueries({ queryKey: [issueNumber,
  taskId] })` (prefix match on the existing key) ⇒ authoritative refetch ⇒ any
  dropped real-time lines are backfilled and the display converges to the store.

The terminal-reconcile trigger is **local to the panel** (it derives terminal
status from its parent `TaskItem` task state, which already re-renders on
workflow/stage events) rather than threaded through the global event router — the
router's handlers lack `issueNumber`/`taskId` in their payloads, and co-locating
the trigger with the panel keeps the reconcile logic with the data it owns.

**Alternatives considered:** a separate local `useState` overlay over the query
cache (two sources of truth, harder reconcile); global router invalidation on
stage/workflow terminal (payloads don't carry the needed keys; spreads task-log
concerns across the router).

## Risks / Trade-offs

- **[Server store change is a prerequisite for runner incremental flush]** → if
  the runner ships incremental flushes while the server still delete-then-inserts,
  earlier increments are wiped. Mitigation: both ship in one monorepo deploy; the
  insert-or-ignore store is **independently backward-compatible** with a
  terminal-only runner (a single batch with no prior rows inserts cleanly), so
  ordering within the deploy is safe and rollback to terminal-only needs no store
  change.
- **[Dialect-agnostic dedup query adds a read per append]** → for a 5000-line
  terminal batch the existing-seq query returns a set and the filter is O(n);
  acceptable under single-machine assumption. If it ever bottlenecks, switch to
  provider-specific `ON CONFLICT DO NOTHING` behind the same method.
- **[Real-time increments may arrive slightly out of order]** → the Web merges by
  `seq > maxCachedSeq`, so an out-of-order late delta with a lower seq is dropped
  from the live view; the terminal reconcile re-fetches the authoritative ordered
  log. Acceptable per spec (terminal guarantees order).
- **[workId → taskId resolution couples the publish path to workflow-run state]**
  → one read per batch, cacheable per workId; if the work item lookup already
  yields the task, it is free. Flagged as the detail to confirm at implementation.
- **[Collector single-producer doc vs. interval timer]** → safe in single-threaded
  JS (no true concurrency); document the new logical caller (`drain`) in the
  collector's doc-comment so the invariant is understood as logical ownership,
  not thread-safety.
- **[Subscription durability adds grain surface]** → mirroring the existing
  subscription-set replay on reconnect; scope set is small (only expanded tasks)
  and cleared on disconnect.

## Migration Plan

Single monorepo deploy; **no EF migration** (unique index already exists).

1. **Server store + service first (or same commit):** land D2 (insert-or-ignore)
   + D3 (persist-then-publish) + D4 (publisher/hub method/envelope) + D5
   (subscription scope). The store change is backward-compatible with the existing
   terminal-only runner, so this can land and be verified in isolation (terminal
   batch still inserts correctly; no real-time subscribers yet ⇒ fan-out is a
   no-op).
2. **Runner incremental flush (D1):** once the server accepts incremental
   appends, enable the interval/count flush trigger. The terminal batch remains,
   so a partial deploy (new runner, old-but-store-changed server) still converges.
3. **Web live-append (D6):** subscribe + merge + reconcile. Safe to land last; the
   hub method already exists from step 1.

**Rollback:** disabling the real-time rail (publisher no-op / Web not subscribing)
degrades cleanly to Phase 1 terminal-only behavior — the store (insert-or-ignore)
and the terminal batch are untouched, so the authoritative log is complete. No
data migration is involved in either direction.

## Open Questions

- Does `RunnerWorkStore.FindAsync` (already called in `TaskLogService.AppendAsync`)
  carry the workflow task id? If yes, the `workId → taskId` stamping in D5 is free;
  if no, confirm the cheapest resolve path via `WorkflowRunQuerier` and whether a
  per-workId cache is warranted.
- Exact flush-cadence constants (interval / line threshold / incremental upload
  timeout) — pick conservative defaults (e.g. ~1–2 s interval, ~200–500 line
  threshold) and tune against a real long-running task; they are constants, not
  config-surface, per the "data model简洁" principle.
- Whether the durable subscription grain needs the task-log scope replayed on
  reconnect, or whether the Web re-asserting `SubscribeTaskLogAsync` on reconnect
  (it already re-applies the type subscription set) is sufficient — prefer the
  latter (Web-driven) to avoid new grain state.
