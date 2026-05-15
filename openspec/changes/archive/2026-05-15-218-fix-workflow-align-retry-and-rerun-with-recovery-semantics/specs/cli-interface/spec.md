## ADDED Requirements

### Requirement: REQ-CLI-RECOVERY-001 Recovery copy uses retry rerun rewind vocabulary

CLI-facing workflow recovery messages SHALL use the recovery vocabulary `retry`, `rerun`, and `rewind`. Workflow recovery copy SHALL NOT reintroduce `restart` as a recovery action; the only allowed `restart` usage is unrelated server restart commands or the removed restart endpoint explaining that restart is unavailable.

#### Scenario: Retry and rerun guidance uses approved terms
- **WHEN** a workflow recovery command or endpoint fails and the CLI displays the error
- **THEN** the guidance uses `retry`, `rerun`, or `rewind` as appropriate
- **AND** it does not tell the user to restart the workflow or pipeline

#### Scenario: Approval rejection copy avoids restart terminology
- **WHEN** an issue approval is rejected and CLI output describes the follow-up behavior
- **THEN** the message does not say the pipeline will restart
- **AND** it uses current recovery vocabulary or neutral state-transition wording
