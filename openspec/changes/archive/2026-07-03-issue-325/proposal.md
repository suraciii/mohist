## Why

Every Issue-query edit today means opening one ~2400-line `IssueQuerier` and hunting across six unrelated responsibilities (read-model query, metrics aggregation, event sourcing, config derivation, mapping, persistence loaders). The two biggest concerns — synchronous read-model query and analytics aggregation — never call each other, yet live in one class, and the same "load a project's issues then map them to read models" block is copied verbatim across 5 call sites while the "scan `IssueEvents` by project source" loop is copied across 4, so a single change must be mirrored in up to 5 places or the copies drift apart. This is safe to do now because the read-model and metrics paths are provably orthogonal (no cross-calls, no shared mutation), and it is the next step of the "converge the super-large services to single responsibility" epic.

## What Changes

- Extract a dedicated `IssueMetricsQuerier` that owns all analytics aggregation — completion buckets, quality, approval-wait, delivery time, and stage durations — together with their result records, the `CompletionBucket` enum, and the private accumulator/helper types. `IssueQuerier` keeps only read-model queries (list, detail, enrichment, the workflow-run → issue reverse lookup) and its read-model mapping surface.
- Consolidate the duplicated "load a project's issues → map to read models" prelude into one shared method, consumed by all 5 current call sites (2 read-model, 3 metrics).
- Consolidate the duplicated "scan `IssueEvents` by project source" loop into one shared method, consumed by all 4 current call sites (completion, quality, delivery-time, stage-duration).
- Merge the near-identical `ToInfo` read-model mapping overloads into a single consolidated path.
- Keep a single median implementation; the inline copy in the approval-wait path delegates to the shared method.
- Remove the deprecated label-format normalization branch from `IssueStore` deserialization (no version compatibility required).
- **BREAKING** (source, same-assembly): metrics result types move from `IssueQuerier.*` to `IssueMetricsQuerier.*`; API route partials and tests update their references.
- **BREAKING** (persisted data): Issue deserialization accepts only the current label object format; the legacy array-format labels are no longer normalized.
- No change to any aggregation formula, read-model/DTO field shape, the API route partial file structure, or any HTTP contract — only how results are constructed and where the code lives.

## Capabilities

- `issue-read-model-queries`: The Issue read-model query service owns only synchronous read-model concerns — list, detail, enrichment, and the workflow-run → issue reverse lookup. It contains no metrics aggregation method and no metrics result/accumulator type. Its read-model mapping surface is a single consolidated path (the near-duplicate `ToInfo` overloads merged), not four copies.
- `issue-metrics-aggregation`: A dedicated issue metrics service owns all analytics aggregation — completion buckets, quality, approval-wait, delivery time, and stage durations — with their result records, the `CompletionBucket` enum, and the private accumulator/helper types. Its repeated internal patterns are single-implementation and shared across the metrics methods: the "scan `IssueEvents` by project source" loop, the workflow-run load-and-pair logic, and the median calculation (the approval-wait inline copy delegates to the shared method). No aggregation result changes.
- `issue-query-shared-loading`: The "load a project's issues and map them to read models" prelude is defined exactly once and consumed by all of its call sites spanning both the read-model list/approval-gate paths and the metrics quality/approval-wait/stage-duration paths, eliminating the five copy-pasted blocks. It is a cross-service shared helper, not duplicated per caller.
- `issue-persistence-legacy-cleanup`: Issue deserialization carries no deprecated label-format normalization path. The persistence layer accepts only the current label object format; the legacy array-format branch and its `legacyLabelsDiscarded` surfacing are removed.

## Impact

- **Server** (`packages/server`):
  - `Issue/Services/IssueQuerier.cs` — loses the 5 metrics methods, their records/enum, private metrics helpers/types, and the metrics-only const/static-array fields; keeps read-model queries; gains the consolidated `ToInfo` mapping and shared load/event-scan helpers.
  - `Issue/Services/IssueMetricsQuerier.cs` (new) — receives the migrated metrics methods, types, and internal collaboration.
  - `Infrastructure/Data/Issue/IssueStore.cs` — remove `NormalizeLegacyLabels` and the `legacyLabelsDiscarded` deserialization overload.
  - DI registration — `IssueMetricsQuerier` implements `IScopedService` and is registered conventionally via the assembly scan (`AddMohistConventionalServices`), matching `IssueQuerier`'s lifetime; add one scoped theory row to `MigratedServicesRegistrationSpecs`.
  - `Api/IssueRoutes.*Metrics.cs` (5 partials) — repoint metrics result type references to the new service/namespace.
- **Tests** (`packages/server/tests`):
  - `Specs/Issue/Querier/IssueQuerierSpecs.cs` (~3600 lines, mixed) — split the metrics specs out to a metrics-service spec file; read-model specs stay.
  - `Specs/Issue/Api/IssueMetricsApiSpecs.cs` — update `IssueQuerier.X` seed-constant references to the new home.
  - `Specs/Foundation/MigratedServicesRegistrationSpecs.cs` — assert the new metrics service is scoped-registered.
  - `ThrowingIssueQuerier` stub (Epic specs) — keep constructor-compatible or update.
- **Web / runner / CLI**: none. No HTTP contract, DTO, or read-model field shape changes.
- **Risk** (medium): the type-namespace move is source-breaking within the assembly but behavior-preserving at runtime; the orthogonality (zero cross-calls between read-model and metrics) makes the split regression-free in principle, the main risk being an overlooked call site or a shared helper subtly altering one duplicated block's edge behavior — covered by keeping every existing query and metrics spec green.
