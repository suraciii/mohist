### Requirement: Status view served from cache without deserializing State when ETag is unchanged

The status query path (`WorkflowQuerier.GetStatusAsync`) SHALL first read the persisted `ETag` of the target `WorkflowRuns` row as a lightweight scalar projection (without materializing or deserializing `State`). When that `ETag` matches the version of an already-built status view for that run, the query SHALL return the cached view and MUST NOT deserialize the run's `State`.

#### Scenario: Repeated status reads with no State write between them
- **WHEN** `GetStatusAsync(workflowRunId)` is called twice in succession with no `WorkflowRunStore` write (and therefore no `ETag` increment) between the two calls
- **THEN** the second call MUST return a view equivalent to the first, and the run's `State` JSON MUST be deserialized zero times during the second call

#### Scenario: First read after process start with a stable ETag
- **WHEN** `GetStatusAsync` is called for a run whose `ETag` has not changed since the cached view was built, and the cache holds an entry for that `ETag`
- **THEN** the query MUST serve the cached view and MUST NOT read or deserialize the `State` column

### Requirement: Cache rebuilt exactly once when State changes

When the persisted `ETag` differs from the version of the cached entry (or no cached entry exists), the query SHALL deserialize `State` exactly once, rebuild the status view, and refresh the cache entry to the new `ETag`. Subsequent reads at the same new `ETag` MUST NOT deserialize `State` again.

#### Scenario: State write between two reads
- **WHEN** a `WorkflowRunStore` save increments the run's `ETag`, then `GetStatusAsync` is called
- **THEN** the query MUST deserialize `State` exactly once, return a view reflecting the written State, and store it under the new `ETag`

#### Scenario: Repeated reads after a single State change
- **WHEN** `ETag` increments once and `GetStatusAsync` is then called several times with no further write
- **THEN** `State` MUST be deserialized exactly once across those calls, and every call MUST return the rebuilt view

### Requirement: Cached status view equivalent to an uncached read

A status view served from the cache MUST be equivalent to the view a cache-free read would produce for the same `ETag` and the same artifacts: identical `Status`, `CurrentStage`, stages, tasks, checks, approval state, failure, pending work, and artifact summaries. The cache is a performance optimization only; it MUST NOT alter observable content.

#### Scenario: Cached view matches a freshly built view
- **WHEN** a status view is produced by a cache hit for `ETag` N, and another view is produced by forcing a full rebuild at the same `ETag` N
- **THEN** the two views MUST be equal field-by-field

### Requirement: External status contract shape preserved

`GetStatusAsync` SHALL continue to return `WorkflowStatusView?` with the same fields and the same null/return semantics it has today: it returns `null` when the run row does not exist or when no status view can be built from the run and its definition. The introduction of caching MUST NOT change the type, fields, or nullability of the returned contract, and MUST NOT require any calling site (API routes, `IssueGrain`, `WorkflowActivityQuerier`, `AgentActivityFeedAssembler`) to change.

#### Scenario: Unknown run id
- **WHEN** `GetStatusAsync` is called for a `workflowRunId` with no `WorkflowRuns` row
- **THEN** it MUST return `null` and MUST NOT create a cache entry

### Requirement: Artifact summaries reflect the latest artifacts

Artifacts are persisted in a table separate from `State` and their addition or change does not advance the State `ETag`. The status query SHALL NOT return artifact summaries that are stale relative to the artifact table: a view served for a run MUST reflect artifacts that exist at the time of the query, including artifacts added after the last State write. The cache invalidation contract MUST cover both the State `ETag` dimension and the artifact dimension.

#### Scenario: Artifact recorded after the last State write
- **WHEN** an artifact is recorded for a task run after the most recent `WorkflowRunStore` save (State `ETag` unchanged), then `GetStatusAsync` is called
- **THEN** the returned view's artifact summaries MUST include that artifact

#### Scenario: Artifact recorded then status read twice
- **WHEN** an artifact is recorded (no State write) and `GetStatusAsync` is called twice
- **THEN** both calls MUST return artifact summaries that include the artifact, and `State` MUST still be deserialized only to the extent required by the State-keyed portion of the cache (not re-deserialized solely because artifacts changed)

### Requirement: Cache must not reintroduce unbounded memory growth

This change exists to reduce per-read allocation on a high-frequency path. The cache SHALL be bounded so that it does not itself become a source of unbounded process memory growth: it MUST NOT retain status views for an unbounded set of runs indefinitely, and entries for a run whose status can no longer change MUST be eligible for eviction. Correctness (equivalence to an uncached read) MUST hold regardless of eviction, since a miss is always recoverable by a single rebuild.

#### Scenario: Terminal run entry is evictable
- **WHEN** a run reaches a terminal status and many other runs are subsequently queried
- **THEN** the cache MUST be able to evict the terminal run's entry without violating correctness, and a later read for that run MUST still return an equivalent view (rebuilt on the miss)

#### Scenario: Eviction never changes observable content
- **WHEN** a cache entry is evicted and the same `ETag` is queried again
- **THEN** the rebuilt view MUST be equivalent to the view that was served before eviction
