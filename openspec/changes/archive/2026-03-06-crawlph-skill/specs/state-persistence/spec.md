## ADDED Requirements

### Requirement: File-based State Storage

The system SHALL persist state to files in the crawlph agent data directory (`~/.openclaw/agents/crawlph/data/`).

#### Scenario: Store claims

- **WHEN** claiming an Issue
- **THEN** system SHALL write to `crawlph-claims.json`
- **AND** file SHALL be atomic (write to temp, then rename)

#### Scenario: Store cursor

- **WHEN** in watch mode
- **THEN** system SHALL store cursor in `crawlph-cursor.json`
- **AND** cursor SHALL include last checked timestamp

#### Scenario: Store progress

- **WHEN** making progress on an Issue
- **THEN** system SHALL store in `progress/issue-{N}.json`
- **AND** progress SHALL include current stage, attempts, and checkpoints

#### Directory Structure

```
~/.openclaw/agents/crawlph/data/
├── crawlph-claims.json      # Issue claims
├── crawlph-cursor.json      # Watch mode cursor
└── progress/                # Per-issue progress
    ├── issue-123.json
    └── issue-456.json
```

### Requirement: State File Format

State files SHALL use JSON format with consistent structure.

#### Scenario: Claims file format

- **WHEN** reading `crawlph-claims.json`
- **THEN** format SHALL be:
  ```json
  {
    "claims": {
      "123": {
        "sessionId": "session-id",
        "claimedAt": "2024-01-01T00:00:00Z",
        "stage": "design"
      }
    }
  }
  ```

#### Scenario: Progress file format

- **WHEN** reading `progress/issue-{N}.json`
- **THEN** format SHALL be:
  ```json
  {
    "issueNumber": 123,
    "currentStage": "implementation",
    "attempts": 2,
    "prNumber": 456,
    "checkpoints": {
      "exploration": "2024-01-01T00:00:00Z",
      "design": "2024-01-01T01:00:00Z"
    }
  }
  ```

### Requirement: State Recovery

The system SHALL recover state after restart.

#### Scenario: Resume from persisted state

- **WHEN** orchestrator restarts
- **THEN** system SHALL read existing state files
- **AND** system SHALL resume processing from last checkpoint

#### Scenario: Handle corrupted state

- **WHEN** state file is corrupted
- **THEN** system SHALL log an error
- **AND** system SHALL start fresh for that Issue

### Requirement: State Cleanup

The system SHALL clean up stale state.

#### Scenario: Clean up completed Issues

- **WHEN** Issue processing completes
- **THEN** system SHALL remove progress file
- **AND** system SHALL release claim

#### Scenario: Clean up stale claims

- **WHEN** a claim is older than 24 hours
- **THEN** system SHALL consider it stale
- **AND** system SHALL allow re-claiming the Issue
