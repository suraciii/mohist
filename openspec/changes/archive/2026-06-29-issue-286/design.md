## Context

Today the only "needs attention" surface is the Dashboard Attention Hero
(`packages/web/src/widgets/attention-hero`), which derives live from the current
issue query. When the browser is closed or an issue moves on, the signal is
gone — there is no durable record that a workflow failed, an approval was
requested, or work started/completed while the operator was away.

The authoritative events already exist and are already published on the
in-process CloudEvents bus:

- `WorkflowRunFailed` → `com.mohist.workflow.run.failed`
- `StageApprovalRequested` → `com.mohist.workflow.stage.approval-requested`
- `IssueWorkStarted` → `com.mohist.issue.work-started`
- `IssueWorkCompleted` → `com.mohist.issue.work-completed`

See `packages/server/src/Mohist.Server/Infrastructure/Events/EventCatalog.cs`
and `WorkflowEventSerializer.cs` / `IssueEventSerializer.cs`. Existing bus
consumers (`EpicAutoDoneHandler`, `RunnerWorkflowTerminalStatusHandler`,
`WorkflowStageLockReleaseHandler`) already subscribe with `[Subscription(Type = ...)]`
and are auto-registered via `AddCloudEventHandlersFromAssembly`. The inbox adds
another such consumer; it changes nothing about how events are produced.

Two facts about the current event plumbing shape this design:

1. **Issue events carry identity extensions; workflow events do not.**
   `IssueGrain.PublishIssueEventsAsync`
   (`packages/server/src/Mohist.Server/Issue/Grains/IssueGrain.cs:598`) stamps
   every CloudEvent with `projectid`, `issueid`, `issueno` extensions and sets
   `subject` to the issue number. `WorkflowRunStore.ToCloudEvent`
   (`packages/server/src/Mohist.Server/Infrastructure/Data/Workflow/WorkflowRunStore.cs:80`)
   emits **no extensions** and a null subject; only the `workflowRunId` is
   recoverable, from the `/mohist/workflow-runs/{id}` source URI.

2. **Project/issue identity for a workflow run lives in `WorkflowRun.Metadata.Annotations`.**
   `IssueGrain` sets `projectId`, `issueId`, `issueNumber` annotations at run
   start (`IssueGrain.cs:205`), and `WorkflowGrain.GetProjectId()/GetIssueId()/GetIssueNumber()`
   read them back. The inbox projection reuses the same source of truth.

3. **Neither event payload carries the issue title.** `IssueWorkStarted`/`IssueWorkCompleted`
   carry only `WorkflowRunId`; the workflow event payloads carry only failure
   message / stage. The projection must load the issue to obtain a title for
   deep-linking.

Constraints (from `design/architecture.md`):

- The inbox is a **Control Plane read surface**. Runner reports facts; only the
  server-side projection decides an event becomes an inbox item. The runner and
  Web client have no delivery logic.
- Existing live SignalR/dashboard subscriptions stay transport/presentation
  state; they are not the source of inbox truth.
- No changes to workflow execution, issue lifecycle, or runner behavior. The
  projection emits no new events.

## Goals / Non-Goals

**Goals:**

- Durable, project-scoped inbox items produced by a server-side projection over
  the four MVP events, idempotent by source event.
- Strict project isolation: items from one project never appear in another.
- Project inbox HTTP API (list / mark-one-read / mark-all-read / archive) scoped
  to one project.
- Web inbox page with list, explicit empty state, read/unread presentation, and
  a deep-link to the issue.
- Replay-safe projection so a future event-store replay or backfill does not
  duplicate items.

**Non-Goals:**

- No multi-user accounts, assignees, mentions, or team semantics; the recipient
  is the implicit local operator.
- No global inbox; no per-project notification preferences (all four kinds on).
- No email/push/desktop delivery.
- No changes to workflow state transitions, issue lifecycle, runner, existing
  API contracts, or the live dashboard/SignalR transport.
- No full historical backfill in this change (the dedup key makes a later
  backfill safe, but writing one is out of scope).

## Decisions

### D1. Projection = CloudEvents bus subscription handler, not a grain

Place the projection as an `ICloudEventHandler` in
`packages/server/src/Mohist.Server/Events/Subscriptions/` — the same pattern as
`EpicAutoDoneHandler` and `RunnerWorkflowTerminalStatusHandler`. It subscribes
to the four types (pipe-separated) and writes one `InboxItem` row per event.

