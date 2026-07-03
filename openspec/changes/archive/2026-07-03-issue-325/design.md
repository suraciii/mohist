## Context

`IssueQuerier` (`packages/server/src/Mohist.Server/Issue/Services/IssueQuerier.cs`, 2393 lines) mixes six unrelated responsibilities. The two largest — synchronous read-model query and analytics aggregation — are provably orthogonal: no read-model method calls a metrics method and no metrics method mutates a read model. The cost of the colocation is duplication, not coupling:

- The "load a project's issue rows → resolve default template id + disabled workflow profile ids → `ToInfo` → `ToReadModel` → apply workflow/feedback projections" prelude is copy-pasted across 5 call sites (2 read-model: `ListWithLabelFiltersAsync`, `ListInProgressWithApprovalGateAsync`; 3 metrics: `GetQualityAsync`, `GetApprovalWaitAsync`, `GetStageDurationsAsync`).
- The "scan `IssueEvents` constrained to the project's issue sources" loop is copy-pasted across 4 call sites, **all of them metrics** (`GetCompletionBucketsAsync`, `GetQualityAsync`, `GetDeliveryTimesAsync`, `GetStageDurationsAsync`).
- The field-by-field `Issue` → `IssueInfo` mapping body is duplicated across 4 `ToInfo` overloads (+ an async `ToInfoAsync`), differing only in how `WorkflowProfileId` is resolved.
- The odd/even median formula exists twice: inline in `GetApprovalWaitAsync` (lines ~1040) and as `ComputeMedian` (line ~1520) whose own comment admits it "reuse[s] the exact formula from `GetApprovalWaitAsync`" yet the two never call each other.

`IssueStore.Deserialize` (95-line file) carries a legacy label-format normalization branch (`NormalizeLegacyLabels` + an `out bool legacyLabelsDiscarded` overload) for an array-format label shape the project has declared it never needs to support.

Constraints / stakeholders:
- This is the next step of epic #31 ("converge the super-large services to single responsibility").
- The 17 API route partials are already healthy and stay untouched in structure; only 5 metrics partials change their **type references**.
- `IssueStageAttribution` is already a separately-extracted pure collaborator shared between stage-duration metrics and the snapshot job; it is **not** in scope to move, only its doc comment's `IssueQuerier.GetStageDurationsAsync` cref updates.
- No HTTP contract, DTO, read-model field shape, or aggregation formula changes — only where code lives and how results are constructed.

## Goals / Non-Goals

**Goals:**
- Split `IssueQuerier` into a read-model-only `IssueQuerier` and a metrics-only `IssueMetricsQuerier`, each independently evolvable.
- Collapse the 5 load-and-map prelude copies into one shared helper consumed by both services.
- Collapse the 4 event-scan copies and the 2 median copies into single implementations inside the metrics service.
- Merge the 4 near-duplicate `ToInfo` mapping bodies into one consolidated path.
- Remove the legacy label normalization from `IssueStore`.
- Keep every existing Issue query and metrics spec green (behavior-preserving move).

**Non-Goals:**
- Change any aggregation formula, window definition, or bucketing rule.
- Change any read-model / DTO field shape or HTTP contract.
- Touch the API route partial **file** structure (the 5 metrics partials keep their files; only injected service type + result-type references change).
- Global `cancelled` status naming normalization (cross-context product decision).
- Refactor `IssueStageAttribution` — it is already a shared pure collaborator and stays put.

## Decisions

### D1 — New `IssueMetricsQuerier` owns all analytics; `IssueQuerier` keeps read-model only

`IssueMetricsQuerier` (new file `Issue/Services/IssueMetricsQuerier.cs`) receives:
- The 5 public methods: `GetCompletionBucketsAsync`, `GetQualityAsync`, `GetApprovalWaitAsync`, `GetDeliveryTimesAsync`, `GetStageDurationsAsync`.
- All their result records + the `CompletionBucket` enum + private accumulator/helper types (`QualityAccumulator`, `QualityTrendAccumulator`, `WorkflowRunEventFact`, `WorkflowRunStageEvent`, `PerIssueCycleBreakdown`).
- Metrics-only private helpers: `ClassifyRuns`, `BuildWindow`, `BuildTrend`, `LoadWorkflowRunsAsync`, `LoadWorkflowRunEventFactsAsync`, `LoadWorkflowRunStageEventsAsync`, `ComputeIssueAttribution`, `ComputeLatestAttemptStageDurations`, `SumApprovalGateWaitSeconds`, `ResolveProjectStageOrderAsync`, `BuildEmptyStageDurationResult`, `ComputeMedian`, `ISOWeekHelper`, the `QualityStageOrder`/`QualityWorkflowEventTypes`/`StageDurationEventTypes` static arrays.

