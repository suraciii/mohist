### Requirement: Each Runner process has one opaque generation

Each Runner process MUST create a non-empty opaque `processGeneration` when the process starts. The generation MUST remain unchanged for that process lifetime, MUST be included in registration and every poll, and MUST differ from the generation of every other process using the same Runner identity. The Server SHALL compare process generations only for equality and MUST NOT derive ordering or age from their values.

#### Scenario: A Runner process starts

- **WHEN** a Runner process starts and registers its Runner identity
- **THEN** it MUST create and register one non-empty `processGeneration`
- **AND** every poll from that process MUST carry the same generation

#### Scenario: The same process reconnects

- **WHEN** transport connectivity is lost and restored without terminating the Runner process
- **THEN** the process SHALL reuse its existing `processGeneration` when it registers or polls again
- **AND** the Server MUST NOT treat that reconnect as a process-generation replacement

#### Scenario: The Runner process restarts

- **WHEN** a supervisor starts a replacement process under the same Runner identity
- **THEN** the replacement process MUST use a `processGeneration` different from the terminated process

### Requirement: Claims are owned by the registered process generation

The Server SHALL admit execution work only from a poll whose `processGeneration` matches the currently registered generation for that Runner. Every successful Workflow or AgentJob claim MUST atomically record the claiming Runner identity and process generation with the Running work identity. A poll with a missing or non-current generation MUST NOT claim work or receive an execution redelivery.

#### Scenario: Workflow work is claimed

- **WHEN** a current-generation poll successfully claims eligible Workflow work
- **THEN** the Workflow attempt SHALL become Running with both the Runner identity and the poll's `processGeneration` recorded as its claim owner

#### Scenario: AgentJob work is claimed

- **WHEN** a current-generation poll successfully claims an eligible AgentJob
- **THEN** the AgentJob ledger SHALL become Running with both the Runner identity and the poll's `processGeneration` recorded as its claim owner

#### Scenario: A stale process polls

- **WHEN** a poll carries a missing generation or a generation different from the Runner's current registered generation
- **THEN** the Server MUST NOT create a Workflow or AgentJob claim for that poll
- **AND** the Server MUST NOT redeliver Running execution work to that poll

### Requirement: Registration closes out older-generation Running work

When registration establishes a different current generation for an existing Runner identity, the Server MUST close out every Workflow and AgentJob work item still Running under an older generation of that Runner as failed with reason code `runner-lost`. This closeout MUST use Server-persisted claim ownership and MUST NOT depend on a Runner journal, in-flight report, or other Runner-side durable state. The replacement generation MUST NOT be allowed to poll for work until the older-generation closeout has completed.

#### Scenario: A Runner dies during execution and restarts

- **WHEN** generation `g1` has Running work, its process terminates without reporting a result, and generation `g2` registers under the same Runner identity
- **THEN** the Server SHALL settle every still-Running `g1` work item as `FAILED("runner-lost")`
- **AND** this settlement MUST complete before the Server serves the first poll from `g2`

#### Scenario: Runner-side durable state is absent

- **WHEN** a replacement generation registers after all process-local work and report state from the prior generation has been lost
- **THEN** the Server SHALL still discover and close out the prior generation's Running Workflow and AgentJob claims from its persisted ledgers

#### Scenario: Closeout cannot complete

- **WHEN** the Server cannot complete older-generation closeout during replacement registration
- **THEN** it MUST NOT admit a poll or claim for the replacement generation until closeout is successfully reconciled

#### Scenario: The same generation registers again

- **WHEN** registration repeats with the generation already current for that Runner identity
- **THEN** the Server MUST preserve work Running under that generation
- **AND** it MUST NOT synthesize `runner-lost` solely because registration repeated

### Requirement: Work is never executed across process generations

A work item claimed by one process generation MUST NOT be redelivered for execution to another process generation. When normal owner recovery makes a distinct retry attempt eligible after generation closeout, that retry MUST have its own work identity and MUST be claimed through the ordinary dispatch path.

#### Scenario: The replacement generation polls after closeout

- **WHEN** older-generation work has been closed out and the replacement generation sends its first poll
- **THEN** the closed work identity MUST NOT appear as an execution dispatch or reserve Runner capacity

#### Scenario: Workflow recovery creates a retry

- **WHEN** normal Workflow failure handling makes another attempt eligible after `runner-lost`
- **THEN** the retry SHALL use a work identity distinct from the closed attempt
- **AND** only that distinct attempt SHALL be eligible for the replacement generation to claim through normal dispatch

### Requirement: Presence expiry remains the no-replacement backstop

Generation replacement SHALL provide the earliest closeout when a replacement process appears. If no replacement generation registers, presence expiry MUST continue to close out Running Workflow and AgentJob work for the missing Runner with the same `runner-lost` failure reason.

#### Scenario: A crashed Runner never returns

- **WHEN** a Runner process disappears with Running work and no replacement generation registers before presence expires
- **THEN** the Server SHALL close out that Running work as `FAILED("runner-lost")`

### Requirement: Same-generation dispatch behavior is preserved

While the registered process generation remains unchanged, process-generation fencing MUST NOT change dispatch ordering, claim atomicity, capacity enforcement, readiness-signal admission, or the reconciliation of work reported by that process as in flight or awaiting acknowledgement.

#### Scenario: A live process continues polling

- **WHEN** the current Runner process repeatedly polls with its registered generation
- **THEN** eligible work SHALL continue through the existing ordering, capacity, readiness, and claim rules
- **AND** generation fencing MUST NOT cancel or fail its Running work
