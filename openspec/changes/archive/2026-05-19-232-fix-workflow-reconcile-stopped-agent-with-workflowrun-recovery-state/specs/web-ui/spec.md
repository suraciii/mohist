## ADDED Requirements

### Requirement: Issue Detail actions derive from recovery projection

Issue Detail SHALL render primary recovery actions from the backend recovery projection rather than from `issue.status === blocked` or other issue-level heuristics alone.

#### Scenario: Blocked issue does not automatically show Retry

- **WHEN** an issue has `status = blocked`
- **AND** the backend recovery projection does not include retry as an allowed action
- **THEN** Issue Detail SHALL NOT render or enable Retry solely because the issue is blocked

#### Scenario: Failed latest attempt enables Retry

- **WHEN** the backend recovery projection reports latest attempt state `failed`
- **AND** retry is an allowed action
- **THEN** Issue Detail SHALL render Retry as an available action

#### Scenario: Interrupted latest attempt shows interrupted guidance

- **WHEN** the backend recovery projection reports latest attempt state `interrupted`
- **THEN** Issue Detail SHALL present resume, rerun stage, or inspect guidance according to allowed actions
- **AND** it SHALL preserve blocked reason and interruption diagnostics as supporting evidence
- **AND** it SHALL NOT label the interrupted attempt as failed retryable work

#### Scenario: Running latest attempt shows wait or stop guidance

- **WHEN** the backend recovery projection reports latest attempt state `running`
- **AND** live execution evidence is present
- **THEN** Issue Detail SHALL present wait or stop guidance rather than retry

### Requirement: Web UI agrees with API recovery actions

Web UI recovery controls SHALL match the action availability returned by the API for running, completed, failed, and interrupted latest attempt states.

#### Scenario: UI follows backend action list

- **WHEN** issue detail data includes a recovery projection with allowed actions
- **THEN** the Issue Detail primary action controls SHALL be enabled only for actions present in that projection
- **AND** disabled or unavailable actions SHALL not be inferred from issue status alone
