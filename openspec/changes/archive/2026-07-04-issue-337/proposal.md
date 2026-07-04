## Why

Phase 1 (issue #336) made an ops task's execution log viewable — but only *after*
the task finishes, as one terminal batch. For a long-running task (a big rebase,
a slow openspec generation), the user waits blind: which step is it stuck on, is
it already emitting errors, how much longer? They cannot decide to abort early
until the task ends and the log lands. We need GitHub-Actions-style live
scrolling *during* execution — the log lines appear (near-)real-time as the task
runs — so a failing long task can be triaged mid-flight instead of after the
fact. This is the Phase 2 loop, an incremental extension of Phase 1's
capture/store/panel onto a second, best-effort real-time rail.

## What Changes

- Runner: replace the **terminal-only** flush (`runtime/host.ts` `flushTaskLog`)
  with **incremental batched flushes during execution** (攒批 — accept
  second-level latency, never one-request-per-line) plus a final **terminal
  reconciliation batch** on completion. Phase 1's collector
  (`runtime/task-log.ts`) gains a flush trigger (interval/count) and tracks
  already-sent `seq` so each batch carries only new lines; no such trigger
  exists today (only `TASK_LOG_UPLOAD_TIMEOUT_MS=250`).
- Server store: add a **non-destructive incremental append** path to
  `TaskLogStore`/`TaskLogService`. The current `AppendAsync`
  (`Infrastructure/Data/Runner/TaskLogStore.cs`) **deletes-then-inserts** for a
  work item — correct for a single terminal batch but destructive under
  incremental flushes. Incremental appends must respect the existing unique
  `(OwnerKind, OwnerId, WorkId, Seq)` index; the terminal batch remains the
  authoritative reconciliation (dedup by `seq`).
- Server real-time: add an **independent best-effort distribution publisher**
  mirroring `ITranscriptEventPublisher` / `SignalRTranscriptEventPublisher`, a
  new hub method on `MohistHub` / `IEventsClient` (e.g. `OnTaskLogDelta`), and
  **work/task-scoped subscription filtering** (on-demand: skip fan-out when no
  client has the task expanded). **Persist first, fan-out second**; fan-out
  failure (no subscribers, network drop) is logged and swallowed, never blocking
  persistence or task execution.
- Web: extend `TaskLogPanel` (`widgets/issue-workflow/ui/TaskLogPanel.tsx`) to
  subscribe to the new hub method and **live-append increments** while the task
  runs, then **reconcile to the authoritative store** (existing
  `useIssueWorkflowTaskLog` query, invalidated on terminal) so any lines the
  best-effort channel dropped are backfilled. Today the panel does one
  `limit=5000` fetch with no polling/cursor/increment path.
- The new real-time rail is **physically separate** from the agent-session
  transcript channel (different envelope type, different hub method, different
  subscription filter) — it reuses the *architecture pattern*, not the channel.
- No change to `report` / `WorkResult` / `WorkflowRun`; no change to Phase 1's
  upload endpoint shape or the issue-path GET query contract. The double-write
  invariant from `design/task-log.md` (落库权威 + 实时分发 best-effort) is
  preserved and made concrete.

No user-visible API is broken: changes are internal behavior (flush cadence,
store append semantics) and additive (new hub method, new event type). Nothing
is marked **BREAKING**.

## Capabilities

Three of these extend Phase 1's capability boundaries with streaming behavior;
`task-log-realtime` is new and covers the best-effort distribution rail.

- `ops-task-log-capture`: Runner-side — extend the per-work `TaskLogCollector`
  from terminal-only to incremental batched flush during execution (a flush
  trigger keyed on interval/count, tracking already-sent `seq`), with the
  terminal batch retained as final reconciliation. Keeps Phase 1's masking,
  monotonic-`seq`, head-drop truncation, no-loss `onLine`, and the unchanged
  `runCommand`/`git()` aggregate-return contract.
- `task-log-persistence`: Server-side authoritative store — a non-destructive
  incremental append that respects the unique `(OwnerKind, OwnerId, WorkId, Seq)`
  index, with terminal-batch reconciliation (dedup by `seq`). Persists every
  received batch *before* any real-time fan-out, so distribution failure cannot
  corrupt the authoritative log; Phase 1's issue-path GET cursor query and
  upload endpoint contract are unchanged.
- `task-log-realtime`: Server + Web independent best-effort distribution rail —
  the fan-out publisher, the new hub method, work/task-scoped on-demand
  subscription filtering (no subscribers ⇒ no push), failure isolation
  (publish errors logged/swallowed, never block persistence or execution), and
  strict channel separation from the agent-session transcript channel
  (different envelope, different method, different filter).
- `task-log-viewer`: Web display — live-append of hub increments during
  execution and authoritative reconciliation on terminal (re-query to backfill
  any dropped lines), layered onto the existing Phase 1 panel without changing
  its terminal-state rendering or truncation indicator.

## Impact

- **Runner (TypeScript)**: `runtime/task-log.ts` (collector: incremental flush,
  sent-`seq` watermark, flush trigger), `runtime/host.ts` `executeAndReport` /
  `flushTaskLog` (drive incremental flushes during the work lifecycle; terminal
  reconciliation batch before `report`), new flush-cadence constants. The
  injectable clock (`now`) convention is reused for any timer.
- **Server (C#)**: `Infrastructure/Data/Runner/TaskLogStore.cs` +
  `Runner/Services/TaskLogService.cs` (incremental append + terminal dedup), a
  new `TaskLogRealtimePublisher` mirroring
  `SignalRTranscriptEventPublisher`, a new method on `MohistHub` /
  `IEventsClient` and a work/task-scoped filter in (or alongside)
  `ConnectionSubscriptionRegistry`; `TaskLogRoutes.HandleUploadAsync` calls
  persist-then-publish. No EF migration expected (schema and unique index are
  already sufficient for incremental inserts).
- **Web (React)**: `TaskLogPanel.tsx` (subscribe + live-append + terminal
  reconcile), `shared/api/events-hub.ts` (register the new event type), TanStack
  Query invalidation on terminal. The agent-session hub wiring
  (`useEventsConnection` / `LiveTaskProvider`) is untouched.
- **APIs/Data**: one new SignalR event type/hub method; existing task-log POST
  and GET contracts unchanged; `report` / `WorkResult` / `WorkflowRun` untouched.
- **Tests**: best-effort failure isolation (persist succeeds when publish throws
  / has no subscribers), on-demand-push (no fan-out when unsubscribed), live +
  authoritative convergence on terminal, Phase 1 terminal-batch/query
  non-regression, and agent-session real-time channel non-interference. No real
  external dependencies or wall-clock (runner `vi.useFakeTimers`; server
  injectable `TimeProvider`).
