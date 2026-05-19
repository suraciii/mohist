## MODIFIED Requirements

### Requirement: REQ-WUI-STRUCTURED-001 issue UI explains workflow convergence generically

The issue UI SHALL render generic workflow convergence state so users can understand whether a blocked workflow is converging without reading review prose.

#### Scenario: Blocked workflow displays convergence evidence

- **WHEN** an issue stage exposes convergence state
- **THEN** the issue detail or pipeline progress UI SHALL show the current failed check, blocked reason, blocking item count, directly repaired count, reaction attempt count, resolved count, unresolved count, and visible non-blocking follow-up items
- **AND** the UI SHALL avoid exposing review-specific lifecycle concepts as Mohist core primitives

#### Scenario: Convergence state is absent

- **WHEN** no convergence state is available
- **THEN** the issue UI SHALL preserve the existing task/check progress display
