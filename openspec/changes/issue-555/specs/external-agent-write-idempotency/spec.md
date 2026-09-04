### Requirement: Every write carries a caller Idempotency-Key

Every launch, follow-up, and stop request MUST include an `Idempotency-Key` header containing 1 through 128 printable ASCII characters. The key is a header, not a JSON field, trace ID, or Input ID; on stop it is the caller-visible operation key, never a Server-generated internal operation ID. A write without a usable key SHALL fail with 400 `idempotency_key_required` or 400 `idempotency_key_invalid`, and the Server MUST NOT create a request mapping or any domain record.

#### Scenario: A write without a key is rejected before admission

- **WHEN** a launch, follow-up, or stop request omits the `Idempotency-Key` header
- **THEN** the Server SHALL return 400 `idempotency_key_required`
- **AND** no request mapping, canonical record, queue entry, or external effect SHALL be created

#### Scenario: A malformed key is rejected

- **WHEN** a write supplies an empty, non-printable, or longer-than-128-character key
- **THEN** the Server SHALL return 400 `idempotency_key_invalid`
- **AND** no request mapping or domain record SHALL be created

### Requirement: The Server computes the request fingerprint

The Server SHALL parse the accepted JSON once and build a versioned canonical representation that preserves the `text` value exactly as parsed — without trimming, case-folding, or otherwise making two distinct prompts equivalent — and that is deterministic through canonical JSON property ordering and the route's canonical IDs. The fingerprint input SHALL include the contract version, the command kind, the command's canonical scope IDs, and the complete accepted body (empty body for stop). Only the resulting fingerprint SHALL be persisted with the durable request mapping; the fingerprint and raw request MUST NOT be exposed as public output, and the caller MUST NOT be able to submit or influence a hash that the Server trusts.

#### Scenario: A retried request with an identical payload matches

- **WHEN** the same launch request body and route IDs are submitted twice under the same key after response loss
- **THEN** both requests SHALL normalize to the same fingerprint
- **AND** the retry SHALL resolve the original mapping rather than create new work

#### Scenario: Distinct prompts are never folded together

- **WHEN** the same key is retried with a `text` that differs only by leading or trailing whitespace or letter case
- **THEN** the normalized fingerprints SHALL differ
- **AND** the retry SHALL be treated as a key-reuse conflict, not a match

### Requirement: The first request durably maps key and fingerprint to its canonical outcome

The Server SHALL durably record, before a successful command response, a request mapping whose idempotency scope is `(projectId, agentId, Idempotency-Key)` for launch, `(sessionId, Idempotency-Key)` for follow-up, and `(turnId, Idempotency-Key)` for stop. The stop mapping SHALL additionally bind `callerKeyId`, canonical `projectId`, and `sessionId` so one caller cannot look up or replay another caller's public key. A retry with the same scope, key, and fingerprint SHALL return the original canonical identities — the Job/Session/Input/Turn mapping for launch, the Input/Turn pair or durable rejection for follow-up, or the target Turn observation for stop — together with their current public observation, and MUST NOT mint different IDs or create another execution. The mapping is stable: public status, timestamps, output, error, and event sequence MAY advance as the Server learns more facts.

#### Scenario: A lost launch response retries to the same execution

- **WHEN** a launch response is lost and the caller repeats the launch with the same Idempotency-Key and body
- **THEN** the Server SHALL return the same canonical Job, Session, Input, and Turn identities as the first accepted request
- **AND** no second Job, Session, Input, Turn, queue entry, or outbox item SHALL be created

#### Scenario: A lost follow-up response retries to the same Input and Turn

- **WHEN** a follow-up is retried with the same key and body
- **THEN** the Server SHALL return the original Input/Turn mapping or its durable rejection
- **AND** no new Input, Turn, queue entry, or external effect SHALL be created

#### Scenario: Stop mappings are caller-bound

- **WHEN** a different caller reuses another caller's Idempotency-Key against the same Turn
- **THEN** the Server SHALL NOT resolve the first caller's mapping
- **AND** the request SHALL be evaluated as a fresh keyed stop for that caller, subject to the unresolved-stop conflict rule

### Requirement: A definitive admission rejection is durable under its key

A well-formed keyed launch or follow-up that receives a durable canonical admission rejection SHALL return 200 with `status=terminal`, `outcome=rejected`, and a safe public error rather than an HTTP transport failure, and that rejection SHALL be recorded durably under the same Idempotency-Key. Retries with the same scope, key, and fingerprint — including after capacity recovery, a reconnect, or a later point in time — SHALL return the same durable rejection and MUST NOT turn the rejected request into a newly accepted one. A durable rejection MAY carry null live Input and Turn IDs because it intentionally created no live records.

#### Scenario: A rejected launch survives a retry after capacity recovery

- **WHEN** a keyed launch receives a durable capacity rejection and the caller retries the identical request after capacity frees up
- **THEN** the Server SHALL return the original rejection for that key with `status=terminal` and `outcome=rejected`
- **AND** no new Job, Session, Input, or Turn SHALL be created for that key

#### Scenario: A rejection is a 200 observation, not a transport error

- **WHEN** a keyed launch or follow-up is definitively rejected by canonical admission
- **THEN** the response SHALL be 200 with a `PublicExecutionRead` whose `status` is `terminal` and `outcome` is `rejected`
- **AND** a later response-loss replay of the same key SHALL return that same durable decision without inventing an Input or Turn

### Requirement: Key reuse with a different payload conflicts

