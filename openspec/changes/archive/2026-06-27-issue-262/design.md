## Context

The dashboard has no spend feedback loop. Two surfaces ship empty pending this change:

- `InvestmentPanel` (`packages/web/src/pages/dashboard/productivity/InvestmentPanel.tsx:14`) is an explicit empty shell — its body (`InvestmentPanel.tsx:67-76`) literally reads "Data unavailable — … When the usage aggregation hook lands".
- The factory-status headline (`packages/web/src/widgets/factory-status/`) reserved a today-cost slot from #258: `FactoryStatusFields.todayCost: undefined` (`model/factory-status.ts:9,51`) and the headline renders a literal `—` for it (`ui/FactoryStatusHeadline.tsx:66-72`, testid `factory-cost-reserved`).

The raw data already exists. Per-session usage is recorded on `AgentSessionStatusSnapshot.UsageSummary` (`packages/server/src/Mohist.Server/Sessions/Domain/AgentSession.cs:86-106`) as `double? CostAmount` + `string? CostCurrency` (ISO code), serialized into `AgentSessionRow.State` JSON. The server already aggregates this into a 7-day daily-bucket timeseries via `AgentSessionQuerier.GetUsageTimeseriesAsync` (`packages/server/src/Mohist.Server/Workflow/Services/Sessions/AgentSessionQuerier.cs:283`) exposed at `GET /api/projects/{projectRef}/agent/usage` (`packages/server/src/Mohist.Server/Api/AgentRoutes.cs:40`). Issue status is queryable via `IssueQuerier.ListAsync` and `"done"` (lowercase) is the read-model literal for shipped issues (`packages/server/src/Mohist.Server/Issue/Services/WorkflowProfiles/MohistDefaultWorkflowProjection.cs:9-13`; mirrored in `StatusRoutes.cs:58`).

Constraints / stakeholders:

- This issue is **blocked by #258** (first-screen refactor), which reserved the headline today-cost slot. It belongs to epic #23.
- The frontend does **not** currently call `/agent/usage`; there is no existing client to extend. A new hook is authored from scratch, mirroring `entities/issue/api/approval-wait.ts`.
- No new data collection, no schema change, no session/issue-domain write (spec requirement: "Aggregation introduces no new data collection").

## Goals / Non-Goals

**Goals:**

- Project-scoped cost rollup endpoint co-located with the existing agent-usage surface, returning `totalCost` (cumulative), `todayCost` (current UTC calendar day), `doneIssuesCount`, and `costPerShip`, each with an empty/zero-sample state distinguishable from a real computed value.
- Fill `InvestmentPanel` with cumulative spend + cost-per-ship + done-issue denominator (no longer an empty shell).
- Wire the reserved headline today-cost slot to `todayCost`, with empty distinct from real zero.
- Test coverage for aggregation logic (calendar-day boundary, zero-sample independence, free-shipping real zero, unknown-project 404) and the two UI wirings.

**Non-Goals:**

- No `$/LOC` (LOC is not value).
- No budget hard cap / alerting.
- No per-model or per-prompt drill-down — totals only.
- No change to the existing 7-day timeseries contract beyond co-location on the same route group.
- No frontend consumption of the 7-day `/agent/usage` timeseries (out of scope; the rollup carries the scalar figures this issue needs).

## Decisions

### D1. New sibling endpoint `GET /api/projects/{projectRef}/agent/cost` on the existing agent route group

Add `group.MapGet("/cost", …)` in `packages/server/src/Mohist.Server/Api/AgentRoutes.cs`, reusing the `ProjectResolutionEndpointFilter` already applied to the group (`AgentRoutes.cs:13-14`) and resolving the project via `context.GetResolvedProject()` exactly like the sibling `/usage` handler (`AgentRoutes.cs:42-43`). Return via `ApiResults.Ok(data)` (envelope `{success,data}`).

- **Alternatives considered:**
  - *Extend `/usage` to also return the rollup* — rejected: `/usage` returns a timeseries array (`AgentUsageTimeseriesDto.Buckets`), the rollup is a scalar object; mixing shapes couples two reads and forces dashboards that only want the scalar to deserialize the array.
  - *Add a query param `?summary=1` to `/usage`* — rejected for the same shape-mismatch reason; also harder to cache independently.
- Co-location requirement from the spec is satisfied by sharing the route group + filter + project resolution; the existing `/usage` contract is untouched.

### D2. Rollup is composed in the endpoint handler from two scoped queriers (no new service class)

The handler injects `AgentSessionQuerier` (for the cost fields) and `IssueQuerier` (for the done count), mirroring how `StatusRoutes.cs:44-60` composes multiple queriers in-line.

