## ADDED Requirements

### Requirement: Create persists workflow profile selection

`POST /api/issues` SHALL accept a `workflowProfileId` field in the request body and SHALL persist it as the issue's workflow profile selection. An explicitly supplied `workflowProfileId` SHALL NOT be silently dropped or replaced by a default. When the field is absent from the request body the issue SHALL have no issue-level selection and reads SHALL resolve the effective profile via default inheritance.

#### Scenario: Create with workflow profile persists it

- **WHEN** the server receives `POST /api/issues` with `workflowProfileId: "mohist/pr"`
- **THEN** the created issue's stored workflow profile selection SHALL be `mohist/pr`
- **AND** `GET /api/issues/:number` SHALL return `workflowProfileId: "mohist/pr"`

#### Scenario: Create without workflow profile inherits default

- **WHEN** the server receives `POST /api/issues` without a `workflowProfileId` key
- **THEN** the issue SHALL have no issue-level workflow profile selection
- **AND** reads SHALL resolve the effective profile to the inherited default

### Requirement: PATCH supports workflow profile selection

`PATCH /api/issues/:number` SHALL apply raw-presence-aware merge semantics to `workflowProfileId`: when the key is absent from the raw request body the issue's workflow profile selection SHALL be preserved; when present with a value the selection SHALL be replaced; when present and `null` the issue-level selection SHALL be cleared so reads fall back to default inheritance. After a successful change, the issue detail, list, and workflow-profile endpoint SHALL all reflect the new selection. The change SHALL NOT alter configured workflow profile variables, prompts, or model/stage overlays.

#### Scenario: Absent workflowProfileId preserves selection

- **WHEN** the server receives `PATCH /api/issues/:id` with `{ "body": "new" }` and no `workflowProfileId` key in the raw body
- **THEN** the issue's workflow profile selection SHALL remain unchanged

#### Scenario: Present workflowProfileId updates selection

- **WHEN** the server receives `PATCH /api/issues/:id` with `{ "workflowProfileId": "mohist/pr" }`
- **THEN** the issue's workflow profile selection SHALL become `mohist/pr`
- **AND** the issue detail, list, and workflow-profile endpoint SHALL report `mohist/pr`

#### Scenario: Null workflowProfileId clears issue-level selection

- **WHEN** the server receives `PATCH /api/issues/:id` with `{ "workflowProfileId": null }`
- **THEN** the issue SHALL have no issue-level selection
- **AND** reads SHALL resolve the effective profile via default inheritance

### Requirement: Workflow profile read is consistent across endpoints

The `workflowProfileId` returned by `GET /api/issues/:number`, the issue list endpoint, and the `GET /api/issues/:number/workflow-profile` endpoint SHALL all be the same effective value, resolved from the single source of truth. No endpoint SHALL independently hardcode or recompute a default that diverges from the issue's persisted selection.

#### Scenario: Detail and workflow-profile endpoint agree

- **WHEN** an issue has an issue-level selection of `mohist/pr`
- **THEN** `GET /api/issues/:number` SHALL return `workflowProfileId: "mohist/pr"`
- **AND** `GET /api/issues/:number/workflow-profile` SHALL report profile id `mohist/pr`

#### Scenario: List read model matches detail

- **WHEN** an issue has an effective profile of `mohist/pr`
- **THEN** the issue list endpoint SHALL include `workflowProfileId: "mohist/pr"` for that issue
- **AND** it SHALL match the value returned by `GET /api/issues/:number`

### Requirement: Started issues reject workflow profile selection changes

When an issue has an active workflow run, `PATCH /api/issues/:number` with a present `workflowProfileId` key SHALL be rejected with a clear error stating the issue has started and its execution template cannot be changed. Run-scoped runtime profile overrides (variables/prompts) via the workflow-profile endpoints SHALL remain permitted and SHALL NOT mutate the issue's original template selection.

#### Scenario: PATCH profile rejected on started issue

- **WHEN** the server receives `PATCH /api/issues/:id` with `{ "workflowProfileId": "mohist/pr" }`
- **AND** the issue has an active workflow run
- **THEN** the server SHALL return an error with a clear reason
- **AND** the issue's workflow profile selection SHALL remain unchanged

#### Scenario: Variable override remains allowed on started issue

- **WHEN** the server receives `PUT /api/issues/:number/workflow-profile/variables` for an issue with an active workflow run
- **THEN** the update SHALL be accepted as a run-scoped runtime override
- **AND** the issue's original workflow profile selection SHALL remain unchanged
