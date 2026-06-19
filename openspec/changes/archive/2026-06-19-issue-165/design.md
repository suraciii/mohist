## Context

The Issue context exposes only per-issue reads today (`IssueQuerier.ListAsync` / `ListWithLabelFiltersAsync`). There is no derived completion view, and the only timestamp on an issue — `UpdatedAt` — is imprecise for completion: `Issue.Touch()` (`packages/server/src/Mohist.Server/Issue/Domain/Issue.cs:124`) fires on *every* mutation, including the post-completion `Archive()` (`Issue.Transitions.cs:161`) which also clears `ActiveWorkflowRunId`. So an issue completed last week but archived/edited this week is misattributed to this week if `updatedAt` is used.

The change adds two faces of one concern (see `proposal.md`, spec `issue-completion-metrics`):
- a **client snapshot** — a pure, replaceable derivation over already-loaded issues, accepting the `updatedAt` approximation;
- a **server time-series aggregation endpoint** — bucketed by the *correct* completion time, not `updatedAt`.

The plan-stage open question flagged in the issue was: *does issue/workflow persistence record a precise completion time, and if not, must we backtrack from workflow run completion events?* The investigation below answers it.

### Completion-time source investigation (resolves the plan-stage open question)

The Issue aggregate records **no dedicated `CompletedAt`** (`Issue.cs` has only `CreatedAt` / `UpdatedAt` / `ArchivedAt`). Terminal transitions are:
- `Complete()` → status `Done`, records `IssueWorkCompleted(workflowRunId)` (`Issue.Transitions.cs:149`);
- `Close()` → status `Cancelled`, clears the run id, records `IssueClosed(reason)` (`Issue.Transitions.cs:180`).

These domain events are in-memory only, **but the grain projects every transition to the durable `IssueEvents` table** as a CloudEvents 1.0.2 envelope (`packages/server/src/Mohist.Server/Issue/Grains/IssueGrain.cs:485` `PublishIssueEventsAsync`). Each row carries:
- `Type` = the reverse-DNS bus type — `com.mohist.issue.work-completed` (→ completed) and `com.mohist.issue.closed` (→ failed) (`IssueEventSerializer.cs:32-33`);
- `Time` = when the transition actually occurred (`EventStore.cs:38`);
- `Source` = `/mohist/issues/{issueId}` (`IssueEventPersistence.IssueSource`);
- `ExtensionsJson` containing `["projectid"]` (`IssueGrain.cs:492`).

