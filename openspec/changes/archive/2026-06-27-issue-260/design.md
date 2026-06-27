## Context

Mohist exposes a single "is the human a bottleneck" signal today: nothing. The `Attention Hero` widget (`packages/web/src/widgets/attention-hero/ui/AttentionHero.tsx`) already aggregates *what is waiting* (attention items derived from `useIssues` + `useAgentStatus`), but never *how long approvals sit*. Issue #260 adds that signal — the average approval-gate wait surfaced where the user is about to act. See `proposal.md` for motivation and `specs/` for the requirements.

The relevant state of the world (verified against the current tree):

- **#258 (Attention Hero first-screen refactor) is merged.** The Hero is already a full-width slot above the dashboard zones grid (`packages/web/src/pages/dashboard/ui/DashboardPage.tsx:66-71`). The display landing this issue depends on **exists today** — no sequencing blocker.
- **`dashboard-attention` spec currently forbids new backend endpoints** for the Hero (`openspec/specs/dashboard-attention/spec.md`, "derives content exclusively from existing read-only sources"). The proposal flags relaxing this as a spec-level BREAKING change for *one* summary value.
- **Approval timestamps are not a table.** `StageApproval.RequestedAt` (non-null `DateTime`) / `RespondedAt` (nullable `DateTime?`) (`packages/server/src/Mohist.Server/Issue/Services/StageApproval.cs:7-8`) are produced by deserializing the `WorkflowRuns.State` JSON column into a `WorkflowRun`, running `WorkflowStatusMapper.BuildStatusView`, and applying the profile projection (`MohistDefaultWorkflowProjection` picks the *last* stage carrying an `ApprovalStatus`). See `IssueQuerier.LoadWorkflowStatesAsync` + `ApplyWorkflowProjections` (`packages/server/src/Mohist.Server/Issue/Services/IssueQuerier.cs:495-571`). `IssueEvents` carries only terminal events (`work-completed`, `closed`) — no approval transition is persisted there.
- **The existing metrics precedent** is `GET /api/projects/{projectRef}/issues/metrics/completion` (`packages/server/src/Mohist.Server/Api/IssueRoutes.Metrics.cs`) backed by `IssueQuerier.GetCompletionBucketsAsync` (`IssueQuerier.cs:215-336`): resolve the project's issue set via the indexed `Issues.ProjectId`, compute window boundaries from a passed-in `now`, load candidates, and **aggregate in memory** because EF Core SQLite cannot translate `DateTimeOffset` comparisons against TEXT columns. Frontend twin: `useCompletionTrend` (`packages/web/src/entities/issue/api/completion-trend.ts`) — `staleTime: 60_000`, query key `['issues','metrics','completion','week',projectId]`.

Constraints carried from `AGENTS.md`: data model stays minimal; tests use fakes/in-memory and run fast.

## Goals / Non-Goals

**Goals:**

- Ship one project-scoped, read-only aggregation endpoint returning avg / median / max approval-gate wait over a trailing 7-day window, plus a `SampleCount` discriminator so "no data" is distinguishable from "instant".
- Surface the aggregate **average** as a single summary line on the Attention Hero, with a defined empty/zero-sample presentation.
- Reuse the existing approval projection (`MohistDefaultWorkflowProjection`) so there is exactly **one** definition of "the approval for an issue" in the codebase — the metric and the Hero must not disagree on which stage counts.
- Introduce no new event, state collection, or write path.

**Non-Goals (inherited from proposal):**

