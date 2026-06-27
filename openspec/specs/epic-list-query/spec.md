### Requirement: Epic list query issues a single aggregate SQL

The epic list endpoint (backed by `EpicQuerier.ListAsync`) SHALL fetch every epic and its linked issues for a project via a single SQL query that joins `Epics`, `EpicIssues`, and `Issues`. The number of SQL statements the list path issues SHALL be bounded by a constant that is independent of the number of epics; the list path SHALL NOT re-execute a per-epic issue query (no N+1 amplification).

#### Scenario: Query count is constant regardless of epic count
- **WHEN** the epic list endpoint is requested for a project with N epics
- **THEN** the list path SHALL issue a constant number of SQL statements that does not grow with N
- **AND** SHALL NOT execute one issue-enrichment query per epic

#### Scenario: Grouping happens in memory
- **WHEN** the single aggregate result set is returned
- **THEN** rows SHALL be grouped into epics in memory
- **AND** the existing pure function `EpicProgress.Build` SHALL be reused unchanged to derive progress

### Requirement: Epic list path avoids full issue enrichment

The epic list path SHALL NOT load `WorkflowRuns.State` JSON, Comments, Attachments, or agent configuration for linked issues. Progress, `readyToMarkDone`, `nextIssue`, and `CanStart` SHALL remain computable from the joined `Issues` columns alone, without deserializing any workflow-run state. The `IssueQuerier.ListAsync` full-enrichment path SHALL NOT be invoked from the list path.

#### Scenario: No workflow-state deserialization on list
- **WHEN** the epic list endpoint serves a project
- **THEN** the list path SHALL NOT deserialize `WorkflowRuns.State`
- **AND** SHALL NOT read Comments, Attachments, or agent configuration

### Requirement: Issue derived columns mirror the State JSON

The `Issues` table SHALL expose stored computed columns `Title`, `Priority`, `IsDraft`, and `PrerequisiteNumbersJson`, each derived from `json_extract(State, '$.…')` with `COALESCE` tolerating both camelCase and PascalCase JSON keys, mirroring the existing `ProjectId`/`Number`/`Status`/`IsArchived` mechanism. The derived columns SHALL stay consistent with `State` automatically (stored computed columns); no separate write path SHALL be introduced to maintain them.

#### Scenario: Derived column tracks State after update
- **WHEN** an issue's `State` JSON is updated (title, priority, isDraft, or prerequisiteNumbers)
- **THEN** the corresponding stored computed column SHALL reflect the new value without any additional write
- **AND** readers selecting the column SHALL observe the updated value

#### Scenario: Missing or legacy keys yield null safely
- **WHEN** an issue's `State` JSON omits one of the derived keys, or uses an unexpected casing
- **THEN** the `COALESCE` expression SHALL yield a null/default value
- **AND** the query SHALL NOT error

### Requirement: Epic list progress correctness is preserved

The epic list endpoint SHALL produce `deliveredCount`, `totalIssueCount`, and `readyToMarkDone` identical to the full-enrichment path, derived from the linked issues' stored `Status`. A linked issue SHALL count as delivered only when its `Status` is `done` or `completed`; a `cancelled` linked issue SHALL NOT count toward delivery.

#### Scenario: List progress matches the detail path
- **WHEN** the epic list endpoint computes progress for an epic
- **THEN** `deliveredCount`, `totalIssueCount`, and `readyToMarkDone` SHALL equal the values the full-enrichment detail path would produce for the same epic

#### Scenario: Cancelled issues are not counted as delivered
- **WHEN** a linked issue is `cancelled`
- **THEN** it SHALL NOT increment `deliveredCount`
- **AND** SHALL NOT satisfy `readyToMarkDone`

#### Scenario: Empty epic and archived issues
- **WHEN** an epic has no linked issues, or all linked issues are archived
- **THEN** `totalIssueCount` and `deliveredCount` SHALL be zero
- **AND** `readyToMarkDone` SHALL be false

### Requirement: Epic list next-issue and CanStart correctness is preserved

The epic list endpoint SHALL compute `nextIssue` and `CanStart` from the joined issues' stored `Status`, the new `IsDraft` column, and the parsed `PrerequisiteNumbersJson` column (combined with the stored `Status` of the referenced prerequisite issues), producing selection identical to the `EpicProgress` ordering semantics used on the detail page (priority + `CanStart` + no `StartBlocker`). `cancelled` linked issues SHALL be excluded from next-issue selection.

#### Scenario: Next issue matches detail ordering
- **WHEN** the epic list endpoint selects the next issue for an epic with multiple startable linked issues
- **THEN** the selected issue SHALL be the same issue the detail path would select by the shared priority + CanStart + no-StartBlocker ordering

#### Scenario: Unmet prerequisite blocks CanStart
- **WHEN** a linked issue's `PrerequisiteNumbersJson` references a prerequisite issue whose stored `Status` is not `done`/`completed`
- **THEN** that linked issue's `CanStart` SHALL be false on the list endpoint
- **AND** it SHALL match the detail path's `CanStart` for the same issue

#### Scenario: Draft issue is not startable
- **WHEN** a linked issue's `IsDraft` is true
- **THEN** its `CanStart` SHALL be false on the list endpoint
- **AND** it SHALL match the detail path's `CanStart`

#### Scenario: Cancelled issues are excluded from selection
- **WHEN** next-issue selection runs on the list endpoint
- **THEN** any `cancelled` linked issue SHALL be excluded from the candidate set

### Requirement: Epic list Health is approximated while detail Health stays exact

On the epic list endpoint, an `in_progress` linked issue SHALL be reported under `activeIssues` (Health approximated as `active`) rather than being split into `blocked` versus `active`. The epic DETAIL endpoint (`EpicDetailDto`, single epic) SHALL continue to compute precise Health (distinguishing `blocked` from `active`) via the unchanged full-enrichment path. The progress bar, `readyToMarkDone`, `nextIssue`, and `CanStart` SHALL remain exact on the list endpoint regardless of the Health approximation. The `EpicWithProgressDto` shape SHALL be unchanged, so the web client is unaware of the approximation.

#### Scenario: Blocked issue reported as active on the list
- **WHEN** a linked issue is `in_progress` with Health `blocked`
- **AND** the epic list endpoint is requested
- **THEN** the issue SHALL appear under `activeIssues`
- **AND** SHALL NOT appear under `blockedIssues` on the list endpoint

#### Scenario: Detail page keeps precise Health
- **WHEN** the epic detail endpoint is requested for an epic containing a `blocked` in-progress issue
- **THEN** the detail response SHALL continue to distinguish `blocked` from `active` precisely

#### Scenario: Approximation does not affect exact fields
- **WHEN** the list endpoint applies the Health approximation
- **THEN** the progress bar, `readyToMarkDone`, `nextIssue`, and `CanStart` SHALL remain identical to the full-enrichment computation
