## MODIFIED Requirements

### Requirement: Direct Agent sessions included in active-agents readout

The active-agents readout SHALL include generic `agent-launch` sessions that are currently active, and SHALL NOT exclude records solely because they have a blank workflow run id or work id. The active-agents entry for a generic session SHALL attribute the session to its Agent profile and SHALL NOT require a workflow-run-derived work item to report progress. The active-agents readout SHALL convey AgentSession *visibility* only — which sessions are currently shown and can enter transcript or activity — and SHALL NOT be consumed as the source of capacity active-slot counts; capacity used/max slots SHALL be sourced from the `runner-capacity` projection instead. Workflow-session entries in the active-agents readout SHALL remain unchanged.

#### Scenario: Active generic session appears in active-agents

- **WHEN** a generic `agent-launch` session is currently active
- **THEN** the active-agents readout SHALL include it
- **AND** SHALL NOT exclude it for having a blank workflow run id or work id

#### Scenario: Generic active-agent entry is agent-attributed

- **WHEN** the active-agents readout includes a generic session
- **THEN** the entry SHALL attribute the session to its Agent profile
- **AND** SHALL NOT require a workflow-run-derived work item to report progress

#### Scenario: Active-agents readout conveys visibility, not capacity

- **WHEN** a capacity readout (used/max slots) is computed for any surface
- **THEN** the active-agents readout count SHALL NOT be the source of the capacity active-slot count
- **AND** capacity SHALL be sourced from the `runner-capacity` projection instead

#### Scenario: Workflow active-agent entries are preserved

- **WHEN** the active-agents readout includes workflow sessions
- **THEN** those entries SHALL behave exactly as before this change
- **AND** their workflow-derived progress SHALL remain unchanged