**Rationale:** matches the established side-effect wiring (lock release, runner
status, epic reconcile all flow through bus subscriptions, not grain-internal
`On()` branches). Keeps `WorkflowGrain`/`IssueGrain` free of notification
logic. Auto-registered by the existing `AddCloudEventHandlersFromAssembly`
scan — no new hosting code.

**Alternatives considered:**

- _Orleans grain subscribing to a stream._ Rejected: there is no Orleans stream
  infrastructure today; the in-process bus is the established event path. Adding
  streams for one consumer would diverge from the existing, simpler pattern.
- _Reentrant `WorkflowGrain.On()` branch._ Rejected: `architecture.md` keeps
  notification logic out of the workflow decision grain; the existing migration
  explicitly moved side effects out of `On()` to bus subscriptions.

### D2. Inbox read model = EF Core table, not Orleans grain state

Add an `InboxItemRow` to `MohistDbContext` plus a migration. Columns:

| Column | Purpose |
|--------|---------|
| `Id` (PK, guid) | Item identity for API mutations |
| `ProjectId` | Owning project — isolation key |
| `IssueId`, `IssueNumber` | Deep-link identity |
| `IssueTitle` | **Snapshot** at projection time |
| `NotificationKind` | One of the four MVP strings |
| `SourceEventId` | CloudEvent `Id` — **idempotency key** |
| `CreatedAt` | Projection time, list ordering |
| `ReadAt` (nullable) | Read state |
| `ArchivedAt` (nullable) | Archive/dismiss state |

Indexes: `(ProjectId, CreatedAt DESC)` for the list query; `UNIQUE(SourceEventId)`
for dedup; lookup by `(ProjectId, Id)` for project-scoped mutations.

`InboxStore` (writes), `InboxQuerier` (reads) in a new
`packages/server/src/Mohist.Server/Inbox/` module, following the `*Store` /
`*Querier` role names in `design/conventions.md`.

**Rationale:** list, mark-all-read, and archive are naturally relational
operations. A per-project grain would have to load every item into actor state
for each list call and would fight the EF-based read-model pattern used for
Epic/Issue lists. The codebase already centralizes all persistence in
`MohistDbContext` with manual migrations.

**Alternatives considered:**

- _One project-scoped Orleans grain keyed by `projectId`._ Rejected for the
  relational-query reasons above; also would introduce a second persistence
  substrate (Orleans storage) for a read model that is read-heavy and
  set-oriented.
- _Derive the inbox on demand from the event store._ Rejected: the spec
  requires durable retrieval independent of browser/connection and product-facing
  item text; a derived view would re-resolve issue titles on every read and
  recompute idempotency each time. A materialized read model is simpler and
  matches how `EpicRow`/`EpicActiveIssueRow` are already materialized.

### D3. Idempotency key = CloudEvent `Id` with a UNIQUE constraint

Each published CloudEvent carries a unique `Id` (a GUID assigned at publish in
`IssueGrain.PublishIssueEventsAsync` / `WorkflowRunStore.ToCloudEvent`). The
projection stores it as `SourceEventId` with a `UNIQUE` index. Insert that
conflicts on `SourceEventId` is treated as "already projected" and skipped
(SQLite `INSERT OR IGNORE`-equivalent via EF conflict handling).

**Rationale:** the spec's idempotency scenarios are "the same source event is
replayed or retried." The CloudEvent envelope is the unit of redelivery, so its
`Id` is the correct dedup key. Two genuinely distinct failures of the same run
produce two envelopes with two ids → two items, which is the intended behavior.

**Alternatives considered:**

- _Composite key `(Source, Type)`._ Rejected: an issue can legitimately start
  work more than once across re-runs, each a distinct `IssueWorkStarted` from
  the same source. `(Source, Type)` would wrongly collapse them.
- _Dedup table consulted before insert._ Rejected: redundant given a UNIQUE
  index on the row itself.

### D4. Identity resolution differs by event source

The handler branches on `evt.Type`:

- **Issue events** (`issue.work-started`, `issue.work-completed`): read
  `projectid`, `issueid`, `issueno` directly from `evt.Extensions` (already
  stamped by `IssueGrain`). Load the issue via `IssueStore.LoadAsync` only to
  snapshot the title. If extensions are missing, log and skip (same defensive
  pattern as `EpicReconcileDispatcher`).
