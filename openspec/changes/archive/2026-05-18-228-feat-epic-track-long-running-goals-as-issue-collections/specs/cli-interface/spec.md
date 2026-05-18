## ADDED Requirements

### Requirement: Epic CLI Commands

CLI SHALL provide a `mo epic` command group for basic Epic operations using the server-backed API client.

#### Scenario: Create Epic from CLI

- **WHEN** a user runs `mo epic create` with title, description, and priority
- **THEN** the CLI creates an Epic through the API client and prints the new Epic id

#### Scenario: List Epics from CLI

- **WHEN** a user runs `mo epic list`
- **THEN** the CLI prints Epic status, delivered/total progress, and next issue information

#### Scenario: Show Epic from CLI

- **WHEN** a user runs `mo epic show <id>`
- **THEN** the CLI prints description, status, priority, progress, next issue, and linked issues

#### Scenario: Manage Epic membership from CLI

- **WHEN** a user runs `mo epic add-issue <epic-id> <issue-id>` or `mo epic remove-issue <epic-id> <issue-id>`
- **THEN** the CLI manages membership through the API client
- **AND** duplicate membership and not-found errors are readable

#### Scenario: Update Epic lifecycle from CLI

- **WHEN** a user runs `mo epic done <id>` or `mo epic close <id>`
- **THEN** the CLI updates only the Epic status through the API client

#### Scenario: No Epic start command

- **WHEN** a user inspects `mo epic` commands
- **THEN** no command starts workflow execution for an Epic
