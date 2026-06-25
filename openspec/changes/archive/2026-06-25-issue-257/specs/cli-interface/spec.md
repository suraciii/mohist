## ADDED Requirements

### Requirement: CLI issue create accepts workflow profile flag

`mo issue create <title>` SHALL accept a `--workflow-profile <id>` flag that selects the issue's workflow profile selection. When provided, the CLI SHALL send `workflowProfileId` in the `POST /api/issues` request body. When omitted, the CLI SHALL NOT include a `workflowProfileId` key so the server applies default inheritance. `mo issue show <number>` SHALL display the issue's effective workflow profile.

#### Scenario: Create issue with workflow profile

- **WHEN** the user runs `mo issue create "Fix bug" --workflow-profile mohist/pr`
- **THEN** the CLI sends `workflowProfileId: "mohist/pr"` in the create request body
- **AND** `mo issue show <number>` displays workflow profile `mohist/pr`

#### Scenario: Create issue without workflow profile omits the key

- **WHEN** the user runs `mo issue create "Fix bug"` without `--workflow-profile`
- **THEN** the CLI SHALL NOT include a `workflowProfileId` key in the create request body
- **AND** the created issue resolves its profile via default inheritance

#### Scenario: Show displays the effective workflow profile

- **WHEN** the user runs `mo issue show 42`
- **THEN** the output SHALL display the issue's effective workflow profile id

### Requirement: CLI issue update accepts workflow profile flag

`mo issue update <number>` SHALL accept a `--workflow-profile <id>` flag that changes the issue's workflow profile selection by sending `workflowProfileId` in the `PATCH /api/issues/:number` request body. When the flag is omitted, the CLI SHALL NOT include a `workflowProfileId` key, preserving the issue's existing selection. When the server rejects the change because the issue has started, the CLI SHALL print the server-provided error and exit with a non-zero status.

#### Scenario: Update workflow profile on backlog issue

- **WHEN** the user runs `mo issue update 42 --workflow-profile mohist/pr`
- **THEN** the CLI sends `workflowProfileId: "mohist/pr"` in the PATCH request body
- **AND** `mo issue show 42` displays workflow profile `mohist/pr`

#### Scenario: Omitting workflow profile flag preserves selection

- **WHEN** the user runs `mo issue update 42 --body "new body"` without `--workflow-profile`
- **THEN** the CLI SHALL NOT include a `workflowProfileId` key in the PATCH request body
- **AND** the issue's existing workflow profile selection SHALL remain unchanged

#### Scenario: Started issue surfaces rejection

- **WHEN** the user runs `mo issue update 42 --workflow-profile mohist/pr`
- **AND** the server rejects the change because issue 42 has an active workflow run
- **THEN** the CLI prints the server-provided error message
- **AND** exits with a non-zero status