`IssueQuerier` keeps: `GetAsync`, `GetInfoAsync`, `GetDomainAsync`, `GetIssueIdForWorkflowRunAsync`, `ListAsync`/`ListWithLabelFiltersAsync`, `ListInProgressWithApprovalGateAsync`, the consolidated `ToInfo` mapping, `ToReadModel`, enrichment, projections, label-filter helpers.

**Why split rather than namespace-reorganize:** the two concerns never call each other, so the split introduces no cycle and is regression-free in principle; it also physically forces the orthogonality to survive future edits (the root user voice).

**Alternative considered:** keep one class, extract metrics into a `#region` / partial file. Rejected — partials across concerns is exactly the anti-pattern this epic is converging away from, and the duplication cannot be removed without a real type boundary to hand the shared helper to.

### D2 — `IssueMetricsQuerier` is auto-registered as scoped via `IScopedService` (NOT a manual registration)

**Correction to the proposal:** the proposal names "DI registration (`MigratedServicesRegistration`)" as a manual step. The actual mechanism is assembly-scanned conventional registration — `ServiceCollectionExtensions.AddMohistConventionalServices` uses Scrutor to register every concrete `IScopedService` as itself with `Scoped` lifetime. `IssueQuerier` is registered this way today; it has no hand-written registration line.

Therefore `IssueMetricsQuerier` gets scoped registration for free by implementing `IScopedService` (matching `IssueQuerier`'s lifetime semantics, satisfying the spec's "scoped, distinct per scope" scenario). The only DI-adjacent change is adding one theory row to `MigratedServicesRegistrationSpecs.MigratedServices()`:

```csharp
yield return new object[] { typeof(IssueMetricsQuerier), ServiceLifetime.Scoped };
```

**Alternative considered:** add an explicit `services.AddScoped<IssueMetricsQuerier>()` line. Rejected — it would be the only hand-written line for an otherwise conventionally-registered service, creating a "last-registration-wins" override that adds noise without value and diverges from the established pattern.

### D3 — The 4 event-type/source constants move **with** the metrics code

`WorkStartedType`, `WorkCompletedType`, `ClosedType`, `IssueSourcePrefix` are `internal const` on `IssueQuerier` today. Inspecting every reference: **all four are used exclusively inside metrics methods** (the 4 event-scan sites + the terminal-type set in completion buckets). No read-model method touches them. They move to `IssueMetricsQuerier` as `internal const`, so:

- The read-model service carries no event-scan concern (consistent with the "no metrics concern in read-model" requirement).
- `IssueMetricsQuerier` does **not** depend on `IssueQuerier` at all (clean type boundary).
- Tests that seed events (`IssueMetricsApiSpecs.cs`) update `IssueQuerier.WorkCompletedType` → `IssueMetricsQuerier.WorkCompletedType`, etc. — the mechanical "update seed-constant references" step already listed in the proposal.

**Alternative considered:** a shared `IssueEventTypes` static holder. Rejected as over-engineering — there is no second consumer outside metrics, so a shared holder would be speculative. If a future non-metrics consumer appears, extracting a holder then is a trivial follow-up.

### D4 — The load-and-map prelude becomes a shared collaborator (cross-service)

The prelude needs instance dependencies (`_dbFactory`, `_projectProfileManager`, `_effectiveProfileResolver`, `_profiles`) and must be reachable from **both** `IssueQuerier` and `IssueMetricsQuerier` (spec `issue-query-shared-loading` requires it be "a cross-service shared helper available to both ... MUST NOT be duplicated within each service"). A private method on either querier would force the other to depend on it, re-coupling the two services we just split.

Decision: extract a scoped collaborator `IssueReadModelLoader` (new, `Issue/Services/IssueReadModelLoader.cs`, `IScopedService`) owning the single prelude:

```
LoadProjectedAsync(db, projectId, project?) -> List<IssueReadModel>
  // rows → LoadProjectDefaultTemplateAsync → GetDisabledWorkflowProfileIdsAsync
  //     → IssueRowMapper.ByNumber → consolidated ToInfo → ToReadModel
  //     → ApplyWorkflowProjections + ApplyFeedbackProjections
```

Both queriers inject it. The 5 former call sites (2 read-model, 3 metrics) call `loader.LoadProjectedAsync(...)`. The helper methods it needs (`LoadWorkflowStatesAsync`, `LoadFeedbackAsync`, `ApplyWorkflowProjections`, `ApplyFeedbackProjections`, `LoadProjectDefaultTemplateAsync`, the consolidated `ToInfo`/`ToReadModel`) move to the loader; `IssueQuerier` delegates its single-issue enrichment paths through the same mapping.

**Alternatives considered:**
- (a) Static helper taking all dependencies as parameters — rejected: 5+ params per call, hides the collaborator behind procedural signatures, can't stay scoped.
- (b) Put the prelude on `IssueQuerier` and have `IssueMetricsQuerier` inject `IssueQuerier` — rejected: re-couples metrics to read-model and lets metrics accidentally call read-model methods; violates the orthogonality the split is meant to enforce.

### D5 — The event-scan loop and median stay **inside** `IssueMetricsQuerier`

All 4 event-scan call sites are metrics-internal (completion, quality, delivery-time, stage-duration), and the spec `issue-metrics-aggregation` scopes these as "shared internal patterns **within** the metrics service." They become private helpers on `IssueMetricsQuerier`:

- `ScanIssueEventsByProjectSourceAsync(db, projectSources, typeFilter?)` → returns the filtered, in-memory event stream (SQLite can't translate `DateTimeOffset` on the TEXT `Time` column, so the materialize-then-filter idiom is preserved verbatim).
- `LoadAndPairWorkflowRunsAsync(...)` for the quality/stage-duration run-discovery + event-fact pairing.
- `ComputeMedian(sortedSamples)` — the single median; `GetApprovalWaitAsync`'s inline copy (lines ~1040) is deleted and delegates here.

**Why not a separate collaborator here too:** unlike the prelude, these patterns have no consumer outside metrics, so a second collaborator would be speculative. A private helper on the same class satisfies "single implementation, shared across callers" with no extra type.

### D6 — Consolidate the 4 `ToInfo` bodies into one mapping with a resolved `profileId` parameter

The 4 overloads differ only in `WorkflowProfileId` resolution:
- static (project=null) and static (resolver, project): hardcode `IssueWorkflowProfiles.LocalId` (used by no-DI paths — the prerequisite-missing lookup in `EnrichAsync`).
- instance (templateId) and instance (templateId, disabledIds): use `_effectiveProfileResolver.Resolve(...)`.

Consolidation: one private static body `BuildInfo(issue, project, resolvedProfileId)` that field-by-field constructs `IssueInfo`. Public entry points become thin wrappers that only differ in computing `resolvedProfileId`:
- instance `ToInfo(issue, project, templateId, disabledIds)` → resolves via `_effectiveProfileResolver` → `BuildInfo`.
- static `ToInfo(issue, project)` / `ToInfo(resolver, issue, project)` → pass `IssueWorkflowProfiles.LocalId` → `BuildInfo` (preserving the current no-DI default exactly).

The field-by-field mapping now appears in **exactly one location**, satisfying the spec. This concern is read-model-only, so `BuildInfo` lives with the loader (D4) / `IssueQuerier`, not metrics.

**Risk note:** the static paths' hardcoded `LocalId` default is a latent correctness smell (no-DI prerequisite lookups assume the local profile), but changing that semantics is explicitly out of scope — consolidation preserves it bit-for-bit.

### D7 — Remove legacy label normalization from `IssueStore`

Delete `NormalizeLegacyLabels` and the `Deserialize(string, out bool legacyLabelsDiscarded)` overload. The surviving `Deserialize(string)` becomes:

```csharp
public static DomainIssue? Deserialize(string json) =>
    string.IsNullOrEmpty(json) ? null : JSON.Deserialize<DomainIssue>(json);
```

Confirmed the 2-arg overload has no external caller — it is only called by the 1-arg overload internally. The spec's "only the single-result overload is available" scenario is satisfied by deletion.

## Risks / Trade-offs

- **[Overlooked call site of a moved type/const]** → The move is source-breaking within the assembly. Mitigation: `TreatWarningsAsErrors` + compile failure pinpoints every reference; the full green query+metrics spec suite is the regression gate.
- **[Shared loader subtly alters one duplicated block's edge behavior]** → Each of the 5 prelude copies and 4 scan copies has minor per-caller variation (e.g. delivery-time maps via `IssueRowMapper.ById` not `ByNumber`, completion scans terminal types only). Mitigation: the loader covers only the 5 sites whose block is **structurally identical** (load→map→project); delivery-time and completion keep their own scan logic via the parameterized `ScanIssueEventsByProjectSourceAsync` helper rather than being force-fitted. Behavior is preserved because each caller passes its own type filter / projection decision.
- **[Static `ToInfo` LocalId default drift]** → Consolidation must keep the no-DI path's hardcoded `LocalId`. Mitigation: D6 makes `BuildInfo` take the already-resolved `profileId`, so the static wrappers are the only place that still hardcodes `LocalId` — the smell is preserved, not widened. A future issue can address it.
- **[`ThrowingIssueQuerier` test stub breaks]** → Two epic spec files subclass `IssueQuerier`. Since `IssueQuerier` keeps its constructor and read-model methods (only loses metrics methods), the stubs stay constructor-compatible. Mitigation: if a stub overrode a metrics method, move that override out; verify by compiling the epic specs.
- **[Type-namespace move is source-breaking for any out-of-tree consumer]** → Acceptable: the repo is the only consumer and is under active development with no version-compat constraint (per AGENTS.md).

## Migration Plan

Single-PR, single-deploy, no data migration. Order of edits (each step compiles before the next):

1. **Persistence cleanup (D7)** — remove `NormalizeLegacyLabels` + 2-arg overload in `IssueStore.cs`. Self-contained, compile-verify.
2. **Create `IssueMetricsQuerier` (D1, D3, D5)** — new file; move the 5 methods, records, enum, accumulators, helpers, and the 4 constants. At this point `IssueQuerier` no longer references them.
3. **Create `IssueReadModelLoader` (D4)** — extract the shared prelude + the mapping helpers it owns; rewire `IssueQuerier`'s list/approval-gate paths and `IssueMetricsQuerier`'s quality/approval-wait/stage-duration paths to call it.
4. **Consolidate `ToInfo` (D6)** — collapse to `BuildInfo` + thin wrappers inside the loader/`IssueQuerier`.
5. **Repoint route partials** — the 5 `IssueRoutes.*Metrics.cs` partials: inject `IssueMetricsQuerier` instead of `IssueQuerier`, update `IssueQuerier.CompletionBucketsResult` → `IssueMetricsQuerier.CompletionBucketsResult` (and the enum/result/record crefs).
6. **Registration + tests** — add the scoped theory row (D2); split metrics specs out of `IssueQuerierSpecs.cs` into `IssueMetricsQuerierSpecs.cs`; update `IssueMetricsApiSpecs.cs` const references; verify `ThrowingIssueQuerier` still compiles.

**Verification gate:** `npm test` (server) full green — every existing query and metrics spec is the regression assertion (specs assert "identical to before this change").

**Rollback:** revert the single PR. No persisted-data implications: the legacy-label removal is the only data-adjacent change, and the project has declared no persisted rows carry the legacy array format, so a deploy→rollback→redeploy sequence never encounters a row that the removed branch would have handled.

## Open Questions

- **Median naming/location confirmation:** `ComputeMedian` currently lives as a private static on `IssueQuerier` used only by stage-duration; the inline copy is in approval-wait. After D5 both call the single `IssueMetricsQuerier.ComputeMedian`. Confirm there is no third median consumer hiding in a non-`IssueQuerier` file (quick `rg "samples.Count / 2"` sweep during implementation).
- **`IssueReadModelLoader` boundary vs. `IssueQuerier` enrichment:** the loader owns load+map+project; `IssueQuerier` keeps the heavier `EnrichAsync` (comments, attachments, epic links, prereqs, agent config). Confirm the spec's "single consolidated read-model mapping" is satisfied by the loader owning `ToInfo`/`ToReadModel` even though `EnrichAsync` stays on `IssueQuerier` — i.e. mapping vs. enrichment are distinct and only mapping must consolidate. (Reading of the spec: yes — the spec constrains the field-by-field `Issue → IssueInfo` mapping body, not the post-mapping enrichment.)