- No breakdown of other stage durations.
- No approval reminder / notification mechanism.
- No per-stage or per-issue drill-down from the Hero number.
- No parameterization of the window (v1 is fixed trailing 7d, mirroring completion's fixed day/week).
- No new persistence column / migration. The endpoint is derived data.

## Decisions

### D1. Aggregate over the existing read-model projection, not a new parser

**Decision.** The new `IssueQuerier.GetApprovalWaitAsync(projectId, now)` reuses the *same* path that builds `IssueReadModel.StageApproval`: resolve the project's `WorkflowRunId` set from `Issues` (indexed), load the matching `WorkflowRuns` rows, deserialize `State` → `WorkflowRun` → `WorkflowStatusView`, and run `MohistDefaultWorkflowProjection.ProjectWorkflowState` to obtain each `StageApproval`. Aggregation then filters on `StageApproval.Status` ∈ {`approved`,`rejected`} with non-null `RespondedAt` inside `[now-7d, now]`.

**Rationale.** "Which stage is *the* approval for an issue" is the projection's job (it picks the last stage with an `ApprovalStatus`). A second, thinner parser reading `stages[].approvalStatus` directly from `WorkflowStatusView` would silently duplicate that selection and drift.

**Alternatives considered.**
- *Direct JSON parse of `WorkflowRuns.State`.* Skips the profile indirection but re-implements stage selection and the stringified `approved`/`rejected` mapping. Rejected — two sources of truth.
- *Persist approval transitions as `IssueEvents` rows.* Cleaner aggregations later, but violates the proposal's "introduces no new data collection" Non-Goal and adds a write-side change out of scope for this issue. Rejected for #260; noted as a future option in Open Questions.

### D2. In-memory aggregation, mirroring the completion precedent

**Decision.** Load the candidate set (project's workflow-run states) and compute avg / median / max in .NET. Window predicate applied in memory against the deserialized `RespondedAt`.

**Rationale.** Matches `GetCompletionBucketsAsync` verbatim (`IssueQuerier.cs:278-294` documents the SQLite/TEXT/DateTimeOffset limitation). The candidate set is bounded by the project's issue count and small at v1 volumes; the same in-memory posture keeps the codebase consistent and avoids EF-provider-specific SQL.

**Alternatives considered.**
- *Raw SQL `json_extract` over `WorkflowRuns.State`.* Feasible (the computed `MetadataProjectId` proves the path) but non-stored / non-indexed, provider-specific, and would still need the profile's stage-selection logic duplicated in SQL. Rejected.
- *Server-side `Average()` / LINQ translation.* Same TEXT-column blocker; EF can't translate the median anyway. Rejected.

### D3. Clock seam = passed-in `now` (not `TimeProvider`)

**Decision.** `GetApprovalWaitAsync(string projectId, DateTimeOffset now)`; the endpoint supplies `DateTimeOffset.UtcNow`. Tests pass a literal `now` (exactly the `GetCompletionBucketsAsync_*` test pattern at `IssueQuerierSpecs.cs:774-994`).

**Rationale.** Parity with the sibling metrics method. `TimeProvider.System` is registered (`MohistServiceRegistration.cs:77`) and used elsewhere, but the existing metrics path deliberately uses a `now` parameter — diverging here would create two patterns for no gain.

### D4. Window keyed on `RespondedAt`, trailing 7d inclusive

**Decision.** Sample = `respondedAt - requestedAt` for each completed approval whose `RespondedAt ∈ [now - 7d, now]`. Boundary math mirrors the completion path: `windowFrom = now - 7d`, `windowTo = now`, both UTC.

**Rationale.** Spec mandate (`specs/approval-waiting-metrics/spec.md`, "trailing 7-day window keyed on respondedAt"): the metric reflects *recent responsiveness*, so a stale-but-still-pending approval never contaminates the average, and a long-forgotten approval resolves *out* of the window once it ages past 7 days.

### D5. Wire contract: nullable stats + `SampleCount` discriminator

**Decision.** `sealed record ApprovalWaitMetricsResponse` with:

| Field | Type | Empty (0 samples) | 1+ samples |
|---|---|---|---|
| `Window` | `{ From: string, To: string }` (ISO `o`) | window bounds | window bounds |
| `SampleCount` | `int` | `0` | `n` |
| `AverageSeconds` | `double?` | `null` | mean |
| `MedianSeconds` | `double?` | `null` | median (avg of two middle for even `n`) |
| `MaxSeconds` | `double?` | `null` | max |

Wrapped in the standard `ApiResponse<T>` envelope via `ApiResults.Ok`. Seconds (not ISO duration / `TimeSpan`) because System.Text.Json's `TimeSpan` support is awkward and the frontend already formats raw numbers.

**Rationale.** The spec requires "empty result distinguishable from a genuine average of `0`" (`specs/approval-waiting-metrics/spec.md`, "Zero-sample aggregation returns a defined empty result"). `SampleCount == 0` is the discriminator; `null` stats (not `0`) prevents the UI rendering "0s" for a project with no approvals.

**Alternatives considered.**
- *Return `0` with a flag.* Conflates "no data" with a zero sample; rejected.
- *ISO-8601 duration strings (`PT3H12M`).* Verbose, harder to format client-side; rejected.

### D6. Route + file placement

**Decision.**
- Endpoint: `GET /api/projects/{projectRef}/issues/metrics/approval-wait` (literal `metrics/` prefix keeps the `{number:int}` route from colliding — see the existing comment at `IssueRoutes.Metrics.cs:10-11`).
- Backend files: `Api/IssueRoutes.ApprovalMetrics.cs` (new partial, registered via `projectIssues.MapIssueApprovalMetrics()` in `IssueRoutes.cs` next to `MapIssueMetrics`); DTOs appended to `Api/IssueRoutes.Dtos.cs`; query method in `IssueQuerier.cs` with its result `record`s alongside `CompletionBucketsResult`.

**Rationale.** 1:1 structural twin of the completion endpoint — reviewers see the symmetry.

### D7. Frontend: hook twins `useCompletionTrend`; Hero gains an optional `approvalWait` prop

**Decision.**
- New `packages/web/src/entities/issue/api/approval-wait.ts` exporting `fetchApprovalWait(projectId)` + `useApprovalWait()`, query key `['issues','metrics','approval-wait',projectId]`, `staleTime: 60_000`, `enabled: !!projectId`. Exported through `entities/issue/index.ts`.
- `AttentionHero` gains an optional `approvalWait?: ApprovalWaitMetrics` prop mirroring the existing `issues?` / `agentStatus?` injection slots (`AttentionHero.tsx:27-47`) — internally it calls `useApprovalWait()` and prefers the prop when present. This is the testability seam (already used by `AttentionHero.test.tsx`'s hoisted-mock pattern).
- Approve mutation's `onSuccess` invalidation (`AttentionHero.tsx:52-66`) adds `queryClient.invalidateQueries({ queryKey: ['issues','metrics','approval-wait'] })` so the number refreshes after a human acts.
- New `packages/web/src/shared/lib/format-duration.ts` (`formatDuration(seconds) → "3.2h"` / `"5d"` / `"<1m"`), with co-located `format-duration.test.ts`. None of the existing `format-time.ts` / `relative-time.ts` helpers format *elapsed durations* — they all compare a past timestamp to `now`.
- Empty presentation follows `CompletionTrend`'s convention: `data-testid="approval-wait-empty"`, `data-state="empty"`, muted Tailwind copy explaining *when* the number appears.

**Rationale.** The `completion-trend.ts` file is the exact template (same folder, same `staleTime`, same key namespace, same DTO-inline typing). The optional-prop pattern keeps the widget unit-testable without MSW (this repo has no MSW dependency). A dedicated duration formatter avoids overloading the relative-time helpers.

**Alternatives considered.**
- *Compute the metric client-side over `useIssues()` data.* Explicitly forbidden by the proposal/`dashboard-attention` spec ("the Hero SHALL NOT compute the metric client-side over the full issue list") — the issue list is paginated/filtered and would give the wrong denominator.
- *Poll like `useAgentStatus` (5s).* Approvals are human-paced; 60s `staleTime` is plenty and matches the sibling metric. Rejected.

### D8. Spec relaxation is explicit and minimal

**Decision.** Modify the `dashboard-attention` "derives content exclusively from existing read-only sources" requirement (per `specs/dashboard-attention/spec.md`) to add the single exception: the Hero MAY consume the approval-wait endpoint *only* for this one summary value. Every other Hero behavior stays read-only over existing sources; no new mutations beyond the existing approve/resume actions.

**Rationale.** The proposal flags this as the one spec-level BREAKING change. Keeping the exception narrow prevents scope creep toward a Hero that calls many backend endpoints.

## Risks / Trade-offs

- **[Approval data lives only in serialized JSON]** → Every aggregation deserializes each project workflow-run `State`. *Mitigation:* scope candidates through the indexed `Issues.ProjectId` → `WorkflowRunId` join before deserializing (the completion path already constrains via `Issues` first); at v1 volumes the candidate set is small. Flag for profiling if a project exceeds ~hundreds of issues (see Open Questions).
- **[Non-indexed project scoping on `WorkflowRuns`]** → `WorkflowRuns` has no stored `ProjectId`. *Mitigation:* never query `WorkflowRuns` by project directly; always resolve `WorkflowRunId`s from `Issues` (which carries the `(ProjectId, Number)` unique index) then load runs by id — identical to `LoadWorkflowStatesAsync`.
- **[Average is outlier-sensitive]** → A single forgotten approval skews the mean. *Mitigation:* the spec mandates returning avg **and** median **and** max from the same sample set; the Hero copy leads with the value but the endpoint exposes all three so a future UI can show median ("typical") vs. max ("worst") without a new endpoint.
- **[Drift between the metric and the Hero's per-item approval rendering]** → Both must agree on what counts as "completed". *Mitigation:* D1 — both consume `MohistDefaultWorkflowProjection`; one definition.
- **[Zero-sample UX ambiguity]** → "No data yet" could read as "broken". *Mitigation:* D5 + D7 — `SampleCount==0` returns `null` stats; the Hero renders an explicit empty state distinguishing it from a short wait.
- **[In-memory filtering cost as history grows]** → Same class of risk as the completion aggregator; accepted at v1, tracked under the existing D-OQ2 profiling note (`IssueQuerier.cs:283`).

## Migration Plan

**Deploy:** no schema change, no data backfill, no new writes. The endpoint is purely derived from already-populated `WorkflowRuns.State`. The Hero renders with or without the metric (the `approvalWait` prop is optional and the query failing/returning empty degrades to the empty presentation).

**Rollback:** revert the code change. The endpoint disappears (404); the Hero's `useApprovalWait` query fails and the widget renders the empty/zero-sample presentation — existing issue/agent queries and approve/resume actions are unaffected. No data cleanup required.

**Sequencing:** #258 is already merged, so the Hero slot exists. This change can land independently of any other in-flight issue.

## Open Questions

- **History growth.** At what project issue count does deserializing all workflow-run states for the metric become noticeable? Deferred to profiling (mirrors the open D-OQ2 note on the completion aggregator). If it bites, options are (a) a `json_extract`-based raw SQL pre-filter on `respondedAt`, or (b) persisting approval transitions as `IssueEvents` rows (currently a Non-Goal, would be a separate change).
- **Which statistic leads the Hero copy?** The endpoint returns avg/median/max; the spec mandates the Hero show the *average*. Whether median is a better "typical" signal for a future iteration is a product call, not a contract decision — returning all three now keeps it open.
- **Should the window also support a `?window=7d` query param for future flexibility?** v1 ships fixed 7d (parity with completion's fixed day/week). Parameterization deferred until a second window length is actually requested.
