## Context

`WorkflowQuerier.GetStatusAsync` (`packages/server/src/Mohist.Server/Workflow/Services/WorkflowQuerier.cs:35`) is the highest-frequency full-load read in Server. Every call:

1. loads the whole `WorkflowRuns` row (`FirstOrDefaultAsync`, `AsNoTracking`),
2. fully deserializes `State` via `Hydrate` → `JSON.Deserialize<WorkflowRun>(row.State)` (实测平均 325 KB, 最大 3.6 MB),
3. re-runs the definition cascade (`WorkflowDefinitionResolver.LoadTemplateAsync` — several scalar DB reads + a small `JsonDocument.Parse` over `State` to read the bound profile id), supporting live profile edits (hot-reload, see `WorkflowDefinitionResolver.cs:89-92`),
4. builds `WorkflowStatusView` (`WorkflowStatusMapper.BuildStatusView`),
5. attaches artifact summaries from the separate `WorkflowArtifacts` table.

`mo run watch` polls this every 3s; runner high-frequency reports add more. Step 2 is the proven LOH source — STJ string transcoding during the big deserialize accounts for 95%+ of LOH allocations, driving RSS toward 2 GB. #537 already removed State's bulk (dispatch snapshot externalization); this issue removes the *frequency tax* on unchanged State.

The status view therefore assembles from **three independently-versioned inputs**:

- **State** (run aggregate) — versioned by the `WorkflowRuns.ETag` shadow column, incremented on every actual State write (`WorkflowRunStore.cs:156,166`) and maintained idempotently by cold-start upgraders (`design/workflow/run-state.md:59-66`).
- **Definition** (resolved profile/template) — re-resolved per call; can change via profile edits independent of State ETag.
- **Artifacts** — separate table; change independent of State ETag.

`WorkflowQuerier` is `IScopedService` (per-request lifetime, `ServiceCollectionExtensions.cs:34-36`). A per-scope cache would die with each HTTP request and give zero benefit to the 3s cross-request poll. The codebase's caching/lifetime convention is the `ISingletonService` marker (auto-registered `AsSelf()` singleton); `Microsoft.Extensions.Caching.Memory` is not used anywhere and not referenced in the server csproj. `IDbContextFactory<MohistDbContext>` is singleton-safe (`ProjectQuerier` is already a singleton querier built on it).

## Goals / Non-Goals

