## ADDED Requirements

### Requirement: Issue Detection and Filtering

The system SHALL detect and filter GitHub Issues based on configurable criteria.

#### Scenario: Filter by stage labels

- **WHEN** processing Issues
- **THEN** system SHALL only select Issues with stage:* labels matching the configured filter

#### Scenario: Filter by additional labels

- **WHEN** --label parameter is specified
- **THEN** system SHALL only select Issues with all specified labels

#### Scenario: Limit number of Issues

- **WHEN** --limit parameter is specified
- **THEN** system SHALL process at most the specified number of Issues

### Requirement: Claim-based Tracking

The system SHALL prevent duplicate processing of Issues using claim-based tracking.

#### Scenario: Claim an Issue

- **WHEN** starting to process an Issue
- **THEN** system SHALL create a claim record in crawlph-claims.json
- **AND** the claim SHALL include Issue number, timestamp, and orchestrator session ID

#### Scenario: Skip already claimed Issues

- **WHEN** an Issue is already claimed by another orchestrator session
- **THEN** system SHALL skip processing that Issue
- **AND** system SHALL log a message indicating the Issue is already being processed

#### Scenario: Release claim on completion

- **WHEN** Issue processing completes (success or failure)
- **THEN** system SHALL release the claim
- **AND** the claim record SHALL be removed from crawlph-claims.json

### Requirement: Concurrent Processing

The system SHALL support concurrent processing of multiple Issues with configurable limits.

#### Scenario: Process Issues in parallel

- **WHEN** multiple Issues are available for processing
- **THEN** system SHALL spawn sub-agents for each Issue concurrently
- **AND** system SHALL respect the maximum concurrent limit (default: 8)

#### Scenario: Respect concurrent limit

- **WHEN** the number of active sub-agents reaches the limit
- **THEN** system SHALL wait for at least one sub-agent to complete
- **AND** system SHALL NOT spawn additional sub-agents until a slot is available

#### Scenario: Collect results from all sub-agents

- **WHEN** all sub-agents complete
- **THEN** system SHALL collect and aggregate results
- **AND** system SHALL report overall status

### Requirement: Trigger Modes

The system SHALL support multiple trigger modes: manual, watch, and cron.

#### Scenario: Manual trigger

- **WHEN** user invokes the skill without --watch or --cron
- **THEN** system SHALL process available Issues once and exit

#### Scenario: Watch mode

- **WHEN** user invokes the skill with --watch
- **THEN** system SHALL continuously poll for new Issues
- **AND** system SHALL process new Issues as they appear
- **AND** system SHALL respect the configured interval (default: 60 seconds)

#### Scenario: Cron mode

- **WHEN** user invokes the skill with --cron
- **THEN** system SHALL process available Issues once and exit
- **AND** system SHALL be suitable for external cron scheduling
