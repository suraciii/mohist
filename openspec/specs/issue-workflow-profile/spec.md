### Requirement: Single source of truth for issue workflow profile

An issue SHALL have exactly one workflow profile selection that is the single source of truth across every read surface. The issue detail read model, the issue list read model, the workflow-profile endpoint response, and `mo issue show` SHALL all project the identical effective `workflowProfileId`. When no issue-level selection is persisted, the effective profile SHALL be resolved by inheriting the project default and then the system default (`mohist/default`); no read surface SHALL independently invent or hardcode a default independent of this resolution.

#### Scenario: All read surfaces agree after create with PR profile

- **WHEN** an issue is created with workflow profile `mohist/pr`
- **THEN** the issue detail read model SHALL report `workflowProfileId: "mohist/pr"`
- **AND** the issue list read model SHALL report `workflowProfileId: "mohist/pr"`
- **AND** the workflow-profile endpoint SHALL report the same profile id
- **AND** `mo issue show <number>` SHALL display `mohist/pr`

#### Scenario: Read surfaces agree after update to PR profile

- **WHEN** a backlog issue whose profile is `mohist/default` is updated to `mohist/pr`
- **THEN** the issue detail, list, and workflow-profile endpoint SHALL all report `workflowProfileId: "mohist/pr"` in the same response cycle

#### Scenario: No issue-level selection inherits default

- **WHEN** an issue is created without an explicit workflow profile selection
- **THEN** the effective `workflowProfileId` SHALL resolve to the project default, or the system default `mohist/default` when no project default exists
- **AND** every read surface SHALL report that same resolved value

### Requirement: Create persists the workflow profile selection

`POST /api/issues` SHALL persist the `workflowProfileId` supplied in the request body as the issue's workflow profile selection when provided. The persisted value SHALL be the value returned by every subsequent read of the issue's workflow profile. The create handler SHALL NOT silently drop or default an explicitly supplied `workflowProfileId`.

#### Scenario: Create with explicit profile persists it

- **WHEN** a client sends `POST /api/issues` with `workflowProfileId: "mohist/pr"`
- **THEN** the created issue's stored workflow profile selection SHALL be `mohist/pr`
- **AND** a subsequent `GET /api/issues/:number` SHALL return `workflowProfileId: "mohist/pr"`

#### Scenario: Create without profile inherits default

- **WHEN** a client sends `POST /api/issues` without a `workflowProfileId` field
- **THEN** the issue SHALL have no issue-level selection
- **AND** reads SHALL resolve the effective profile via default inheritance

### Requirement: Workflow profile is mutable on backlog/ready issues

A backlog or ready issue (an issue with no active workflow run) SHALL support changing its workflow profile selection through an official entry on the API, CLI, and Web. After a successful change, every read surface SHALL reflect the new selection. Changing the workflow profile selection SHALL NOT alter any already-configured workflow profile variables, prompts, or model/stage overlays.

#### Scenario: Update profile on backlog issue

- **WHEN** a backlog issue's workflow profile is changed from `mohist/default` to `mohist/pr`
- **THEN** the issue detail, list, and workflow-profile endpoint SHALL report `mohist/pr`
- **AND** the issue's configured workflow profile variables SHALL remain unchanged

#### Scenario: Clearing issue-level selection falls back to default

- **WHEN** a backlog issue with an issue-level `mohist/pr` selection is cleared
- **THEN** the issue SHALL have no issue-level selection
- **AND** reads SHALL resolve the effective profile to the inherited default

### Requirement: Started issues reject execution template changes

An issue that has an active (started) workflow run SHALL reject any attempt to change its workflow profile selection (the execution template), returning a clear error that names the reason. The error SHALL distinguish execution-template changes from run-scoped runtime profile overrides (variables/prompts), which remain permitted. If the product supports modifying the running workflow's runtime profile, such a modification SHALL be explicitly scoped to the run and SHALL NOT mutate the issue's original template selection.

#### Scenario: Reject profile change on started issue

- **WHEN** a client attempts to change the workflow profile of an issue that has an active workflow run
- **THEN** the server SHALL return an error with a clear reason stating the issue has started
- **AND** the issue's workflow profile selection SHALL remain unchanged

#### Scenario: Runtime variable override remains allowed on started issue

- **WHEN** a client updates a workflow profile variable on an issue that has an active workflow run
- **THEN** the update SHALL be accepted as a run-scoped runtime override
- **AND** the issue's original workflow profile selection SHALL remain unchanged

### Requirement: Startup uses the displayed workflow profile

The workflow definition used when starting an issue SHALL be the one resolved from the issue's effective workflow profile selection — the same value displayed on the issue detail and `mo issue show`. A `mohist/pr` profile SHALL enter the PR publish/merge execution path, and a `mohist/default` profile SHALL enter the default merge/push execution path. The startup resolution SHALL NOT consult a divergent source from the read models.

#### Scenario: PR profile starts the PR workflow

- **WHEN** an issue whose effective profile is `mohist/pr` is started
- **THEN** the started workflow run SHALL use the PR workflow definition
- **AND** the run SHALL enter the PR publish/merge path

#### Scenario: Default profile starts the default workflow

- **WHEN** an issue whose effective profile is `mohist/default` is started
- **THEN** the started workflow run SHALL use the default workflow definition
- **AND** the run SHALL enter the default merge/push path