- **Cost path:** add `GetCostRollupAsync(string projectId, CancellationToken ct)` onto `AgentSessionQuerier` next to `GetUsageTimeseriesAsync` (`AgentSessionQuerier.cs:283`). It reuses `_sessionQuery.ListByLabelsAsync` with the `ProjectId` label, `AgentSessionJsonHelper.Usage` (`Sessions/Services/AgentSessionJsonHelper.cs:21`), and the `HasUsage` predicate (`AgentSessionQuerier.cs:323`). Two reads:
  - `totalCost`: unbounded window (`from`/`to` omitted), sum `CostAmount ?? 0d` over sessions where `HasUsage`, currency from the first non-null `CostCurrency` (same `??=` pattern as `AgentSessionQuerier.cs:313`).
  - `todayCost`: window `[DateTime.UtcNow.Date, DateTime.UtcNow.Date.AddDays(1))` — the current-day bucket of the timeseries, identical boundary math to `AgentSessionQuerier.cs:285-286`.
- **Done-count path:** in the handler, `await issuesQuery.ListAsync(project.Id, project, all: true)` then `Count(i => i.Status == "done")` — verbatim pattern from `StatusRoutes.cs:44-58`. `doneIssuesCount` is never "empty" — `0` is a real value (a project with zero shipped issues).
- `costPerShip` is computed in the handler: `totalCost.amount / doneIssuesCount` when `doneIssuesCount > 0` and `totalCost` has a sample; otherwise the undefined/empty result.

- **Alternatives considered:**
  - *A dedicated `AgentCostRollupService` / `DashboardRollupService`* — rejected: too much ceremony for one read; the codebase composes queriers in handlers (StatusRoutes precedent).
  - *Push the done-count into `AgentSessionQuerier`* — rejected: mixes the agent-session domain with the issue domain inside one service; keep `IssueQuerier` as the single reader of issue status.

### D3. Response shape: per-metric nullable amount + sample count (mirrors the QualityPanel `sampleCount === 0` convention)

```
AgentCostRollupDto {
  totalCost:    AgentCostMetricDto   // { amount: double?, currency: string?, sampleCount: int }
  todayCost:    AgentCostMetricDto
  doneIssuesCount: int               // never empty; 0 is real
  costPerShip:  AgentCostMetricDto   // amount null when doneIssuesCount == 0
}
```

- **Empty vs real zero:** `amount == null && sampleCount == 0` ⇒ empty/no-data; `amount == 0.0 && sampleCount > 0` ⇒ a genuine computed zero (free shipping). `costPerShip.amount` is `null` precisely when `doneIssuesCount == 0` (undefined ratio), independently of whether `totalCost` has a sample. This satisfies the spec's "emptiness is evaluated independently per metric" requirement and maps directly onto the frontend's existing `sampleCount === 0` convention (`packages/web/src/pages/dashboard/productivity/QualityPanel.tsx:19,100-118`).
- `doneIssuesCount` is a bare `int` — a count has no empty state; `0` means "zero shipped issues", full stop.

- **Alternatives considered:**
  - *Discriminated union / `Option<double>`* — rejected: C#-idiomatic nullability + a parallel count is simpler and serializes cleanly to JSON the frontend already patterns on.
  - *A single `hasData: bool` flag per metric* — rejected: `sampleCount` is strictly more informative and already the established convention.

### D4. Currency handling: single-currency assumption with first-non-null currency, matching the existing timeseries

Sum `CostAmount` unconditionally across sessions and report `CostCurrency` from the first non-null session (`??=` pattern, `AgentSessionQuerier.cs:313`). This is a known limitation already present in the shipped timeseries; the rollup inherits it for consistency rather than introducing a divergent behavior in this issue.

- **Alternatives considered:**
  - *Per-currency breakdown* (e.g. `{ "USD": 1.20, "EUR": 0.05 }`, as `WorkflowSessionsPanel.tsx:95` already does for its inline rollup) — deferred. The headline figures this issue surfaces (cumulative spend, cost-per-ship) are single-amount presentations; a per-currency breakdown is a natural follow-up if real multi-currency projects appear. Flagged in Open Questions.

### D5. Frontend hook in `entities/agent/api/cost-rollup.ts`, mirroring `approval-wait.ts`

Create `packages/web/src/entities/agent/api/cost-rollup.ts` with `fetchCostRollup(projectId)`, an exported `costRollupQueryKey(projectId?)`, and `useCostRollup()` (`enabled: !!projectId`, `staleTime: 60_000`). Export through `entities/agent/index.ts`. The data source is agent-session usage, so it belongs under `entities/agent` (not `entities/issue`, despite the metrics hooks living there).

- **Alternatives considered:**
  - *Place under `entities/issue/api/`* — rejected: the cost fields are agent-session data; only `doneIssuesCount` comes from issues. Placing in `entities/agent` keeps the data source honest.
  - *Reuse the (nonexistent) `/agent/usage` client* — rejected: that client does not exist on the frontend and the timeseries shape is not what we need.

### D6. UI wiring

