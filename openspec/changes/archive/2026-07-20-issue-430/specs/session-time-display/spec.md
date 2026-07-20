### Requirement: Time helper exposes status-aware absolute vs relative formatting

The session time-formatting helper (`formatRelativeTime` and any wrapper that consumes it) SHALL take a session `statusKind` (or equivalent terminal/non-terminal signal) and SHALL choose between an absolute and a relative time format based on that status. The helper MUST NOT rely on the wall clock alone; it SHALL accept a clock value (or an injectable `now()` provider) so that the rendered output is deterministic under test.

#### Scenario: Helper accepts statusKind and now
- **WHEN** the time helper is invoked
- **THEN** the helper SHALL accept at least a date string, the session's `statusKind`, and a `now` timestamp
- **AND** the helper's output SHALL be a function of those inputs and SHALL NOT read the system clock implicitly

#### Scenario: Helper returns absolute time for terminal sessions
- **WHEN** the helper is invoked with a terminal `statusKind` (`completed`, `failed`, or `stale`) and a session whose `lastActivityAt` (or comparable anchor timestamp) is older than the absolute-relative threshold
- **THEN** the helper SHALL return an absolute date-time string (for example `Jun 17, 09:52`)
- **AND** the helper SHALL additionally expose the relative equivalent (for example `8h ago`) for use as hover/tooltip content

#### Scenario: Helper returns relative time for live sessions
- **WHEN** the helper is invoked with a non-terminal `statusKind` (`live`, `finalizing`, `probing`)
- **THEN** the helper SHALL return the relative time string (for example `4m ago`)
- **AND** SHALL NOT force an absolute format on a running session

### Requirement: Header and sticky strip use absolute time for terminal sessions

The session header and the sticky identity strip SHALL render the timestamp of a terminal session as an absolute date-time string (for example `Jun 17, 09:52`). The relative equivalent (for example `8h ago`) SHALL be available to the user via a hover or focus tooltip on that timestamp. The `absolute-relative threshold` SHALL be defined as: the session's anchor timestamp (the more recent of `completedAt` or `lastActivityAt`) is at least 1 hour older than `now` AND `statusKind` is `completed`, `failed`, or `stale`. Below that threshold or for non-terminal statuses, the rendered format SHALL be the relative form, as before.

#### Scenario: Terminal session older than the threshold shows absolute time
- **WHEN** `statusKind` is `completed` (or `failed` / `stale`)
- **AND** the session's anchor timestamp is at least 1 hour older than `now`
- **THEN** the timestamp rendered in the header and in the sticky strip SHALL be an absolute date-time
- **AND** a hover or focus tooltip SHALL expose the relative equivalent

#### Scenario: Terminal session younger than the threshold still shows relative time
- **WHEN** `statusKind` is `completed` (or `failed` / `stale`)
- **AND** the session's anchor timestamp is less than 1 hour older than `now`
- **THEN** the timestamp rendered in the header and in the sticky strip SHALL remain the relative form
- **AND** the absolute equivalent SHALL be available as a hover or focus tooltip

#### Scenario: Live session always shows relative time
- **WHEN** `statusKind` is `live`, `finalizing`, or `probing`
- **THEN** the timestamp rendered in the header and in the sticky strip SHALL be the relative form regardless of age
- **AND** no absolute date-time SHALL be forced on a running session

### Requirement: Probing indicator keeps its "Checking since ..." relative phrasing

When `statusKind` is `probing` and `probeSentAt` is available, the header SHALL continue to display the "Checking since <relative time>" indicator using the relative time of `probeSentAt`. The probing indicator SHALL NOT switch to an absolute format even when the probe is older than the absolute-relative threshold, because the indicator's whole purpose is to communicate the elapsed wait in relative terms.

#### Scenario: Probing indicator keeps relative phrasing past the threshold
- **WHEN** `statusKind` is `probing` and `probeSentAt` is more than 1 hour in the past
- **THEN** the header SHALL render "Checking since <relative time>"
- **AND** SHALL NOT switch the probing indicator to an absolute date-time

### Requirement: Time-format helper is deterministic under test

The time-format helper's output SHALL be fully determined by its inputs (date string, `statusKind`, `now`). Two invocations with identical inputs SHALL produce identical output strings. The helper MUST NOT call the system clock; it SHALL consume `now` only as an argument. This allows unit tests to assert the absolute-vs-relative branch choice without fake timers or wall-clock waits.

#### Scenario: Same inputs produce same output
- **WHEN** the time helper is invoked twice with the same date string, the same `statusKind`, and the same `now`
- **THEN** the helper SHALL return the same string in both invocations

#### Scenario: Changing now can flip the absolute/relative branch
- **WHEN** the same anchor timestamp is supplied but two different `now` values straddle the absolute-relative threshold (for example 5 minutes before vs. 5 minutes after)
- **THEN** for a terminal status the helper SHALL return the relative form for the smaller `now` delta and the absolute form for the larger `now` delta
- **AND** the helper SHALL NOT depend on the wall clock to make that decision
