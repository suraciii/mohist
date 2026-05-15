## MODIFIED Requirements

### Requirement: REQ-BDA-EVENTS-001 Drift lifecycle emits typed events

Mohist SHALL emit typed events for base advancement, drift detection, rebase opportunity decisions, safe-window transitions, evidence invalidation, and user attention requests so live clients can refresh state.

#### Scenario: Base advancement event is emitted

- **WHEN** Integrate successfully advances the project base branch
- **THEN** Mohist SHALL emit an event containing project, issue, base branch, and new base position facts

#### Scenario: Drift opportunity events are emitted

- **WHEN** an active candidate is evaluated after base advancement
- **THEN** Mohist SHALL emit events for drift detection, opportunity opening, decision made, and user attention when applicable

#### Scenario: Protected work and safe window events are emitted

- **WHEN** rebase is deferred because mutating work is active
- **THEN** Mohist SHALL emit an active-work-protected event
- **AND** when the issue reaches a safe window, Mohist SHALL emit a safe-rebase-window event

#### Scenario: Evidence invalidation event is emitted

- **WHEN** base drift or rebase invalidates candidate evidence
- **THEN** Mohist SHALL emit an event that identifies affected evidence and issue context
