## MODIFIED Requirements

### Requirement: issue-recovery-actions-match-user-intent

The Web UI SHALL present issue recovery actions according to the user intent model rather than raw internal status names. Closed issues use Reopen, paused/interrupted issues use Resume, and failed or needs-action issues use Retry or Rerun Stage. The UI SHALL NOT expose Restart.

#### Scenario: Closed issue shows reopen

- **WHEN** the user views a closed issue in the Web UI
- **THEN** the issue surface shows a Reopen action
- **AND** it does not show Resume as the primary recovery action

#### Scenario: Paused issue shows resume

- **WHEN** the user views a paused issue in the Web UI
- **THEN** the issue surface shows a Resume action
- **AND** it does not label the action Reopen

#### Scenario: Interrupted issue shows resume

- **WHEN** the user views an interrupted issue in the Web UI
- **THEN** the issue surface shows a Resume action
- **AND** the UI explains that the pipeline can continue from where it stopped

#### Scenario: Failed issue shows failure-oriented actions

- **WHEN** the user views an issue in a failed or needs-action state
- **THEN** the UI shows Retry and Rerun Stage actions when those actions are allowed
- **AND** the UI does not show Restart

#### Scenario: Blocked label is replaced for users

- **WHEN** the internal issue status is `blocked`
- **THEN** the user-visible label is rendered as `Needs action` or `Failed`
- **AND** diagnostic evidence such as blockedReason remains visible
