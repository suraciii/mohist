### Requirement: A running tool row displays a live-ticking elapsed duration

A tool row whose state is `pending` or `running` SHALL display an elapsed duration derived from the row's `startedAt` timestamp and the current wall-clock time. The displayed duration SHALL update once per second while the row remains in the in-progress state. The duration SHALL be formatted using the transcript's existing elapsed-duration format (for example `4.7s` below one minute, `2m 03s` at or above one minute). This requirement applies only while the hosting session is alive (running); a non-running session SHALL NOT tick any row's duration.

#### Scenario: Running row shows a ticking duration
- **WHEN** a tool row with state `running` or `pending` renders in a running session
- **THEN** the row SHALL display an elapsed duration computed from its `startedAt` to the current wall-clock time
- **AND** the displayed duration SHALL advance once per second while the row remains in progress

#### Scenario: Pending row is treated as in progress for the live duration
- **WHEN** a tool row with state `pending` renders in a running session
- **THEN** the row SHALL display the live-ticking elapsed duration, identical in behavior to a `running` row

#### Scenario: Live duration formats consistently with finalized durations
- **WHEN** a running row's elapsed time crosses formatting boundaries (sub-second, seconds, minutes, hours)
- **THEN** the displayed live duration SHALL use the same formatting rules as a completed row's finalized duration

### Requirement: The duration freezes at the finalized delta when the tool leaves the in-progress state

When a tool row transitions from `pending`/`running` to `completed`, `failed`, or `cancelled`, the displayed duration SHALL stop ticking and SHALL be fixed at the delta between the row's `startedAt` and `completedAt`. Once frozen, the duration SHALL NOT change on subsequent wall-clock ticks.

#### Scenario: Completed row freezes at the finalized duration
- **WHEN** a tool row transitions from `running` to `completed`
- **THEN** the displayed duration SHALL stop updating
- **AND** SHALL be set to the delta between the row's `startedAt` and `completedAt`

#### Scenario: Failed row freezes at the finalized duration
- **WHEN** a tool row transitions from `running` to `failed`
- **THEN** the displayed duration SHALL stop updating and SHALL reflect the `startedAt` to `completedAt` delta

#### Scenario: Cancelled row freezes at the finalized duration
- **WHEN** a tool row transitions from `pending` or `running` to `cancelled`
- **THEN** the displayed duration SHALL stop updating and SHALL reflect the `startedAt` to `completedAt` delta

### Requirement: Live duration ticking is gated on session liveness

The wall-clock ticking of an in-progress row's duration SHALL be gated on the hosting session's liveness signal. When the session is not running (ended, completed, failed, cancelled, or inactive), an in-progress row SHALL NOT render a ticking duration. If a row was ticking at the moment the session ended, the duration SHALL stop ticking.

#### Scenario: Ticking stops when the session ends mid-tool
- **WHEN** a running session that is ticking a tool row's duration transitions to not running
- **THEN** the row's duration SHALL stop ticking from that point onward

#### Scenario: Already-ended session renders no ticking duration
- **WHEN** an in-progress tool row renders in a session that is already not running
- **THEN** the row SHALL NOT display a continuously ticking duration
