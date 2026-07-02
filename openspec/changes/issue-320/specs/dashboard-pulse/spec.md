## MODIFIED Requirements

### Requirement: Pulse zone renders runner capacity and live status counts

The `dashboard-pulse` zone content SHALL surface runner slot usage as an active/max capacity indicator derived from the unified `runner-capacity` projection (runner grain runtime active workflow works for `active`, persisted runner definition slots for `max`), and SHALL NOT be computed locally from the active-session-card count versus a runner-count-plus-one heuristic. The slot-usage indicator SHALL render atop the zone regardless of whether any candidate cards exist. The zone SHALL NOT render lifecycle status pills for the `active`, `waiting`, `completed`, or `failed` counts; those signals are already carried by the factory status headline (In flight / Awaiting approval) and the Digest zone, and re-listing them in Pulse would duplicate those counts. The Pulse zone's distinct signal is the runner slot-usage indicator, which the factory status headline does not duplicate.

#### Scenario: Slot usage indicator renders without status pills

- **WHEN** the Pulse zone renders with runner activity data
- **THEN** the zone SHALL render a slot-usage indicator showing active/max slots used
- **AND** the active/max values SHALL be sourced from the `runner-capacity` projection
- **AND** the zone SHALL NOT render status pills for the `active`, `waiting`, `completed`, or `failed` counts

#### Scenario: Capacity indicator renders even with no active sessions

- **WHEN** the Pulse zone renders with no active in-flight sessions
- **THEN** the zone SHALL still render the slot-usage indicator atop the zone
- **AND** the zone SHALL NOT render any lifecycle status pills
