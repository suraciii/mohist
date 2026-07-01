## ADDED Requirements

### Requirement: Single source of truth for runner slot capacity

Runner slot capacity SHALL be defined by a single authoritative source. Used slots SHALL equal the count of workflow works actively occupying runner slots at runtime, computed as the runner grain's active workflow works distinct by owner id. Max slots SHALL equal the slot count persisted on the runner definition. The capacity contract SHALL be projected by the existing `RunnerStatusService` runner status projection; a second aggregation model or capacity service SHALL NOT be introduced.

#### Scenario: Used slots count active workflow works

- **WHEN** the runner grain runtime holds active workflow works
- **THEN** the used-slot count SHALL equal the number of active workflow works distinct by owner id
- **AND** SHALL count each distinct owner id once

#### Scenario: Max slots come from the persisted runner definition

- **WHEN** a runner capacity readout is produced for an online runner
- **THEN** the max-slot value SHALL equal the slot count persisted on the runner definition
- **AND** SHALL NOT be derived from session counts or heuristics

#### Scenario: No second capacity aggregation model

- **WHEN** capacity is computed for any readout
- **THEN** it SHALL be derived through the existing runner status projection
- **AND** a duplicate capacity service or DTO SHALL NOT be introduced

### Requirement: All capacity readouts derive from the unified runner capacity source

Every runner capacity readout — the issues sidebar (`/agent/status.capacity`), the runner status page and CLI (`/runners[].capacity`), and the Dashboard pulse slot-usage indicator — SHALL derive its `active` and `max` values from the single runner capacity source via the existing runner status projection. For the same runner set at the same point in time, the readouts SHALL report identical used/max values. The wire shape (`{ active, max }`) SHALL remain unchanged; only the value derivation SHALL be unified.

#### Scenario: Sidebar and runner status agree

- **WHEN** `/agent/status.capacity` and `/runners[].capacity` are read for the same runners
- **THEN** their used and max values SHALL match
- **AND** both SHALL derive from the runner capacity source

#### Scenario: Dashboard pulse and runner status agree

- **WHEN** the Dashboard pulse slot-usage indicator renders alongside runner status
- **THEN** the pulse active/max SHALL match the runner capacity source
- **AND** SHALL NOT be computed locally from session-card counts or runner-count-plus-one

#### Scenario: CLI is consistent with the sidebar

- **WHEN** the CLI reads `/runners[].capacity` (`usedSlots`/`totalSlots`)
- **THEN** those values SHALL equal the sidebar capacity source
- **AND** SHALL be consistent with the runner status projection

### Requirement: Capacity is decoupled from AgentSession visibility

Capacity active-slot counts SHALL be sourced from runner grain runtime active workflow works and SHALL NOT be sourced from the count of active AgentSessions. The `activeAgents` readout SHALL retain its AgentSession visibility semantics (which sessions are currently shown and can enter transcript or activity) but SHALL NOT contribute to capacity active-slot counts. When runner active works exceed the number of visible active AgentSessions, capacity SHALL still reflect the runner active-works count.

#### Scenario: AgentSession count does not feed capacity

- **WHEN** a capacity active-slot count is produced
- **THEN** it SHALL equal the runner active workflow works count
- **AND** SHALL NOT equal the active AgentSession count

#### Scenario: Capacity reflects runner works when sessions diverge

- **WHEN** the runner grain holds more active workflow works than there are visible active AgentSessions
- **THEN** the used-slot count SHALL equal the runner active-works count
- **AND** SHALL NOT be clamped to or reduced by the AgentSession count

#### Scenario: activeAgents retains visibility semantics

- **WHEN** the active-agents readout is produced
- **THEN** it SHALL convey which AgentSessions are currently visible
- **AND** it SHALL NOT be consumed as the capacity active-slot source
