### Requirement: Required idempotency key

Every direct launch, follow-up, and Turn-stop write MUST contain exactly one `Idempotency-Key` header containing 1 through 128 printable ASCII characters. The key MUST be treated as opaque caller data, MUST NOT be accepted from a JSON body or used as a trace ID or Input ID, and MUST be rejected when missing, duplicated, malformed, or outside the length or character bounds.

#### Scenario: Missing or invalid key
- **WHEN** a write omits `Idempotency-Key`, supplies more than one value, or supplies an invalid value
- **THEN** the Server returns `400 idempotency_key_required` or `400 idempotency_key_invalid` and creates no request mapping, domain record, queue entry, outbox item, or external effect

### Requirement: Server-computed request fingerprint

The Server MUST parse each accepted body once and compute a versioned fingerprint from the complete accepted request, canonical route IDs, command kind, and canonical scope. It MUST preserve the submitted text string exactly after JSON parsing and MUST NOT trim, case-fold, or otherwise make distinct text equivalent. The Server MUST persist the fingerprint but MUST NOT expose the fingerprint, raw request, or a caller-supplied hash. Project and Agent values for a follow-up MUST be derived from the canonical Session rather than accepted from the body.

#### Scenario: Same text with different surrounding whitespace
- **WHEN** two launch requests use the same key but their parsed text values differ by leading or trailing whitespace
- **THEN** the Server computes different fingerprints and treats the second request as a conflicting key reuse

#### Scenario: Follow-up attempts to change ownership
- **WHEN** a follow-up body includes a Project or Agent value that differs from the canonical Session or tries to influence a derived value
- **THEN** the request is invalid and cannot create a new Input, Turn, mapping, or external effect

### Requirement: Caller and resource isolation

Durable mappings MUST bind the caller credential identity in addition to the operation scope. A launch mapping MUST be isolated by caller, Project, Agent, and key; a follow-up mapping MUST be isolated by caller, canonical Session, and key; and a stop mapping MUST be isolated by caller, Project, Session, Turn, and key. Reusing a convenient key in another authorized scope MUST NOT resolve an unrelated operation.

#### Scenario: Key reused by another caller
- **WHEN** two PATs use the same key and equivalent launch content for the same Project and Agent
- **THEN** each caller receives an independent mapping and the second caller cannot observe or replay the first caller's canonical identities

#### Scenario: Key reused for another resource
- **WHEN** one caller reuses a follow-up or stop key for a different Session or Turn
- **THEN** the Server treats the request as a separate scope and never redirects it to the earlier Input, Turn, or stop operation

### Requirement: Durable matching replay

The first accepted mapping MUST be durable before a successful command response. When the required public projection checkpoint is available, a retry with the same caller, scope, key, and fingerprint MUST return the original canonical identities and the latest public observation for that mapping. If the mapping is known but its required public projection is behind, the retry MUST return `503 projection_lag` without creating a new effect. Matching retries MUST NOT mint another Job, Session, Input, Turn, queue entry, dispatch attempt, stop operation, or external effect.

#### Scenario: Launch response is lost
- **WHEN** the Server durably accepts a launch and the caller loses the response before learning the Job ID
- **THEN** repeating the launch with the original key and identical body returns the same Job anchor and current public observation rather than starting replacement work

#### Scenario: Follow-up response is lost
- **WHEN** a follow-up has been accepted but its response is lost
- **THEN** repeating the same Session/key/body returns the original Input/Turn mapping or durable rejection and does not append another Input

### Requirement: Conflicting key reuse

When a caller reuses a durable scope and key with a different normalized fingerprint, the Server MUST return `409 idempotency_key_reused`. The conflict MUST be stable and MUST create no new canonical record, public event, queue entry, outbox item, or external effect.

#### Scenario: Changed launch payload
- **WHEN** a launch key already maps to accepted work and the caller submits different text or another accepted field
- **THEN** the Server returns `409 idempotency_key_reused` and the original Job, Session, Input, and Turn remain the only records for that key

#### Scenario: Changed stop request
- **WHEN** a stop key is reused with a request that does not match the original canonical stop command
- **THEN** the Server returns `409 idempotency_key_reused` and does not issue another stop effect

### Requirement: Durable rejection and unknown handling

A definitive admission rejection MUST remain attached to its key and MUST be returned as the same terminal public decision on matching retries. Capacity recovery, reconnection, or a later retry with the same key MUST NOT convert that rejection into new work. An unresolved `unknown` outcome MUST remain queryable through the original mapping, and polling or reconnecting MUST NOT automatically replay the original command.

#### Scenario: Definitive capacity rejection
- **WHEN** a keyed launch or follow-up is rejected before acceptance because admission is definitively unavailable
- **THEN** the Server persists the rejection, returns a terminal rejected observation, and returns the same rejection for later matching retries even after capacity becomes available

#### Scenario: Unknown outcome after response loss
- **WHEN** the Server cannot confirm whether an external effect completed
- **THEN** the original mapping remains `unknown`, the caller is directed to observe it or retry only the same key, and no new key automatically starts replacement work

### Requirement: Fenced stop replay and terminal race

The first stop request for a Turn MUST durably freeze the target revision, context generation, complete binding or explicit null binding, and stop deadline before any external effect. A matching retry MUST use that frozen target and mapping. A terminal execution result and a stop result MUST compete through one terminal fence; the first durable terminal fact MUST win, and a late result MUST NOT rewrite the public outcome, output, error, or event sequence.

#### Scenario: Repeated stop after success
- **WHEN** a stop request completes and the caller repeats the same Turn/key after a response loss
- **THEN** the Server returns the original Turn observation without contacting the Runner or issuing a second stop

#### Scenario: Stop outcome remains unknown
- **WHEN** a stop effect is not confirmed and a caller tries a different key for the same Turn
- **THEN** the Server returns `409 stop_outcome_unknown`, keeps admission blocked, and does not supersede or replay the unresolved stop

#### Scenario: Execution and stop race
- **WHEN** a Runner result arrives after a stop has already committed a terminal fence
- **THEN** the late result is ignored for public state and at most one terminal public outcome and event are retained
