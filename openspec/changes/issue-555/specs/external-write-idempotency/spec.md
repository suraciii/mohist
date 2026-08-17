### Requirement: Every write carries a usable Idempotency-Key

Every launch, follow-up, and stop request MUST carry an `Idempotency-Key`
header: an opaque caller string of 1 through 128 printable ASCII characters.
A write without a key SHALL return `400 idempotency_key_required`; a key
outside that form SHALL return `400 idempotency_key_invalid`. Neither
response MAY create a request mapping or any domain record. The key is a
header, never a JSON field, trace ID, or Input ID.

#### Scenario: Missing key is rejected before admission

- **WHEN** a launch POST omits the `Idempotency-Key` header
- **THEN** the response is `400 idempotency_key_required`
- **AND** no Job, Session, Input, Turn, or mapping row is created

#### Scenario: Malformed key is rejected

- **WHEN** a stop POST sends a 200-character `Idempotency-Key`
- **THEN** the response is `400 idempotency_key_invalid`
- **AND** no stop operation or mapping is created

### Requirement: Write bodies are minimal and validated before admission

Launch and follow-up bodies SHALL accept only a required, non-empty `text`
JSON string. Invalid JSON, unknown properties, duplicate JSON property names,
and missing or empty `text` SHALL fail with `400 invalid_request` before
admission; attachments, context references, and caller-selected execution
options MUST NOT be silently accepted or ignored. The stop body SHALL be
empty. The caller MUST NOT place a Project, Agent, or other derived value in a
write body; a follow-up's Project and Agent are derived from the canonical
Session.

#### Scenario: Unknown property is rejected

- **WHEN** a launch body contains `text` and an `attachments` property
- **THEN** the response is `400 invalid_request`
- **AND** no canonical record or mapping is created

#### Scenario: Duplicate JSON property names are rejected

- **WHEN** a follow-up body contains two `text` properties
- **THEN** the response is `400 invalid_request` before admission

#### Scenario: Follow-up derives Project and Agent from the Session

- **WHEN** a follow-up targets Session `session_123` of Project `proj_a` and Agent `agent_1`
- **THEN** the Server resolves Project and Agent from the canonical Session and the request fingerprint contains only canonical route IDs and the accepted body

### Requirement: The Server computes the normalized fingerprint

The Server MUST parse the accepted JSON once into a versioned canonical
representation and compute the request fingerprint itself. The text value is
preserved exactly as a JSON string after parsing; the Server MUST NOT trim,
case-fold, or otherwise make two distinct prompts equivalent. Canonical JSON
property ordering and the route's canonical IDs make the representation
deterministic. The caller never submits a hash, and the fingerprint and raw
request MUST NOT be exposed as public output.

#### Scenario: Distinct prompts never collide

- **WHEN** the same launch scope and key is retried with body text `Fix the bug` after `Fix the bug `
- **THEN** the fingerprints differ and the retry returns `409 idempotency_key_reused`

#### Scenario: Identical accepted body replays deterministically

- **WHEN** the same launch request is replayed with the identical accepted body after the original response was lost
- **THEN** the Server computes the same fingerprint and returns the original mapping

### Requirement: Durable keyed mappings are scoped per command

The Server SHALL persist one durable idempotency mapping per accepted write,
scoped as follows, and the mapping MUST be durable before a successful command
response:

- Launch: scope `(projectId, agentId, Idempotency-Key)`; fingerprint input is the contract version, the launch discriminator, canonical `projectId`, canonical `agentId`, and the complete accepted body.
- Follow-up: scope `(sessionId, Idempotency-Key)`; fingerprint input is the contract version, the follow-up discriminator, canonical `sessionId`, and the complete accepted body.
- Stop: scope `(turnId, Idempotency-Key)`; fingerprint input is the contract version, the stop discriminator, canonical `turnId`, and the empty body. The durable stop mapping MUST additionally bind `callerKeyId`, canonical `projectId`, `sessionId`, and `turnId`, so one caller cannot look up or replay another caller's public key.

The first launch under a mapping creates at most one canonical
Job/Session/Input/Turn group; a follow-up creates at most one canonical
Input/Turn pair.

