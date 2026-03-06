## ADDED Requirements

### Requirement: Dual-channel Progress Reporting

The system SHALL use both Channel Notifications AND Issue Comments for progress reporting.

#### Scenario: Channel Notifications for real-time updates

- **WHEN** significant real-time progress occurs
- **THEN** system SHALL send notification to configured Channel
- **AND** notification SHALL include Issue number and current status

**Channel Notification Triggers**:
- Ralph Loop iteration start/complete
- Sub-agent failure and retry
- Consecutive failure warning (> 10 attempts)
- User input required

#### Scenario: Issue Comments for milestones

- **WHEN** important milestone is reached
- **THEN** system SHALL add a comment to the Issue
- **AND** comment SHALL include relevant details and links

**Issue Comment Triggers**:
- Design PR created (include PR link)
- Implementation PR ready for review (Draft → Open)
- PR merged and Issue closed

### Requirement: Channel-based Notifications

The system SHALL send progress notifications to configured channels.

#### Scenario: Send to Telegram channel

- **WHEN** significant progress is made
- **THEN** system SHALL send notification to configured Telegram channel
- **AND** notification SHALL include Issue number and current stage

#### Scenario: Support multiple channel types

- **WHEN** channel is configured
- **THEN** system SHALL support:
  - Telegram channels
  - Other OpenClaw channel integrations

### Requirement: Channel Configuration

Channels SHALL be configurable via OpenClaw settings.

#### Scenario: Configure in openclaw.json

- **WHEN** channel is configured in skills.entries["crawlph"].notifyChannel
- **THEN** system SHALL use that channel for notifications
- **AND** format SHALL be "{channel_type}:{channel_id}"

#### Scenario: Override via command line

- **WHEN** --notify-channel parameter is provided
- **THEN** system SHALL use the specified channel
- **AND** command line SHALL override configuration file

### Requirement: Notification Content

Notifications SHALL include relevant progress information.

#### Scenario: Stage transition notification

- **WHEN** transitioning between stages
- **THEN** notification SHALL include:
  - Issue number and title
  - Previous stage
  - New stage
  - Timestamp

#### Scenario: Completion notification

- **WHEN** Issue processing completes
- **THEN** notification SHALL include:
  - Issue number
  - PR link
  - Summary of changes

#### Scenario: Failure notification

- **WHEN** Issue processing fails
- **THEN** notification SHALL include:
  - Issue number
  - Error message
  - Retry count

### Requirement: Notification Throttling

The system SHALL avoid notification spam.

#### Scenario: Batch notifications

- **WHEN** multiple events occur in quick succession
- **THEN** system SHALL batch notifications
- **AND** system SHALL send at most one notification per minute per Issue

#### Scenario: Skip minor updates

- **WHEN** progress update is minor (e.g., reading files)
- **THEN** system SHALL NOT send notification
- **AND** system SHALL only notify on significant events