- **`InvestmentPanel`** (`InvestmentPanel.tsx`): call `useCostRollup()`; when `totalCost.sampleCount === 0`, keep a `data-state="empty"` block with copy "No spend recorded yet — …" (mirroring `QualityPanel.tsx:100-118`); otherwise render cumulative spend (`formatCost(totalCost.amount, totalCost.currency)`), cost-per-ship (`formatCost(costPerShip.amount, …)` or `—` when undefined), and the done-issue count. Update the caliber basis string (`InvestmentPanel.tsx:11-12`) from "trailing 7 days" to reflect cumulative semantics. The existing `data-state="empty"` / `INVESTMENT_EMPTY_TESTID` tests move to the no-spend branch.
- **Factory-status headline** (`factory-status`): extend `FactoryStatusFields.todayCost` from `undefined` to `{ amount: number | null; currency: string | null; sampleCount: number } | undefined`; thread `useCostRollup()` into `FactoryStatusHeadline` alongside `useIssues`/`useAgentStatus` (`FactoryStatusHeadline.tsx:14-26`) and pass it through `deriveFactoryStatus`; replace the `factory-cost-reserved` block (`FactoryStatusHeadline.tsx:66-72`) with: `sampleCount === 0` ⇒ keep `—` (no-data); otherwise `formatCost(amount, currency)` (which yields `$0.00` for a real zero). Update the placeholder tests (`FactoryStatusHeadline.test.tsx:147-153`, `factory-status.test.ts:79,137,141-142`) to assert the new behavior.

## Risks / Trade-offs

- **[Mixed-currency sum is incorrect for multi-currency projects]** → Mitigation: inherit the existing timeseries behavior (D4) so the rollup is no worse than the shipped 7-day view; document the single-currency assumption; defer per-currency breakdown to a follow-up. `formatCost` already handles the display symbol by currency code.
- **[`CostAmount` is `double?`, not `decimal` — floating-point drift on money]** → Mitigation: acceptable for display-grade aggregates in a personal-dev tool (not ledger accounting); matches the existing storage type so no conversion drift is introduced; display rounds via `formatCost`'s `toFixed(2)`. No mitigation needed beyond documenting it.
- **[`totalCost` is an unbounded all-history scan]** → Mitigation: the read path is the same `AgentSessionQuery.ListByLabelsAsync` used by the 7-day timeseries, just without the `from`/`to` bounds; for a personal-dev project's session volume this is fine. Watch-point: if a project accumulates tens of thousands of sessions, add a materialized `AgentCostSummary` row later — out of scope here.
- **[Changing `InvestmentPanel` from empty shell to populated breaks the `data-state="empty"` / "data unavailable" tests]** → Mitigation: the no-spend branch preserves a `data-state="empty"` rendering with new copy, so the empty-state contract is upheld for the genuinely-empty case; update the test assertions in the same change.
- **[Wiring `todayCost` changes the `factory-cost-reserved` test contract]** → Mitigation: the `—` rendering is preserved for the no-sample case (`sampleCount === 0`); only the "reserved" semantics change. Tests updated in the same change.
- **[Rollback]** → Mitigation: the change is additive on the backend (new endpoint, no migration, no write) and isolated on the frontend (two component updates + one new hook). Reverting the commits restores the empty shell and reserved slot cleanly; no data to migrate.

## Migration Plan

This is a read-only, additive change — no data migration, no feature flag.

1. **Backend (additive):** add `GetCostRollupAsync` to `AgentSessionQuerier`; add the `AgentCostRollupDto` / `AgentCostMetricDto` records to `AgentSessionReadModels.cs` (next to `AgentUsageTimeseriesDto`); register `GET /agent/cost` in `AgentRoutes.cs`. The existing `/agent/usage` timeseries is untouched. Unknown project → `404` via the shared `ProjectResolutionEndpointFilter` (`ProjectResolutionEndpointFilter.cs:50`).
2. **Frontend hook:** add `entities/agent/api/cost-rollup.ts` + barrel export.
3. **Frontend UI:** fill `InvestmentPanel`; wire `todayCost` into `factory-status`.
4. **Tests:** backend aggregation unit tests (calendar-day boundary, zero-sample independence, free-shipping real zero, no-usage empty, unknown-project 404, no-new-collection invariant); `InvestmentPanel` render + empty-state tests; `FactoryStatusHeadline` today-cost tests (populated, empty `—`, real `$0.00`).
5. **Rollback:** `git revert` the change commits. No data cleanup required.

## Open Questions

- **Per-currency breakdown:** Should the rollup eventually return `{ "USD": 1.20, "EUR": 0.05 }` instead of a single summed amount? Deferred until a real multi-currency project appears; `WorkflowSessionsPanel.tsx:95` already has the frontend pattern to copy.
- **Materialized summary for long histories:** At what session count does the all-history `totalCost` scan need a persisted summary row? Defer until measured; the query path is identical to the shipped timeseries so the baseline cost is known.
- **Endpoint name:** `GET /agent/cost` is chosen for brevity and co-location; `/agent/usage/summary` was considered for tighter coupling to `/usage`. Revisit if a second summary endpoint lands and a naming scheme is needed.
