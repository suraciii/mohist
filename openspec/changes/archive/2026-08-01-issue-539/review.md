# Review — Issue 539 (WorkflowRun status ETag cache)

Reviewer role: reviewer, not fixer. Judging the change in commit `5643fd1a8`
("T-001 Add WorkflowRun status aggregate cache") against issue #539's
acceptance criteria and the plan artifacts under
`openspec/changes/issue-539/`.

## Verdict: PASS

The change faithfully implements the spec/design, every acceptance criterion is
met and covered by a test, the build is warning-clean, and the targeted test
classes (existing + new) pass. No must-fix problems found. Two non-blocking
observations are recorded at the end.

## Artifacts reviewed

- `openspec/changes/issue-539/proposal.md`, `design.md`, `tasks.json`,
  `specs/workflow-run-status-cache/spec.md`, `self-review.md`, `progress.txt`.
- Issue #539 body (Motivation/Scope/References).
- Changed production code:
  - `packages/server/src/Mohist.Server/Workflow/Services/WorkflowQuerier.cs`
    (reworked `GetStatusAsync`; new `LoadAndCacheAsync`; `Hydrate` now takes a
    deserialize delegate; non-status paths keep the static
    `DeserializeWorkflowRun`).
  - `packages/server/src/Mohist.Server/Workflow/Services/WorkflowRunStatusCache.cs`
    (new bounded FIFO singleton).
  - `packages/server/src/Mohist.Server/Workflow/Services/WorkflowRunDeserializer.cs`
    (new deserialization seam; singleton + interface forward-registration).
  - `packages/server/src/Mohist.Server/Infrastructure/Hosting/MohistServiceRegistration.cs`
    (`IWorkflowRunDeserializer` → `WorkflowRunDeserializer` forward).
- Changed tests: `WorkflowRunStatusCacheSpecs.cs` (new, 7 specs),
  `WorkflowRunStatusCacheTests.cs` (new unit), `WorkflowRunQuerierSpecs.cs`
  (real-`IWorkflowRunStore.SaveAsync` spec + counting deserializer),
  `WorkflowStructureSpecs.cs`, `WorkflowGrainSpecs.cs`,
  `WorkflowGrainTestHelpers.cs`, `AgentStatusHistoryBoundedFixture.cs`
  (constructor-arity updates for the new dependencies).
- Cross-checked against the ETag increment rule in
  `WorkflowRunStore.StageRunAsync` (`WorkflowRunStore.cs:156,166`) and the
  conventional-service registration in `ServiceCollectionExtensions.cs`.

## Verification performed

- `npm run build` → `Build succeeded. 0 Warning(s) 0 Error(s)`
  (`TreatWarningsAsErrors` acts as lint; clean).
- `dotnet run --project ...SpecTests -- -class WorkflowRunStatusCacheSpecs`
  → 7/7 pass (2.09s).
- `dotnet run --project ...UnitTests -- -class WorkflowRunStatusCacheTests`
  → 1/1 pass (0.069s).
- `dotnet run --project ...SpecTests -- -class WorkflowStructureSpecs -class WorkflowRunQuerierSpecs`
  → 11/11 pass (3.16s).
- `dotnet run --project ...SpecTests -- -class StatusSpecs` → 6/6 pass (3.37s).
- `git status` clean before and after verification (no Git-visible leftovers).

## Acceptance-criteria trace

| # | Criterion (tasks.json T-001) | Covered by | Result |
|---|---|---|---|
| 1 | Two successive reads, no write → 2nd typed deserialize 0× | `RepeatedStatusReadsReuseTheAggregateWithoutDeserializingAgain` (count==1) | ✓ |
| 2 | Save bumps ETag → next deserialize exactly once; later reads 0× | `StateWriteChangesTheEtagAndRebuildsTheCachedAggregateOnce` (count==2) + `StatusCacheRebuildsAfterWorkflowRunStoreSave` (real store, count==2) | ✓ |
| 3 | Cache-hit view field-by-field equal to forced rebuild at same ETag | `CacheHitAndForcedRebuildProduceEquivalentViews` (JSON equality) | ✓ |
| 4 | Unknown run → null, no cache entry | `UnknownRunReturnsNullWithoutCreatingAnEntry` (count==0, cache.Count==0) | ✓ |
| 5 | Artifact recorded after last State write appears; 2 reads; deserialize not triggered by artifact change | `ArtifactAddedWithoutStateWriteStaysFreshWithoutRebuild` (count==1 across 3 reads) | ✓ |
| 6 | Bounded entry cap + eviction; post-eviction read equivalent | `EvictionRebuildsAnEquivalentView` (spec, capacity:1) + `StoreEvictsTheOldestEntryWhenCapacityIsReached` (unit, capacity:2) | ✓ |
| 7 | Cached `WorkflowRun` not mutated by `BuildStatusView`/`AttachArtifactSummariesAsync`/per-call paths | `PerCallStatusAssemblyDoesNotMutateTheCachedAggregate` (serializes cached run before/after a read with artifact attach) | ✓ |
| 8 | Existing StatusSpecs/WorkflowStructureSpecs pass; no caller change | StatusSpecs 6/6, WorkflowStructureSpecs green; all `new WorkflowQuerier(` / `CountingWorkflowQuerier` base-call sites updated | ✓ |
| 9 | New specs colocated with querier status tests; cost asserts use counts, not wall-clock | `WorkflowRunStatusCacheSpecs` under `Specs/Workflow/Querier/`; `CountingDeserializer.Count` is the probe everywhere | ✓ |
| 10 | `npm run build` 0 warnings; server tests pass | Build clean; targeted classes green | ✓ |