**Conclusion**: a precise, durable, timestamped completion source already exists — no schema migration or domain change is required. The endpoint will bucket from `IssueEvents`, not from workflow-run events (the issue's suggested fallback) and not from `updatedAt`.

## Goals / Non-Goals

**Goals:**
- Snapshot: derive `{ completed, failed, new }` for the trailing 7-day window from loaded issues, as a standalone pure function with a stable swap contract.
- Endpoint: return by-day / by-week completion buckets bucketed by the *actual* terminal-transition time, project-scoped.
- Keep the change purely additive (no migration, no breaking API/DTO change).

**Non-Goals** (from `proposal.md`):
- Productivity view UI (issue G), usage/cost metrics (issue D), configurable bucket size / custom time range, prediction / regression, any `AgentActivity.summary` sourcing.

## Decisions

### D1. Completion-time source = `IssueEvents` table (not `updatedAt`, not workflow-run events)

Bucket completions from the durable `IssueEvents` rows where `Type ∈ { com.mohist.issue.work-completed, com.mohist.issue.closed }`, keyed by `Time`. `work-completed` → `completed` (Done); `closed` → `failed` (Cancelled).

**Rationale:**
- `Time` is the real transition instant; it is untouched by later edits/archive, so an issue edited after completion stays in its completion period (spec scenario "Issue edited after completion is attributed to its completion period").
- It is issue-scoped, so it captures `Close()` without a workflow run (e.g. closing a stale backlog issue) that workflow-run completion events would miss.
- It survives `ActiveWorkflowRunId` being cleared on `Archive()`/`Close()`, which would break an issue→run join.

**Alternatives considered:**
- *Denormalize a `CompletedAt` column onto `IssueRow`*: yields the cleanest single-table query, but costs a migration + backfill + `Issue` aggregate/persistence-mapping change and touches the hot write path. Rejected for v1 — the durable event log already holds the fact.
- *Workflow run `CompletedAt`*: rejected on two grounds. (1) `WorkflowRunRow` stores only an opaque JSON `State` blob with no indexed `IssueId`/`CompletedAt` columns, forcing a full-table scan + deserialize to aggregate; (2) `Issue.ActiveWorkflowRunId` is nulled on `Close()`/`Archive()`, so the issue→run link is lost for exactly the terminal issues we need to count.
- *`updatedAt`*: forbidden by the spec (imprecise).

**Aggregation semantics:**
- Count **distinct issues per bucket** that had a terminal-transition event in that bucket (an issue closed → reopened → closed in *different* buckets appears in each; two terminal events for the same issue in the *same* bucket count once). This prevents a flapping issue from inflating one period.
- `new` (created) is **not** part of the endpoint — it lives in the snapshot only; the time series is the *completion* trend.

### D2. Query path = a new aggregation method on `IssueQuerier`, querying `db.IssueEvents` directly

Add `IssueQuerier.GetCompletionBucketsAsync(projectId, bucket, now)` that:
1. opens a `MohistDbContext` (the class already injects `IDbContextFactory<MohistDbContext>` and joins across tables this way — `IssueQuerier.cs:93`);
2. resolves the project's issue ids (`db.Issues.Where(ProjectId == projectId)` → set of issue ids) to constrain the event `Source`s to this project, since `IssueEvents` has no indexed `projectId` column;
3. selects `IssueEvents` rows with `Type ∈ {work-completed, closed}` and `Time >= windowStart`, filtered to project sources, then groups by the bucket boundary (UTC day / ISO week);
4. returns the time series.

This keeps `EventStore` focused on append/per-aggregate reads and matches the existing direct-DbContext style of `IssueQuerier`.

**v1 fixed window (spec forbids custom range):** by-day → trailing 30 days; by-week → trailing 12 weeks. These lengths are the v1 contract.

### D3. Endpoint = `GET /api/projects/{projectRef}/issues/metrics/completion?bucket=day|week`

New partial `packages/server/src/Mohist.Server/Api/IssueRoutes.Metrics.cs` with `MapIssueMetrics(this RouteGroupBuilder group)`, mirroring the existing `IssueRoutes.Feedback.cs` partial-class pattern. The literal `metrics` segment precedes `{number:int}` routing, so the int route constraint cannot collide.

Response shape:
```json
{
  "bucket": "day",
  "window": { "from": "...", "to": "..." },
  "buckets": [
    { "boundary": "2026-06-12", "completed": 4, "failed": 1 },
    { "boundary": "2026-06-13", "completed": 2, "failed": 0 }
  ]
}
```
Read-only, project-scoped via the existing project-ref resolution (`GetRequiredProject`). The path and the bucketing semantics are recorded here so a reviewer can verify the aggregation `口径` (acceptance criterion 4).

### D4. Client snapshot = pure function + thin hook reservation

New `packages/web/src/entities/issue/lib/completion-snapshot.ts`:
- `deriveCompletionSnapshot(issues: Issue[], now = Date.now()): { completed, failed, new }` — pure, no fetch. Window = `[now - 7d, now]`.
  - `completed` = `status === 'done'` && `updatedAt ∈ window`; `failed` = `status === 'cancelled'` && `updatedAt ∈ window`; `new` = `createdAt ∈ window`.
- A thin `useCompletionSnapshot()` hook wraps `useIssues()` + `deriveCompletionSnapshot` today. **Reservation contract:** when the endpoint is ready, the hook body swaps to a query against D3's endpoint while the pure function and the `{completed,failed,new}` return shape stay unchanged — so consumers do not change (spec "Return shape is preserved for backend swap"). The pure function always satisfies "does not perform its own data fetch".

### D5. Status mapping

`completed ← done`, `failed ← cancelled` (`IssueStatus.Done` / `IssueStatus.Cancelled`, web `IssueStatus.Done` / `IssueStatus.Cancelled`). Non-terminal (`backlog`, `in_progress`) never counts as completed/failed.

## Risks / Trade-offs

- **[Aggregation misattributed by `updatedAt` drift]** → Mitigation: bucket by `IssueEvents.Time`, not `updatedAt`; covered by a unit test where an issue completed in week 1 but `updatedAt` in week 2 lands in the week-1 bucket.
- **[No indexed `projectId` on `IssueEvents` → slow project scoping at scale]** → Mitigation: constrain by the bounded `Time` window first, then by the project issue-id set. Open question D-OQ2 considers adding a `(Type, Time)` index if profiling demands it.
- **[Flapping issue double-counted]** → Mitigation: count distinct issue id per bucket (D1).
- **[Close-without-run completions missed by a workflow-run source]** → Mitigation: the issue-level `IssueClosed` event is the source, so non-workflow closures are included.
- **[Events absent for issues completed before the `IssueEvents` table existed (migration `20260610021455_AddIssueEvents`)]** → Mitigation: trailing window only; older periods simply render empty buckets. Acceptable for a recent-trend view; noted as a limitation.
- **Snapshot `updatedAt` approximation** → Accepted v1 limitation for the snapshot only (the endpoint is precise); documented in the spec.

## Migration Plan

- **Deploy:** purely additive — new read endpoint + new client function. No schema migration, no data backfill, no DTO/contract change to existing endpoints.
- **Rollback:** remove the `IssueRoutes.Metrics.cs` route registration and the `lib/completion-snapshot.ts` file. No state to restore.
- **Backend-ready swap:** when the endpoint is trusted, replace the body of `useCompletionSnapshot()` (D4) with the endpoint query; the pure function and return shape are unchanged, so no consumer changes.

## Open Questions

- **D-OQ1.** Confirm `IssueEvents` rows are populated for *every* `Complete()`/`Close()` path, including programmatic closures and recoveries (not just the happy-path workflow completion). Spot-check during implementation; if any path skips `PublishIssueEventsAsync`, the endpoint undercounts silently.
- **D-OQ2.** Whether to add a DB index on `IssueEvents(Type, Time)` for aggregation latency. Decide after profiling the by-week query on a populated project.
- **D-OQ3.** Whether archived issues (Done + Archived) should be excludable from the endpoint. Current design includes them (a completion is a completion regardless of later archive); confirm with the Productivity view (issue G) when it lands.
