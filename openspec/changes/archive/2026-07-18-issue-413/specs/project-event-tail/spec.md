### Requirement: `mo events tail` follows the selected project's live event stream

`mo events tail` SHALL follow the live event stream of the selected project and emit each live canonical event envelope observed for that project. The project SHALL be resolved from the active project or from `--project` / `--project-id`. When no project can be resolved, the command SHALL fail locally without streaming. Without `--match`, every live project event SHALL be emitted.

#### Scenario: Tailing emits live project events

- **WHEN** an operator runs `mo events tail` against a project that is producing events
- **THEN** each live canonical event envelope for that project SHALL be emitted as it occurs

#### Scenario: No project resolves to a local failure

- **WHEN** an operator runs `mo events tail` and no active project is set and no `--project` / `--project-id` is supplied
- **THEN** the command SHALL fail without contacting the server
- **AND** SHALL report that no project was selected

### Requirement: Match filter restricts output to matching events

When `--match <expr>` is supplied, the tail SHALL emit only envelopes for which the expression matches. An event that does not match SHALL NOT be emitted.

#### Scenario: Only matching events are emitted

- **WHEN** an operator runs `mo events tail --match "event.type == \"com.mohist.issue.completed\" && event.issue in [\"42\", \"43\"]"`
- **THEN** only `com.mohist.issue.completed` events for issue 42 or 43 SHALL be emitted
- **AND** all other events SHALL be suppressed

### Requirement: Strict project isolation

The tail SHALL deliver only events stamped with the resolved project id. An event that carries no project id, or a different project id, SHALL NOT be delivered, even if its type or other attributes would otherwise match the expression. The tail SHALL NOT fall back to delivering unprojected events.

#### Scenario: Other-project event is not delivered

- **WHEN** the tail is scoped to project P and an event stamped with project id Q occurs
- **THEN** the event SHALL NOT be emitted, regardless of the match expression

#### Scenario: Unprojected event is not delivered

- **WHEN** the tail is scoped to a project and an event carrying no project id occurs
- **THEN** the event SHALL NOT be emitted

### Requirement: Match expression is validated before streaming begins

When `--match` is supplied with an expression that fails to compile, the CLI SHALL reject it before any streaming begins. The CLI SHALL write a diagnostic that identifies the error location to standard error and SHALL exit with a non-zero status. No events SHALL be emitted for an invalid expression.

#### Scenario: Invalid expression is rejected before streaming

- **WHEN** an operator runs `mo events tail --match "(event.type == \"x\""`
- **THEN** the CLI SHALL write a location-identifying diagnostic to standard error
- **AND** SHALL exit with a non-zero status
- **AND** SHALL NOT emit any events

### Requirement: Matching is evaluated against the canonical envelope on the server side

The selected expression SHALL be registered with the server's live event delivery and evaluated against each canonical CloudEvent envelope on the server side. The server SHALL deliver to the tail only envelopes that match. The tail SHALL NOT match against event payload content; matching SHALL use envelope attributes only.

#### Scenario: Server filters before delivery

- **WHEN** a tail with `--match` is active and several live events occur
- **THEN** the server SHALL evaluate the expression against each envelope and deliver only matching envelopes to the tail

#### Scenario: Payload content is not used for matching

- **WHEN** an event whose payload contains a value that would satisfy the expression but whose envelope attributes do not
- **THEN** the event SHALL NOT be emitted

### Requirement: Output is one compact envelope per line

The tail SHALL print each emitted event as one compact JSON object on its own line. Each object SHALL carry the canonical CloudEvent envelope fields: `type`, `source`, `id`, `time`, `subject`, `specversion`, and any context extensions. Output SHALL be line-delimited; the tail SHALL NOT emit a single open JSON array.

#### Scenario: One line per event

- **WHEN** two matching events occur during an active tail
- **THEN** the tail SHALL print two lines, one compact JSON object per event
- **AND** each object SHALL carry the canonical envelope fields and extensions

### Requirement: Cancellation terminates the tail

On an interrupt or cancellation signal, the tail SHALL stop streaming, release its live subscription, and exit without emitting further events.

#### Scenario: Interrupt stops the tail

- **WHEN** an operator interrupts a running tail
- **THEN** the tail SHALL stop emitting events
- **AND** SHALL release its live subscription
- **AND** SHALL exit cleanly

### Requirement: Live, best-effort observation without replay

The tail SHALL observe live events from the moment its subscription is established. It SHALL be a transient, best-effort filter and SHALL NOT provide durable persistence, replay, or delivery acknowledgement. Events that occur before the subscription is established, or that are dropped while the connection is interrupted, SHALL NOT be replayed.

#### Scenario: No replay of events before subscription

- **WHEN** events occur before an operator starts `mo events tail`
- **THEN** those prior events SHALL NOT be emitted by the tail

### Requirement: Event commands consolidated under `mo events`

The singular top-level `mo event` noun SHALL be removed. Dead-letter operations SHALL be available only under `mo events dead-letter`. `mo events tail` and `mo events dead-letter` SHALL together form the event command surface. The singular `mo event ...` form SHALL no longer resolve.

#### Scenario: Dead-letter operations move under the plural noun

- **WHEN** an operator runs `mo events dead-letter list` or `mo events dead-letter redeliver <id>`
- **THEN** the command SHALL resolve and behave as the former `mo event dead-letter` commands did

#### Scenario: Singular noun is removed

- **WHEN** an operator runs `mo event dead-letter list`
- **THEN** the command SHALL NOT resolve
