## MODIFIED Requirements

### Requirement: REQ-BDA-API-001 Issue APIs expose drift and rebase decision state

Issue list, issue show, and stage-state APIs SHALL expose normalized base drift and rebase opportunity state for active issue candidates.

#### Scenario: Issue response includes drift state

- **WHEN** an issue has evaluated base drift state
- **THEN** issue API responses SHALL include whether it is drifted, the rebase decision, safe-window status, defer reason when applicable, stale evidence flags, base SHA facts when available, and next action guidance

#### Scenario: Stage-state includes drift guidance

- **WHEN** a client reads stage-state for a drifted issue
- **THEN** the response SHALL include enough drift and rebase decision detail to render user guidance without inspecting raw workflow logs

#### Scenario: Conflict diagnostics are durable

- **WHEN** drift-driven `rebase-branch` fails with conflicts or conflict-resolution failure
- **THEN** issue or stage-state responses SHALL expose conflict files, failure reason, and next action guidance from durable projected state
