## ADDED Requirements

### Requirement: Archived issue detail preserves workflow run history

The issue detail API SHALL return the workflow run reference and all associated execution history for an archived issue exactly as it does for a non-archived issue. Setting `archivedAt` SHALL NOT cause the read path to drop `workflowRunId`, the workflow timeline, artifacts, events, feedback, commits, diffs, or execution context from the response. An archived issue's detail response SHALL be sufficient for a client to render the full workflow execution history.

#### Scenario: Archived issue detail returns the workflow run reference

- **WHEN** a client requests the detail of a `Done` issue that was archived after completing workflow run `wr_1`
- **THEN** the response SHALL include `workflowRunId: "wr_1"`
- **AND** the response SHALL include `archivedAt` set to the archive timestamp
- **AND** no execution-history field present before archiving SHALL be absent after archiving

#### Scenario: Archived issue detail exposes workflow timeline and artifacts

- **WHEN** a client requests the detail (or workflow timeline/artifacts sub-resources) of an archived issue
- **THEN** the workflow timeline, artifacts, events, and feedback SHALL be returned
- **AND** the response SHALL be identical in shape to the non-archived detail response

#### Scenario: Archived issue detail is not treated as an active workflow

- **WHEN** a client requests the detail of an archived issue with a preserved `workflowRunId`
- **THEN** the response SHALL NOT indicate an active/running workflow solely because `workflowRunId` is present
- **AND** any active-workflow indicator SHALL reflect the issue's `Done`/archived status
