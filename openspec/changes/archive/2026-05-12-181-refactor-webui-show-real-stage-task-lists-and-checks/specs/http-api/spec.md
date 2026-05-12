## MODIFIED Requirements

### Requirement: REQ-HTTP-001 Issue stage-state API exposes current progress

`GET /api/issues/:number/stage-state` SHALL expose the canonical user-visible workflow stage view rather than raw stored stage-task rows. Each returned stage SHALL contain the current task list, current check list, stage status, approval state when present, and task metadata needed to explain runtime-added work.

#### Scenario: Stage-state excludes obsolete placeholders

- **WHEN** the backend has stored obsolete placeholder rows alongside real workflow task evidence
- **THEN** `GET /api/issues/:number/stage-state` SHALL exclude the obsolete placeholders from the returned task list
- **AND** it SHALL return the real workflow tasks for that stage in user-visible order

#### Scenario: Stage-state includes reason-aware runtime tasks

- **WHEN** a runtime repair, retry, rebase, or conflict-resolution task exists for a stage
- **THEN** `GET /api/issues/:number/stage-state` SHALL include that task in the stage task list
- **AND** it MAY include explanation metadata such as `reason` or `causedBy`

#### Scenario: Stage-state keeps checks separate

- **WHEN** the API returns stage progress for Issue Detail
- **THEN** the response SHALL keep tasks and checks in separate collections
- **AND** supporting evidence such as task output, attempts, or artifact paths SHALL remain task/check detail data rather than separate top-level tasks
