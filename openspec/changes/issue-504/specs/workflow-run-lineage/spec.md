### Requirement: Typed WorkflowRun issue context

WorkflowRun metadata SHALL store its Project ID, Issue number, and optional Epic number as typed fields. An Issue-backed WorkflowRun SHALL have a non-empty Project ID and a positive Issue number when created; a generic WorkflowRun without an Issue relationship SHALL omit Issue and Epic context. System lineage identity SHALL NOT be stored in annotations, and user-defined annotations SHALL remain available without interpretation as lineage identity.

#### Scenario: Issue-backed run captures typed context

- **WHEN** an Issue starts a WorkflowRun with Project `proj_1`, Issue `42`, and Epic `7`
- **THEN** the WorkflowRun metadata SHALL retain `proj_1`, `42`, and `7` as typed context fields
- **AND** its annotations SHALL NOT contain the system keys `projectId`, `issueNumber`, or `epicNumber`

#### Scenario: User annotations remain separate from lineage

- **WHEN** a WorkflowRun is created with user-defined annotations alongside Issue context
- **THEN** the user-defined annotations SHALL be retained unchanged
- **AND** lineage behavior SHALL derive only from the typed context fields

### Requirement: Context preservation and Epic affiliation refresh

WorkflowRun typed context SHALL survive persistence and reload as the Run's local snapshot of its Issue relationship, not as a second authority for Issue state. A durable Issue-context refresh for the same Project and Issue SHALL update only the optional Epic affiliation of a non-terminal Run; a refresh for another Project or Issue SHALL be rejected without changing the Run.

#### Scenario: Epic affiliation changes for the bound Issue

- **WHEN** a non-terminal WorkflowRun bound to `proj_1#42` receives a refresh with Epic `9`
- **THEN** its Project ID and Issue number SHALL remain `proj_1` and `42`
- **AND** its typed Epic number SHALL become `9`

#### Scenario: Context refresh names another Issue

- **WHEN** a WorkflowRun bound to `proj_1#42` receives a refresh for `proj_1#43`
- **THEN** the refresh SHALL be rejected
- **AND** the stored typed context SHALL remain unchanged

#### Scenario: Terminal Run receives an Epic refresh

- **WHEN** a terminal WorkflowRun receives an Issue-context refresh
- **THEN** its typed context SHALL remain unchanged

### Requirement: Stable ownership, event lineage, and query context

WorkflowRun ownership validation, startup-profile resolution, event lineage construction, and persisted read projections SHALL use typed context fields and SHALL NOT parse system annotation values. Workflow events SHALL retain their existing lineage contract: `workflowrunid` and Project ID SHALL be emitted, Issue and Epic values SHALL be emitted when present, and stage lineage SHALL remain limited to stage-bearing event variants. WorkflowRun API responses and project/issue/epic query results SHALL retain their existing field names and values.

#### Scenario: Event emitted by an Issue-backed WorkflowRun

- **WHEN** an Issue-backed WorkflowRun for `proj_1#42` with Epic `7` emits a stage-bearing workflow event
- **THEN** its event extensions SHALL include the existing `workflowrunid`, Project, Issue, Epic, and stage lineage attributes with their existing values

#### Scenario: Event emitted without an Epic affiliation

- **WHEN** an Issue-backed WorkflowRun with no Epic affiliation emits a workflow event
- **THEN** its event extensions SHALL include the existing WorkflowRun, Project, and Issue lineage attributes
- **AND** the Epic lineage attribute SHALL be omitted

### Requirement: Historical annotation-backed state migration

The system SHALL migrate every persisted historical WorkflowRun with valid legacy `projectId` and `issueNumber` annotation values into typed context, transfer any legacy Epic value or persisted Epic affiliation into the typed optional Epic field, and remove the system identity keys from annotations. Migration SHALL preserve all unrelated annotations and SHALL leave the Run readable, ownership-valid, and able to emit the same lineage after reload.

#### Scenario: Historical Run with custom annotations is migrated

- **WHEN** a persisted historical WorkflowRun contains valid legacy system identity annotations and a user-defined annotation
- **THEN** reload or migration SHALL populate the typed Project, Issue, and applicable Epic context
- **AND** the persisted annotations SHALL retain the user-defined annotation
- **AND** the persisted annotations SHALL not retain `projectId`, `issueNumber`, or `epicNumber`

#### Scenario: Stored Epic affiliation supersedes stale legacy state

- **WHEN** a historical WorkflowRun has a persisted current Epic affiliation that differs from its legacy annotation value
- **THEN** migration or reload SHALL retain the persisted current Epic affiliation in the typed Epic field
- **AND** subsequent workflow events SHALL emit that current Epic value