#### Scenario: One launch per key

- **WHEN** a launch with key `k1` completes and is retried three times with the same body
- **THEN** exactly one canonical Job/Session/Input/Turn group exists for that scope

#### Scenario: Stop keys are caller-bound

- **WHEN** caller B replays caller A's stop key for the same Turn
- **THEN** caller B's request is evaluated against caller B's own durable mapping, not caller A's

### Requirement: Replay returns the original mapping and current observation

A retry with the same scope, key, and fingerprint SHALL return `200` with the
original canonical Job/Input/Turn mapping and its current public observation.
The canonical mapping is stable: public status, timestamps, output, error, and
event sequence may advance as the Server learns more facts, but a matching
retry MUST NOT mint different IDs or another execution, create a new queue
entry, outbox item, public event, or external effect.

#### Scenario: Lost launch response is recovered by retry

- **WHEN** a launch succeeds but the caller never receives the response and repeats the POST with the same key and body
- **THEN** the retry returns `200` with the same Job, Session, Input, and Turn IDs and their current public observation

#### Scenario: Follow-up retry returns the same Input/Turn pair

- **WHEN** a follow-up with key `k2` is retried after success
- **THEN** the retry returns the same Input and Turn IDs and no second Input or Turn exists

### Requirement: Key reuse with a different payload conflicts

The same durable scope and key supplied with a different normalized payload
SHALL return `409 idempotency_key_reused`. The conflict is stable across
retries and MUST NOT create a new canonical record, public event, queue entry,
outbox item, or external effect.

#### Scenario: Different body under the same key is rejected

- **WHEN** a launch with key `k1` and text `task A` is retried with key `k1` and text `task B`
- **THEN** the response is `409 idempotency_key_reused`
- **AND** no second Job, Session, Input, Turn, or public event is created
- **AND** repeating the conflicting request returns the same conflict

### Requirement: Definitive admission rejections are durable

A well-formed keyed launch or follow-up that receives a definitive admission
rejection SHALL return `200` with `status=terminal` and `outcome=rejected`
plus a safe public error, and the rejection MUST be durable under the same
key. Replays with the same key and payload SHALL return the same rejection; a
capacity recovery, reconnect, or retry MUST NOT turn a rejected request into a
newly accepted one or invent an Input or Turn later. A rejection that
intentionally created no live record returns null Input and Turn IDs.

#### Scenario: Rejected launch replays as the same rejection

- **WHEN** a launch is durably rejected for a full queue and the caller retries with the same key and body after capacity frees up
- **THEN** the retry returns `200` with `status=terminal`, `outcome=rejected`, and the same safe public error
- **AND** no Job execution, Input, or Turn is created for that key

### Requirement: Unresolved stop outcome blocks supersession

A stop request SHALL durably map its key to one canonical per-target stop
operation before any Runner effect, with the Server freezing the target
revision, context generation, binding, and deadline. While the original stop's
outcome is `unknown`, a different key targeting the same Turn SHALL return
`409 stop_outcome_unknown` and MUST NOT supersede or replay the unresolved
effect. A matching retry SHALL resolve the same mapping, operation, and
outcome, and MUST NOT reread the current binding to create a replacement
deadline or effect. A Turn already terminal at the first request produces a
durable no-op observation with no Runner call; a queued Turn ends locally and
is recorded cancelled without contacting Runtime.

#### Scenario: Second key cannot supersede an unresolved stop

- **WHEN** a stop with key `s1` is issued and its outcome is still `unknown`, and a stop with key `s2` targets the same Turn
- **THEN** the response is `409 stop_outcome_unknown`
- **AND** no new stop operation or external effect is issued

#### Scenario: Matching stop retry never re-effects

- **WHEN** a stop with key `s1` whose outcome is already known is retried with key `s1`
- **THEN** the retry returns the original target Turn observation
- **AND** the Server does not reread the current binding or issue a second stop effect

#### Scenario: Stopping an already-terminal Turn is a durable no-op

- **WHEN** a stop arrives for a Turn that is already terminal
- **THEN** the response is `200` with the durable Turn observation and no Runner call is made
