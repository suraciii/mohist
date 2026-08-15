# Direct API activation boundary

### Requirement: A mapped direct route has a concrete durable behavior

The Server MUST NOT map an `/api/v1` endpoint whose normal successful path is
`501 Not Implemented`. A direct route MAY be activated only with its
Bearer-PAT authorization boundary, concrete handler, and durable public result
path in the same implementation slice.

#### Scenario: Grant prerequisite alone does not expose a route

- **GIVEN** PAT Project grants exist but no concrete public projection handler
  has shipped
- **WHEN** the Server is composed
- **THEN** it MUST NOT register a direct API placeholder endpoint
- **AND** it MUST NOT claim that the direct API is available

#### Scenario: First public route is vertical

- **GIVEN** the first direct read route is enabled
- **WHEN** an authorized caller requests it
- **THEN** the route authenticates and authorizes before resource lookup
- **AND** it returns only a persisted public projection or `503 projection_lag`
- **AND** it never returns a placeholder response

### Requirement: Public projection uses durable source positions

Every public snapshot and public event MUST be derived from a durable canonical
AgentJob or AgentSession source position composed of source kind, stable source
ID, and monotonic durable revision. A Runner notification, runtime session,
provider response, outbox delivery attempt, timestamp, or client cursor MUST
NOT define a source position.

#### Scenario: Duplicate durable delivery does not duplicate a public event

- **GIVEN** the projector has committed a public event for an AgentSession
  source position
- **WHEN** the same durable source position is delivered again
- **THEN** the projector MUST NOT append another public event or allocate
  another public sequence

#### Scenario: Undetermined source stays unprojected

- **GIVEN** an input cannot be associated with a durable source revision
- **WHEN** the projector receives it
- **THEN** it MUST NOT publish a public observation from that input
- **AND** it MUST NOT infer an outcome from a live runtime or Runner state

### Requirement: Snapshot, event, and checkpoint are atomically consistent

The projector MUST commit every affected public snapshot, public event,
source-position deduplication identity, source checkpoint, stream generation,
and next public sequence in one transaction. A public read MUST use the
persisted projection, not a partial canonical aggregate join.

#### Scenario: Crash before commit exposes no partial observation

- **GIVEN** a projection batch has consumed a canonical source position
- **WHEN** the process stops before its projection transaction commits
- **THEN** no snapshot, public event, checkpoint, or public sequence from that
  batch is visible
- **AND** restart may replay the same source position

#### Scenario: Crash after commit preserves one observation

- **GIVEN** a projection transaction has committed for a source position
- **WHEN** the process restarts and delivery repeats
- **THEN** the existing checkpoint and deduplication identity prevent a second
  public event or sequence

### Requirement: Projection freshness is source-position based

For a direct read, the Server MUST compare the route's required read-only
canonical source positions with the selected public snapshot checkpoint. A
route with an uncovered known required position MUST return
`503 projection_lag` and MUST NOT create or replay execution work. A durable
uncertain canonical fact becomes public `unknown` only after its source
position is checkpointed.

#### Scenario: New canonical fact waits for its projection

- **GIVEN** a Job source revision is newer than the selected snapshot
  checkpoint
- **WHEN** an authorized caller reads that Job
- **THEN** the Server returns `503 projection_lag`
- **AND** it does not combine the newer Job fact with older Session facts
- **AND** it does not launch, follow up, stop, or retry any execution

#### Scenario: Rebuild changes generation without reusing sequence

- **GIVEN** a Session public stream is rebuilt from durable source positions
- **WHEN** the rebuilt snapshot and checkpoints commit
- **THEN** the Server atomically selects a new stream generation
- **AND** an old-generation cursor is invalid
- **AND** the Session's public sequence allocator never reuses a value
