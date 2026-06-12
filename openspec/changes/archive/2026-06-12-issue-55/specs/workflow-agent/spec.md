## ADDED Requirements

### Requirement: acp-agent preserves file and marker completion expectations
The `mohist/acp-agent` action SHALL continue to consume `with.expect.files` and `with.expect.markers` as private action-level completion requirements. File expectations SHALL verify required files or directories exist, and marker expectations SHALL verify target file content contains accepted marker text.

#### Scenario: File expectation validates completion
- **WHEN** an acp-agent task declares `with.expect.files` with a required path
- **THEN** acp-agent SHALL treat the task as incomplete until the required file or directory exists
- **AND** the expectation SHALL NOT by itself request WorkflowArtifact recording

#### Scenario: Marker expectation validates target file
- **WHEN** an acp-agent task declares `with.expect.markers` with `path` and marker content
- **THEN** acp-agent SHALL require the target file to exist
- **AND** it SHALL require the target file content to contain an accepted marker before reporting completion

### Requirement: acp-agent marker expectations support oneOf values
The `mohist/acp-agent` action SHALL support marker expectations that specify a target `path` and `oneOf` accepted marker values. A task SHALL satisfy that marker expectation when the target file contains any one accepted value.

#### Scenario: Review marker accepts pass or fail
- **WHEN** an acp-agent review task declares `oneOf` values `<promise>PASS</promise>` and `<promise>FAIL</promise>` for `review.md`
- **THEN** acp-agent SHALL treat either marker as satisfying the completion expectation
- **AND** it SHALL report task completion without treating `FAIL` as an action execution failure

#### Scenario: Missing marker keeps asking for required format
- **WHEN** the target file exists but contains none of the accepted marker values
- **THEN** acp-agent SHALL continue requesting the required output format by default
- **AND** the workflow YAML SHALL NOT need a separate retry or ask-again setting for that behavior

#### Scenario: Check verdict remains separate
- **WHEN** acp-agent completes because `review.md` contains `<promise>FAIL</promise>`
- **THEN** the workflow task SHALL be considered completed by the action expectation
- **AND** a later check such as `review-passed` SHALL remain responsible for the workflow pass or fail decision

### Requirement: Actions may report dynamic artifacts
Workflow actions MAY report dynamic artifact outputs in addition to statically declared task-level `artifacts.files`. Dynamic action-produced artifacts SHALL be uploaded by the runner and recorded for the same producing task run when the task result is bound.

#### Scenario: Action reports dynamic artifact output
- **WHEN** an action reports a produced artifact that was not listed in task-level `artifacts.files`
- **THEN** the runner SHALL include that artifact in the upload flow for the task result
- **AND** Mohist SHALL record it as a WorkflowArtifact for the same task run after successful binding
