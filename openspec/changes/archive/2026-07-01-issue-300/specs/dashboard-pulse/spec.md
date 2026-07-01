## MODIFIED Requirements

### Requirement: Pulse zone renders runner capacity and live status counts

The `dashboard-pulse` zone content SHALL surface runner slot usage as an active/max capacity indicator and SHALL render live status counts as pills for the four lifecycle states — `active`, `waiting`, `completed`, and `failed`. The slot-usage indicator's active/max values SHALL be derived from the unified `runner-capacity` projection (runner grain runtime active workflow works for `active`, persisted runner definition slots for `max`), and SHALL NOT be computed locally from the active-session-card count versus a runner-count-plus-one heuristic. These indicators SHALL render atop the zone regardless of whether any candidate cards exist.

#### Scenario: Slot usage and status pills render

- **WHEN** the Pulse zone renders with runner activity data
- **THEN** the zone SHALL render a slot-usage indicator showing active/max slots used
- **AND** the active/max values SHALL be sourced from the `runner-capacity` projection
- **AND** the zone SHALL render status pills for the `active`, `waiting`, `completed`, and `failed` counts

#### Scenario: Capacity header renders even with no active sessions

- **WHEN** the Pulse zone renders with no active in-flight sessions
- **THEN** the zone SHALL still render the slot-usage indicator and the four status pills atop the zone