- **Workflow events** (`workflow.run.failed`, `workflow.stage.approval-requested`):
  extract `workflowRunId` from the source URI (the `ExtractWorkflowRunId`
  helper already exists in `WorkflowStageLockReleaseHandler` /
  `WorkflowEventSerializer.ExtractContextFromSource`), then load the run via
  `WorkflowRunStore.LoadAsync` to read `Metadata.Annotations["projectId"] /
  ["issueId"] / ["issueNumber"]`, then load the issue for the title.

**Rationale:** reuses the exact identity metadata the rest of the system
already trusts (`WorkflowGrain.GetProjectId()` reads the same annotations). No
need to enrich workflow CloudEvents with extensions in this change — that would
be a broader, cross-cutting migration.

**Alternatives considered:**

- _Stamp `projectid`/`issueid` extensions onto workflow CloudEvents in
  `WorkflowRunStore.ToCloudEvent`._ Cleaner long-term and would let the handler
  skip the run lookup, but it touches the workflow event-publish path (a core
  path) and would need the run's metadata available at publish. Filed as an
  **open question** (Q1); the current decision avoids widening this change.

### D5. Projection reads from stores, not grains — runs inline on the bus

For workflow events the handler calls `WorkflowRunStore.LoadAsync` (a plain EF
read), **not** `IWorkflowGrain`. This avoids re-entering the workflow grain
from inside `WorkflowRunStore.SaveAsync`'s publish call stack — the same
self-deadlock risk called out in `RunnerWorkflowTerminalStatusHandler`'s
"detached" rationale. The projection then performs its own EF write to the
inbox table.

The handler runs **inline** on the bus dispatch (not fire-and-forget): it only
touches the DB, and the bus already swallows handler exceptions, so a failure
cannot break workflow/issue execution. Inline preserves causal ordering
(an inbox item appears after the event that produced it committed).

**Rationale:** grain re-entrancy from a publish call stack is a known hazard in
this codebase; store reads sidestep it. Inline is simpler than detaching and
keeps ordering.

**Trade-off:** a slow inbox DB write blocks the publish briefly. Acceptable on
a local-first single machine; the bus already tolerates handler slowness.

### D6. Snapshot issue title at projection time

`IssueTitle` is captured once, when the item is created. A later title edit
does not update existing inbox items.

**Rationale:** an inbox item is a historical fact about what happened — the
message that "Issue #42 needs approval" should stay legible even if the issue
is later renamed. Keeps list reads to a single table (no join to current issue
state). Matches `AGENTS.md`'s "model as simple as possible."

**Alternatives considered:**

- _Resolve title live on each list read._ Rejected: extra join per item, and a
  title change would retroactively rewrite history.

### D7. Product-facing text rendered on the client, not stored

