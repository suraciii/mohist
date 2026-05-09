## MODIFIED Requirements

### Requirement: integration progress events
The system SHALL emit live events for Integrate progress and failures.

#### Scenario: Integration starts and progresses
- **WHEN** Integrate starts or a step changes status
- **THEN** SSE clients receive integration events containing issue identity, step, status, summary, and optional structured output

#### Scenario: Integration fails
- **WHEN** Integrate fails at `spec-sync`, `archive`, `merge`, or `final-health`
- **THEN** SSE clients receive an integration failure event with the failing step and actionable details
