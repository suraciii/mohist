# Design: Direct API activation and projection checkpoint

## Activation boundary

The Project grant is an authorization prerequisite, not evidence that the
external API is available. A direct route becomes public only when its handler
can return the specified public result from durable state. The Server must not
map an endpoint whose normal successful path is `501 Not Implemented`.

The first vertical slice is a read path. It includes all of the following in
one reviewable change:

1. Bearer-only caller resolution, scope checks, and Project-grant checks
   before resource lookup.
2. A durable public projection and its checkpointed projector.
3. One concrete read handler that returns an allowlisted public observation or
   `503 projection_lag`.
4. Tests proving authorization has no execution effect, projection recovery,
   and no placeholder endpoint is exposed.

Write routes follow only after that slice. Each write adds its own durable
idempotency record and a concrete handler in the same change; it is never
enabled merely because a route template or shared middleware exists.

### First route boundary

The first mapped route is `GET /api/v1/projects/{projectId}/agent-jobs/{jobId}`.
It is deliberately a Job-only read, not the `PublicExecutionRead` join
described for later launch and Session routes. The AgentJob owner ledger already
has one stable source identity and an optimistic, monotonically increasing
revision. Its writer commits a strict allowlisted Job snapshot and that source
revision in the same database transaction as the canonical ledger state.

The direct handler first checks a Bearer PAT's persisted grant against the
canonical `projectId` path segment, then reads only that persisted snapshot.
It checks the Job ledger revision as read-only metadata. A missing or stale
snapshot is `503 projection_lag`; the handler never serializes the canonical
ledger JSON as a fallback. This provides a recoverable read boundary without
claiming that Job and Session facts are atomically joined.

The Job snapshot deliberately omits output and raw errors. It exposes only
safe status, terminal outcome/reason category, IDs, and timestamps. A later
Session-aware projection will use its own source positions, checkpoint set,
event deduplication, and stream generation; it must not extend this route by
reading the Session live.

## Source contract

The public projector has two canonical source families in v1:

- `agent-job`, identified by the canonical Job ID.
- `agent-session`, identified by the canonical Session ID.

Each projected transition carries a `SourcePosition`:

```text
sourceKind + sourceId + revision
```

`revision` is the source aggregate's durable monotonic event revision. It is
not a timestamp, Runner connection generation, runtime-session identifier,
outbox delivery attempt, in-memory notification, or client cursor. A durable
outbox may schedule projector delivery, but the originating
`SourcePosition` remains the deduplication identity and authority.

A projector input that cannot name a durable source revision is not eligible
for public projection. It remains pending until the canonical source can be
read; it must not be guessed from provider output, Runner logs, or a live
session.

## Snapshot and checkpoint contract

Every public target has a persisted current snapshot. A launch target is
anchored by Job ID before a Session exists; once a Session is accepted, the
same target gains Session, Input, and Turn references. A Session stream has a
persisted generation and a strictly increasing public sequence allocator.

For a projection batch, one database transaction commits:

1. the allowlisted snapshot changes;
2. any normalized public event entries and their source-position
   deduplication identities;
3. each consumed source checkpoint; and
4. the affected stream generation and next public sequence.

The checkpoint is a set of `SourcePosition` values, not one wall-clock value
or a global "up to date" flag. A snapshot records the source positions it
includes. This is necessary because one public observation may depend on both
the Job and Session source families.

If a transaction does not commit, it publishes none of those four facts and
the same source positions may be replayed. If it commits, a replay observes
the source-position deduplication record and cannot allocate another public
event or sequence. The transaction projects observations only; it never
reissues launch, follow-up, stop, Runner, or provider effects.

## Read freshness

A route derives the source positions required for its requested Job, Input,
Turn, or Session from a read-only canonical metadata lookup. It compares those
positions with the persisted snapshot checkpoint:

- A checkpoint covering every required position permits the route to return
  the persisted public snapshot.
- A known required position beyond the checkpoint returns
  `503 projection_lag` with no admission, idempotency, or execution effect.
- A durable canonical fact that itself says an execution cannot be confirmed
  is projected as the public `unknown` state only after its source position is
  checkpointed. Projection lag is not `unknown`.

The route never combines fresh Job fields with stale Session fields. It never
serializes a canonical read model to fill an incomplete projection.

## Generation and rebuild

A normal restart retains the current stream generation and checkpoints. A
rebuild creates a new generation from durable source positions, commits its
snapshot and checkpoints, and atomically selects it as current. An old cursor
cannot select a new generation; it is invalid rather than silently resumed.
The global public sequence allocator remains monotonic across generations.

## Implementation order

1. Keep `/api/v1` unmapped while only the grant prerequisite exists.
2. Add the projection schema, source-position reader, atomic projector, and
   public allowlist contract.
3. Add the Bearer-PAT boundary and one concrete public read route in the same
   slice, including freshness and recovery tests.
4. Add launch idempotency and its concrete route.
5. Add follow-up, fenced stop, and the Session event stream as independent
   vertical slices.

This order preserves the existing control-plane API and avoids treating a
mapped `501` route as an implementation milestone.
