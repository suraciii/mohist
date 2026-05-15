## MODIFIED Requirements

### Requirement: REQ-BDA-WUI-001 Web UI surfaces drift and stale-evidence guidance

The Web UI SHALL render projected base drift and rebase opportunity state for active issues and SHALL suppress stale Check approval actions.

#### Scenario: Drifted issue is visible in issue surfaces

- **WHEN** an active issue is drifted from the current base
- **THEN** issue cards, Issue Detail, or attention summaries SHALL show user-facing drift or needs-attention wording

#### Scenario: Deferred rebase reason is shown

- **WHEN** a rebase opportunity is deferred because mutating work is running
- **THEN** Issue Detail SHALL show why rebase is deferred
- **AND** it SHALL indicate that rebase will be reconsidered at a safe window

#### Scenario: Stale Check approval is suppressed

- **WHEN** Check approval evidence is stale due to base drift
- **THEN** the Web UI SHALL hide or replace ordinary approval actions
- **AND** it SHALL guide the user to rebase or rerun checks

#### Scenario: Conflict diagnostics are visible

- **WHEN** rebase fails or conflict resolution fails
- **THEN** Issue Detail SHALL show conflict files, failure reason, and next action guidance

#### Scenario: Drift events refresh live views

- **WHEN** drift or rebase opportunity events arrive over SSE
- **THEN** the Web UI SHALL refresh affected issue and stage-state data