**Goals:**
- Eliminate the full `State` deserialize (the LOH source) on status reads where State has not changed since the last read.
- Preserve exact equivalence to the uncached result for State, definition, and artifacts — no stale views.
- Keep the cache bounded so it does not itself regress process memory (Epic #65 is a memory-regression epic).
- Leave the external contract (`WorkflowStatusView?`) and every caller unchanged.

**Non-Goals:**
- Changing the status contract shape or any caller.
- Changing State write path / write amplification, or the ETag increment rule.
- Optimizing the other full-load reads (`GetWorkspaceAsync`, `GetRepositoryContextAsync`, log paths — #538).
- Caching definition resolution or artifact reads (they are not the LOH bottleneck and support live edits).
- Cross-process / distributed caching.

## Decisions

### D1. Cache the deserialized `WorkflowRun` aggregate, not the assembled view

The cache stores `(workflowRunId, ETag) → WorkflowRun` — the deserialized aggregate only. On a hit, `GetStatusAsync` skips step 2 (read + deserialize `State`) but still runs definition resolution, view mapping, and artifact attachment per call.

**Rationale.** ETag versions exactly `State`. The aggregate is the single thing whose reconstruction is the proven LOH source. Definition and artifacts are *not* versioned by ETag and must stay live-fresh; by leaving them per-call we inherit their existing freshness semantics for free and avoid inventing version signals for them. `BuildStatusView` and `AttachArtifactSummariesAsync` only *read* the aggregate and build new view objects (they mutate the view, not the run), so the cached aggregate is safely shareable as a read-only snapshot. `Hydrate` also applies `WorkflowRunLineage.RestoreStoredEpicNumber(run, row.EpicNumber)`; EpicNumber is written in the same save transaction as State, so the ETag covers it too — the cached entry stores the post-restoration aggregate.

**Alternatives.**
- *Cache the full `WorkflowStatusView` keyed on `ETag` + artifact-version + definition-version.* Rejected: requires new versioning machinery for artifacts (count/max-RecordedAt) and definition (profile revision), and any stale version signal silently returns a wrong view. The composite key's failure mode is *incorrect data*, whereas D1's worst case is merely *redundant work*.
- *Cache at the store layer (`WorkflowRunStore.LoadAsync`).* Rejected: `LoadAsync` is the control-plane grain's authoritative hydration path; coupling a read cache there conflates control-plane and query-plane concerns and risks serving the grain a stale aggregate. The optimization belongs to the query read path, which is where the frequency tax lives.

### D2. The cache lives in a singleton store, injected into the scoped querier

A new `ISingletonService` (e.g. `WorkflowRunStatusCache`) holds a bounded `ConcurrentDictionary<string, StatusCacheEntry>` where `StatusCacheEntry = { long ETag; WorkflowRun Run; }`. `WorkflowQuerier` (still scoped) depends on it. On each `GetStatusAsync`:

1. project the row's `ETag` as a scalar (`Select(e => EF.Property<long>(e, "ETag"))` for the shadow property; no `State` materialization); if no row → return `null`, do not cache.
2. if the cache holds an entry for `workflowRunId` with the same `ETag` → **hit**: reuse the cached `WorkflowRun`, skip `State` read + deserialize.
3. else **miss**: load the full row, `Hydrate`, store `(ETag, run)`.

**Rationale.** Singleton outlives the request scope, so the 3s poll — which is a fresh request each time — actually reuses a warm entry. The scoped querier keeps its per-request collaborators (db context factory, definition/artifact resolvers); only the aggregate memo is shared. `IDbContextFactory` is singleton-safe, and a singleton that only reads (never writes the DB) has no lifetime hazard.

**Alternatives.**
- *Per-scope cache on the querier.* Rejected: dies every request, zero cross-request benefit — the motivation is precisely the cross-request poll.
- *`Microsoft.Extensions.Caching.Memory` (`IMemoryCache`).* Rejected for now: introduces a dependency/pattern unused in this codebase; the cache has a single, well-known shape (runId → (etag, aggregate)) that a small self-contained singleton expresses more directly and testably. `IMemoryCache` remains a viable swap if bounding/compaction needs grow.

### D3. Validation by scalar ETag projection; correctness is read-time, not TTL-based

Each call reads the current `ETag` and compares. There is **no time-based expiry**: an entry is valid iff its stored `ETag` equals the row's current `ETag`. This is correct because ETag is the State version authority and increments on every actual write; staleness is impossible as long as each call re-reads the ETag.

**Trade-off.** Every call still issues the ETag scalar query (cheap, single-column, no `State`). This is acceptable: the ETag read is a tiny scalar `SELECT`, orders of magnitude cheaper than the LOH-allocating full deserialize it replaces. (Avoiding even the ETag read would require write-side invalidation — out of scope; see Open Questions.)

### D4. Bounded by entry cap with FIFO eviction; correctness preserved on eviction

The singleton caps its entry count; when the cap is exceeded it evicts oldest-inserted entries. A miss after eviction simply rebuilds (one deserialize) — correctness is never compromised because the ETag re-check governs validity, not presence in the cache. The hot run (polled every 3s) stays resident; only the long tail of browsed historical runs contend for eviction.

**Rationale.** The access pattern is one-hot-run-many-reads, so a small cap suffices. Terminal runs never change again, but detecting terminality requires reading the very status we are caching; FIFO avoids the circularity while still bounding memory.

**Alternatives.**
- *Terminal-aware eviction (drop entries once the run is terminal).* Deferred: needs a status signal available without re-deserializing; the `Status` computed column on the row could feed it, but that is a refinement, not required for correctness or the primary win.
- *`MemoryCache` with `SizeLimit` + compaction.* See D2 — viable but heavier than the shape warrants today.

### D5. Stampede under concurrent first-miss is accepted (correctness preserved)

Under N concurrent calls that all miss for the same `workflowRunId`/ETag (e.g. many runner reports landing before the first deserialize completes), `ConcurrentDictionary` may run the build factory more than once. Each call still returns a correct, equivalent aggregate; the only cost is a few redundant deserializes during the rare concurrent first-miss window. The dominant access pattern is a single poller per run, so stampede is uncommon.

**Alternative (not adopted now).** Per-key `Lazy<Task<WorkflowRun>>` / `SemaphoreSlim` to coalesce the first miss. Adopt only if measurements show stampede-driven regressions; it adds per-key allocation and lifecycle complexity not justified by the current access pattern.

## Risks / Trade-offs

- **[Stale definition/artifacts if mistakenly cached]** → D1 caches *only* the aggregate; definition and artifacts are re-resolved/re-attached every call, so live profile edits and new artifacts are always reflected. The spec's equivalence requirement is the regression gate.
- **[Shared mutable aggregate across scopes]** → The cached `WorkflowRun` is treated as a read-only shared snapshot. `BuildStatusView`/`AttachArtifactSummariesAsync` read it and produce fresh view objects (they mutate the *view*, never the run). A test/arch guard should assert no mutation path writes back to a cached aggregate.
- **[Shadow-property projection (`EF.Property`) portability]** → `EF.Property<long>(e, "ETag")` in a `Select` is the standard EF Core shadow-property projection; if a provider quirk blocks it, fall back to a tracked `FindAsync` + `Entry(...).Property<long>("ETag")` (one extra round-trip only on the path that already loads the row on a miss). Verify against SQLite EF provider.
- **[Stampede redundant deserialize]** → Accepted under D5; correctness preserved, rare given single-poller pattern.
- **[Memory regression from the cache itself]** → Bounded by D4 cap; the spec's boundedness requirement plus a cost test (design/testing.md §5) asserting entry-count ceiling gates it.
- **[Clock/TTL drift]** → None: D3 uses no wall-clock; validity is ETag-equality only, fully injectable/observable via the ETag value, satisfying the no-real-time testing rule.

## Migration Plan

- **Schema:** none. The `ETag` column and its increment/idempotency rules already exist and are unchanged. No EF migration, no data upgrader.
- **Code:** additive. New singleton cache type + `WorkflowQuerier.GetStatusAsync` reworked to read-ETag-then-(hit|miss). No public API or contract change; all callers (`WorkflowRoutes*`, `IssueGrain`, `WorkflowActivityQuerier`, `AgentActivityFeedAssembler`) compile and behave identically.
- **Rollout:** no feature flag required (pure optimization, equivalence-gated), but a kill switch (config to bypass the cache, falling back to today's unconditional deserialize) is cheap to add if a hotfix is ever needed.
- **Rollback:** revert the querier change + remove the singleton. State/ETag are untouched, so rollback is safe with no data consequence.
- **Verification:** spec tests (ETag-unchanged → zero `State` deserializes; ETag-changed → exactly one; equivalence to uncached; artifact freshness after a no-State-write artifact record; boundedness/eviction). Cost tests use a deserialization counter/seam (not wall-clock) per design/testing.md §5. Existing `StatusSpecs`/`WorkflowStructureSpecs` must pass unchanged to prove contract preservation.

## Open Questions

- **Exact entry cap value** for D4 — pick from measurement (typical active-run concurrency) rather than guessing; a few hundred likely covers the hot set. Tune after a profiling pass.
- **Should the ETag read itself be avoided?** Write-side invalidation (bump/evict on `WorkflowRunStore.SaveAsync`) would let a hit skip even the scalar ETag query. Out of scope here (touches the write path, which the issue explicitly excludes), but the natural follow-up if the ETag-query cost shows up under heavier fan-in.
- **Terminal-aware eviction** (D4 alternative) — defer until the simple cap is shown insufficient.