The same durable scope and Idempotency-Key supplied with a different normalized fingerprint SHALL return 409 `idempotency_key_reused`. The conflict SHALL be stable across retries and MUST NOT create a new canonical record, Input, Turn, stop operation, queue entry, outbox item, public event, or external effect.

#### Scenario: A launch key reused with different text

- **WHEN** a caller reuses a launch key with a different `text` value
- **THEN** the Server SHALL return 409 `idempotency_key_reused`
- **AND** the original mapping and its execution SHALL be unchanged and no new work SHALL be created

#### Scenario: A follow-up key reused with a different payload

- **WHEN** a caller reuses a follow-up key under the same Session with a different body
- **THEN** the Server SHALL return 409 `idempotency_key_reused`
- **AND** no new Input, Turn, queue entry, or outbox item SHALL be created

### Requirement: A keyed stop maps to one canonical fenced stop operation

After PAT scope and Project authorization, the first keyed stop request SHALL durably map `(callerKeyId, projectId, sessionId, turnId, Idempotency-Key)` to exactly one canonical per-target stop operation before any Runner effect. The Server, not the caller, SHALL freeze the target revision, context generation, complete binding or explicit null binding, and deadline; these facts remain internal and MUST NOT appear in any response or public event. A matching retry SHALL resolve the same mapping, snapshot, operation, and outcome and MUST NOT reread the current binding to create a replacement deadline or effect. A Turn already terminal at the first keyed request SHALL produce a durable no-op observation with no Runner call. A queued Turn SHALL end locally without contacting Runtime and be recorded cancelled. A running Turn SHALL use the canonical fenced stop lifecycle. A changed Turn, binding, context, or owner MUST NOT redirect the request to replacement work, and the route MUST NOT accept a caller-named Runner, Runtime Session, dispatch attempt, or internal operation.

#### Scenario: The Server freezes the stop target

- **WHEN** the first keyed stop request for a running Turn is accepted
- **THEN** the Server SHALL freeze the target revision, context generation, complete binding or explicit null binding, and deadline before any Runner effect
- **AND** a later binding change on the Session MUST NOT redirect that stop operation to replacement work

#### Scenario: A matching retry resolves the original operation

- **WHEN** a stop request is retried with the same key after response loss
- **THEN** the Server SHALL return the original operation's target Turn observation without issuing a new stop effect or recomputing the deadline

#### Scenario: An already-terminal Turn is a durable no-op

- **WHEN** the first keyed stop request targets a Turn that is already terminal
- **THEN** the Server SHALL record a durable no-op observation for that key
- **AND** no Runner call SHALL be made

#### Scenario: A queued Turn stops locally

- **WHEN** the first keyed stop request targets a Turn that is still queued
- **THEN** the Turn SHALL end locally without contacting Runtime and be recorded cancelled

### Requirement: An unresolved stop cannot be superseded by a different key

While a stop operation's fenced outcome is still `unknown`, a different Idempotency-Key targeting the same Turn SHALL return 409 `stop_outcome_unknown` instead of superseding or replaying the unresolved effect; the caller reads the Turn and no new stop is issued. Execution completion and stop race through the same terminal fence: whichever terminal fact wins is returned and emits at most one terminal public event, and a late result MUST NOT replace its outcome, output, error, or sequence. Before a fenced terminal fact exists, the uncertain stop remains `unknown`, Session admission stays blocked, and no automatic replay occurs; response loss is recovered by repeating the same POST with the same key because the direct API exposes no internal-operation lookup route.

#### Scenario: A second key cannot supersede an unknown stop

- **WHEN** a stop's fenced outcome is still `unknown` and the caller submits a stop with a different Idempotency-Key for the same Turn
- **THEN** the Server SHALL return 409 `stop_outcome_unknown`
- **AND** no new stop operation or external effect SHALL be issued

#### Scenario: The same key recovers a lost stop response

- **WHEN** a stop response is lost while the outcome is still unknown
- **THEN** repeating the POST with the same key SHALL resolve the same mapping and return the current Turn observation
- **AND** it MUST NOT create a replacement stop effect

#### Scenario: Completion and stop race through one terminal fence

- **WHEN** a Turn completes while a stop operation is in flight
- **THEN** exactly one terminal fact SHALL win through the terminal fence
- **AND** the losing late observation MUST NOT change the winner's outcome, output, error, or event sequence

### Requirement: A follow-up derives its Project and Agent from the canonical Session

For a follow-up the Server SHALL resolve the Session first and derive its Project and Agent from the canonical Session record; the idempotency scope is the Session, not a caller-declared Project or Agent. The v1 launch and follow-up body SHALL accept only a required, non-empty-after-validation `text` string; unknown properties, duplicate JSON property names, invalid JSON, and a missing or invalid Idempotency-Key SHALL fail with 400 `invalid_request` before admission. Attachments, arbitrary context references, and caller-selected execution options MUST NOT be silently accepted or ignored, and the caller MUST NOT be able to place a Project or Agent in the body or influence the fingerprint with a client-declared derived value.

#### Scenario: The body cannot smuggle execution options

- **WHEN** a launch or follow-up body contains an unknown property, an attachment reference, or a caller-selected execution option
- **THEN** the Server SHALL return 400 `invalid_request` before admission
- **AND** no canonical record or request mapping SHALL be created

#### Scenario: The Session determines Project and Agent

- **WHEN** a follow-up is accepted for a Session
- **THEN** the Project and Agent used for authorization, fingerprinting, and dispatch SHALL be derived from the canonical Session record
- **AND** no body or query value SHALL be able to select a different Agent under the same Session key