Store the structured fields (`NotificationKind`, `IssueNumber`, `IssueTitle`).
Render the human string ("Issue #42 needs approval", "Issue #42 workflow
failed", "Issue #42 started", "Issue #42 completed") in the Web page from
`kind` → template.

**Rationale:** the spec's example strings are a presentation concern; storing
both a text column and its structured inputs duplicates data and forces a
schema change to reword text. Keeps the row minimal.

**Alternatives considered:**

- _Store a pre-rendered `Text` column._ Rejected as above; also makes i18n or
  rewording a data migration.

### D8. API: project-scoped REST routes under the existing filter

```
GET   /api/projects/{projectRef}/inbox                  → list (most-recent-first, excludes archived)
POST  /api/projects/{projectRef}/inbox/{itemId}/read    → mark one read
POST  /api/projects/{projectRef}/inbox/read-all         → mark all project items read
POST  /api/projects/{projectRef}/inbox/{itemId}/archive → archive/dismiss one
```

All routes sit under `MapGroup("/api/projects/{projectRef}/inbox")` with
`AddEndpointFilter<ProjectResolutionEndpointFilter>()`, identical to
`IssueRoutes`. Project isolation is enforced by resolving `projectRef` →
`ProjectInfo` at the filter, then every `InboxQuerier`/`InboxStore` call takes
the resolved `projectId` and filters `WHERE ProjectId = @pid`. A mutation
targeting an item whose `ProjectId` ≠ the resolved project is rejected as 404
(not exposed) — directly satisfying the "cannot cross project boundaries"
scenario.

**Rationale:** reuses the established project-resolution + 404 behavior; no new
authz concept. `projectRef` (name or id) keeps the inbox URL consistent with
issues/epics.

### D9. Web: new entity + page + route; SignalR only invalidates

- `packages/web/src/entities/inbox/` — `client.ts` (typed fetch helpers via
  `request`/`projectApiPath`), `queries.ts` (`useInbox`, mutations:
  `useMarkInboxItemRead`, `useMarkAllInboxRead`, `useArchiveInboxItem`).
- `packages/web/src/pages/inbox/InboxPage.tsx` — list, empty state, read/unread
  styling, deep-link to `/:projectName/issues/:number` via `useProjectPath`.
- Route `/:projectName/inbox` added under the existing `ProjectRouteScope` in
  `packages/web/src/app/App.tsx`.
- Live refresh: the existing `EventBridge` already fans `com.mohist.*` events to
  SignalR clients. The inbox page subscribes and, on any relevant event type,
  **invalidates** the TanStack `inbox` query — it never synthesizes items.
  This satisfies "page drives inbox state only through the API" + "live
  subscriptions are not the source of truth."

**Rationale:** mirrors the structure of `entities/issue` and the other pages.
Invalidation-only keeps the durable API as the single source of truth while
still feeling live.

## Risks / Trade-offs

- **[Missed inbox item if the projection throws]** The in-process bus swallows
  handler exceptions (see `WorkflowRunStore.SaveAsync`'s publish try/catch and
  `InMemoryEventBus`), so a failed projection silently drops an item. →
  *Mitigation:* the `UNIQUE(SourceEventId)` dedup key makes a future event-store
  replay / backfill safe and duplicate-free; handlers log warnings on failure.
  No correctness impact on workflow/issue execution (events for observation,
  not control).
- **[Stale issue title in old items]** D6 snapshots the title. → *Mitigation:*
  acceptable for a historical message; the deep-link still opens the live issue,
  which shows the current title.
- **[Projection blocks publish on slow DB write]** D5 runs inline. →
  *Mitigation:* local-first single machine; the bus tolerates slow handlers. If
  this ever bites, detach to a background task (the pattern in
  `RunnerWorkflowTerminalStatusHandler`) without changing the contract.
- **[Workflow-event identity depends on run metadata being present]** Legacy or
  manually-started runs without `projectId`/`issueId` annotations cannot be
  scoped to a project. → *Mitigation:* the handler logs and skips
  un-scoped events (same defensive skip as `EpicReconcileDispatcher` on missing
  extensions). Only runs started by `IssueGrain` (the normal path) carry the
  annotations.
- **[One row per event can grow unbounded]** No retention in the MVP. →
  *Mitigation:* archive removes items from the default list; a future retention
  sweep is a non-goal here but the schema supports it via `ArchivedAt`/`CreatedAt`.

## Migration Plan

1. Add `InboxItemRow` + indexes to `MohistDbContext` and a new EF migration
   (manual, consistent with the 73 existing migrations in
   `Infrastructure/Data/Migrations/`). `dotnet build` (which runs
   `TreatWarningsAsErrors`) + `npm test` must pass.
2. Add the projection handler, `InboxStore`/`InboxQuerier`, and the API routes;
   register routes in `Program.cs` alongside the other `Map*Routes` calls.
3. Add the Web entity, page, and route.
4. Deploy: the migration runs on server start (schema-only additive; no data
   backfill). Existing events emitted **before** deployment simply have no
   inbox items — expected, since the projection did not exist yet.
5. **Rollback:** drop the new routes/Web page (pure UI) and/or revert the
   migration (drops an isolated table that no other code reads). No workflow or
   issue state depends on inbox rows, so rollback is safe and side-effect-free.

No data migration of existing events is performed in this change (Non-Goals).

## Open Questions

- **Q1. Enrich workflow CloudEvents with `projectid`/`issueid` extensions?**
  D4 resolves identity by loading the run. Long-term it may be cleaner to stamp
  extensions onto workflow events in `WorkflowRunStore.ToCloudEvent` (mirroring
  `IssueGrain`), which would let the handler skip the run lookup and simplify
  other future consumers. That touches the workflow publish path and is
  deferred; decide before adding more workflow-event consumers.
- **Q2. Notification for re-runs / repeated transitions.** An issue that fails,
  restarts, and fails again produces one `workflow_failed` item per distinct
  `WorkflowRunFailed` event (distinct CloudEvent ids). Confirm this is the
  desired UX versus, say, collapsing consecutive same-kind items per issue. The
  spec's idempotency wording ("at most one item per source event") implies
  per-event is correct; left open for product confirmation.