## Design-alignment checks

- **D1 (cache aggregate only).** `WorkflowRunStatusCache` stores
  `(workflowRunId) → (ETag, WorkflowRun)`. `GetStatusAsync` still runs
  `WorkflowDefinitionResolver.LoadTemplateAsync`, `BuildStatusView`, and
  `AttachArtifactSummariesAsync` per call on every path (hit and miss) — live
  profile edits and artifact freshness preserved. Confirmed by reading
  `WorkflowQuerier.cs:41-64`.
- **D2 (singleton injected into scoped querier).** `WorkflowRunStatusCache` is
  `ISingletonService` (auto-registered `AsSelf()`); `WorkflowQuerier` remains
  `IScopedService` and consumes the singleton. Cross-request poll reuses a warm
  entry. `IWorkflowRunDeserializer` is also singleton with an explicit interface
  forward-registration in `MohistServiceRegistration.cs:71-72`, matching the
  established pattern (cf. `IAgentLauncher` at `:95`).
- **D3 (ETag-equality validation, no TTL/wall-clock).** `TryGet` compares the
  stored `ETag` to the row's current `ETag` only; no time-based expiry. Tests
  assert via `CountingDeserializer.Count`, never timing — satisfies
  `design/testing.md` §5.
- **D4 (bounded FIFO).** Cap default 256; FIFO via a sequence-stamped queue.
  Re-store of an existing key preserves the original sequence (no re-enqueue),
  so eviction-by-oldest-insertion is correct; the sequence-match guard in
  `EvictIfNeeded` makes stale queue entries a no-op. No unbounded queue growth.
- **D5 (stampede accepted).** The implementation actually serializes first-miss
  builds under the cache lock, so it is no worse than (arguably better than) the
  design's "may run the factory more than once" framing. Correctness preserved.
- **Non-goals respected.** `WorkflowRunStore` and the ETag increment rule are
  untouched; no EF migration; no status-contract change; other full-load reads
  (`GetWorkspaceAsync`, `GetRepositoryContextAsync`,
  `HasIncompleteTaskWithUsesAsync`, `HasIncompleteTaskByIdAsync`) bypass the
  cache and use the static `DeserializeWorkflowRun`.

## Correctness reasoning for the read path

- **Unknown run.** Scalar ETag projection yields `(long?)null` → early `return
  null` before touching the cache (`WorkflowQuerier.cs:45-49`). No entry
  created.
- **Row deleted between ETag read and full-row load.** `LoadAndCacheAsync`
  re-checks `row is null` and returns null without storing
  (`WorkflowQuerier.cs:71-73`). Caller returns null.
- **TOCTOU between ETag read and full-row load.** If a concurrent save bumps
  ETag N→N+1 in that window, `LoadAndCacheAsync` loads State@N+1 but stores it
  under the stale key N. The served view is built from State@N+1 (the *latest*,
  so not stale); the next read sees ETag N+1 ≠ cached N, misses, and
  self-corrects. Worst case: one redundant rebuild. No stale data is ever
  served. Consistent with the design's "correctness preserved" framing.
- **Read-only snapshot invariant.** `BuildStatusView` only *reads* `run`
  (`WorkflowStatusMapper.cs:52-108`); `AttachArtifactSummariesAsync` mutates
  `view.Stages[].Tasks[i]` (view objects), never the `WorkflowRun`;
  `LoadTemplateAsync` takes `workflowRunId` (string) and cannot touch the
  cached aggregate. The cached entry is safely shareable across scopes.
- **`EF.Property<long>(e, "ETag")` in a `Select`.** Empirically works on the
  SQLite provider — every spec exercises it and passes (resolves the design's
  shadow-property-portability risk note).

## Non-blocking observations (do not block merge)

### O1 — Cache uses `Dictionary` + `lock`, not `ConcurrentDictionary`

Design D2 suggested a bounded `ConcurrentDictionary`. The implementation uses a
plain `Dictionary` under a single `lock` with a sequence-stamped FIFO queue.
This is a justified, benign divergence: `ConcurrentDictionary` has no native
bounded-FIFO story, and the lock makes eviction atomic and simple. Under the
documented one-hot-run access pattern the lock is effectively uncontended, and
under concurrent first-miss it *reduces* stampede versus D5's acceptance. The
spec does not mandate the data structure, so this is not a spec violation. A
future implementer tuning for high fan-in may revisit (e.g. per-key
`SemaphoreSlim` coalescing, also called out in D5 as deferred).

### O2 — `IWorkflowRunDeserializer` forward-registration lacks the neighbor comment

`MohistServiceRegistration.cs:71-72` adds the interface forward-registration
without the explanatory comment that the neighboring forward-registrations
carry (e.g. `:89-94` for `IAgentLauncher`, `:102-115` for the cleanup
forwarders). The pattern is identical and self-evident from context, so this is
cosmetic consistency only.

<promise>PASS</promise>
