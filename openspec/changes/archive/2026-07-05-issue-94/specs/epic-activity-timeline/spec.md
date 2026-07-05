### Requirement: Epic domain events are persisted instead of drained

The `EpicGrain.ApplyPendingEvents` operation (currently a no-op that calls `ClearPendingEvents`, discarding every recorded event) SHALL persist the epic's pending domain events to a dedicated epic-events store. Every code path that records an epic event — create, update, priority change, status transition, close, reopen, and issue link/unlink — SHALL persist its events through this path rather than draining them.

#### Scenario: Events are no longer discarded

- **WHEN** an epic undergoes a mutation that records a domain event (e.g., a status transition or an issue link)
- **THEN** the event SHALL be persisted to the epic-events store
- **AND** the event SHALL be retrievable via the events read path

#### Scenario: Creation event is persisted

- **WHEN** a new epic is created
- **THEN** an `EpicCreated` event SHALL be persisted with the creation timestamp

### Requirement: Dedicated epic-events store

A new epic-events table SHALL be added via an EF Core migration with a column set analogous to the existing issue-events store (source, event id/type, time, spec version, subject, data content type, data payload, extensions). The store SHALL key events by epic source so that an epic's full event history is queryable, and SHALL assign a monotonically increasing per-source sequence id (analogous to the issue-events store).

#### Scenario: Migration creates the epic-events table

- **WHEN** the system upgrades
- **THEN** a new epic-events table SHALL exist with columns analogous to the issue-events store

#### Scenario: Per-epic event sequence

- **WHEN** multiple events are persisted for one epic
- **THEN** each event SHALL receive a per-epic sequence id that increases monotonically

### Requirement: Events carry a timestamp from the injected time provider

Each persisted epic event SHALL carry the timestamp of the mutation, sourced from the injected `TimeProvider` (the same `Now()` the grain uses for state transitions), not from a wall clock. This SHALL hold for tests driving a fake `TimeProvider`.

#### Scenario: Event timestamp reflects the injected time

- **WHEN** a mutation records and persists an event using a fake `TimeProvider` set to a fixed instant
- **THEN** the persisted event's time SHALL equal that fixed instant

### Requirement: Activity timeline event coverage

The activity timeline SHALL cover the full set of epic domain events: creation, field updates, priority changes, status transitions (start/pause/resume/done/close), reopen, and issue link/unlink. The dedicated `EpicReopened` event SHALL be distinguishable on the timeline from generic status changes.

#### Scenario: Timeline shows status changes

- **WHEN** an epic's status changes (e.g., idle → running, running → done)
- **THEN** the timeline SHALL include an entry describing the status change with old and new status

#### Scenario: Timeline shows issue link and unlink

- **WHEN** an issue is linked to or unlinked from an epic
- **THEN** the timeline SHALL include an entry identifying the issue (number/id) and the action

#### Scenario: Timeline shows priority changes

- **WHEN** an epic's priority changes
- **THEN** the timeline SHALL include an entry showing the old and new priority

#### Scenario: Timeline distinguishes reopen

- **WHEN** an epic is reopened
- **THEN** the timeline SHALL show a dedicated reopen entry distinct from a generic status change

### Requirement: Events read path and endpoint

A read path SHALL serve an epic's persisted events as a chronological activity timeline. An HTTP `GET /api/projects/{projectRef}/epics/{id}/events` endpoint SHALL return the event list, ordered chronologically. The `{id}` segment SHALL accept either the epic's internal id or its number, consistent with the other epic routes. The event DTOs SHALL expose at least the event type, the timestamp, and the type-specific payload needed to render the timeline.

#### Scenario: GET events returns the timeline

- **WHEN** a client sends `GET /epics/{id}/events` for an epic with persisted events
- **THEN** the response SHALL be HTTP 200 with the events ordered chronologically

#### Scenario: GET events for an epic with no events

- **WHEN** a client sends `GET /epics/{id}/events` for an epic that has no persisted events
- **THEN** the response SHALL be HTTP 200 with an empty list

### Requirement: Web timeline component on the detail page

The epic detail page SHALL render an activity timeline section that fetches and displays the epic's events chronologically. The timeline SHALL render at least status changes, issue link/unlink, priority changes, and reopen, each with its timestamp and the relevant details (status names, issue number, priority values).

#### Scenario: Detail page renders the timeline

- **WHEN** the detail page loads an epic that has persisted events
- **THEN** the page SHALL render a timeline section
- **AND** the section SHALL list the events chronologically with timestamps

#### Scenario: Timeline handles an empty history

- **WHEN** the detail page loads an epic with no persisted events
- **THEN** the timeline section SHALL render an empty state without error
