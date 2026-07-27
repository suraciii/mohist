### Requirement: Current Session runtime-event vocabulary
The Server SHALL discard unsupported AgentSession runtime-event entries at ingress before activity recording, domain application, runtime-envelope creation, persistence scheduling, realtime publication, or event-info output. `session.closed`, `session.followup_completed`, and `session.followup_failed` MUST NOT be accepted as runtime facts, mapped to transcript parts, or exposed as current transcript event types.

#### Scenario: Retired runtime event is submitted
- **WHEN** a runtime event batch contains `session.closed`, `session.followup_completed`, or `session.followup_failed`
- **THEN** the retired event MUST NOT produce a state change, activity refresh, runtime envelope, event-info result, realtime publication, or persisted transcript part

#### Scenario: Retired event accompanies a current event
- **WHEN** a runtime event batch contains a retired event and a current supported event
- **THEN** the Server SHALL discard only the retired event and process the supported event through its established runtime-event behavior

#### Scenario: Current terminal activity is submitted
- **WHEN** a runtime reports a terminal `session.activity` fact with its terminal status
- **THEN** the Server SHALL persist it as a `session.activity` transcript fact without using a retired terminal event name

### Requirement: Web Session event contract uses current vocabulary
The Web canonical transcript subscription, typed live-event contract, transcript rendering, and Session activity views SHALL include only current Session event types. The Web MUST NOT subscribe to, type, label, route, or apply special view handling for `session.closed`, `session.followup_completed`, or `session.followup_failed`.

#### Scenario: Web configures transcript event subscriptions
- **WHEN** the Web establishes its transcript event subscription set
- **THEN** the set MUST NOT contain `session.closed`, `session.followup_completed`, or `session.followup_failed`

#### Scenario: Web renders current terminal activity
- **WHEN** the Web receives a `session.activity` event that carries terminal status context
- **THEN** it SHALL use the current Session activity handling path without consulting a retired event-name branch

### Requirement: Terminal activity behavior remains activity-authoritative
Terminal execution facts SHALL remain `session.activity` records with their established delivery-idempotency behavior. Session state resolution and command eligibility MUST continue to use the current Session activity rather than historical terminal event names.

#### Scenario: Duplicate terminal activity delivery is retried
- **WHEN** the same terminal `session.activity` delivery is reported more than once with the same delivery identity
- **THEN** the Session SHALL retain one idempotent terminal activity fact and preserve its activity-based state and command eligibility

### Requirement: Historical transcript data remains untouched
This change SHALL NOT migrate, rewrite, or delete already persisted historical transcript records.

#### Scenario: Existing historical records are retained
- **WHEN** the change is deployed to a store containing historical transcript records
- **THEN** the stored records SHALL remain unchanged
