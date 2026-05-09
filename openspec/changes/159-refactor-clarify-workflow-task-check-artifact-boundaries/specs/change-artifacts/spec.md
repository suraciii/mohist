## MODIFIED Requirements

### Requirement: REQ-CA-001 Durable workflow artifacts are preserved files only

Workflow artifacts SHALL refer only to durable files intended to be preserved with the OpenSpec change or archived workflow context. Build logs, test output, command stdout/stderr, transient error summaries, agent session streams, health gate results, and parsed review verdicts SHALL NOT be reported as durable artifacts.

#### Scenario: Durable artifact paths are reported
- **WHEN** plan or check tasks create `proposal.md`, `specs/`, `design.md`, `tasks.json`, `self-review.md`, `review.md`, or `review-self-check.md`
- **THEN** task results MAY list those paths in `artifacts`

#### Scenario: Transient evidence is not an artifact
- **WHEN** a command, health gate, test run, AI review parse, or agent session produces logs or evidence
- **THEN** that data SHALL be stored in `CheckResult.output`, `StageTaskResult.output`, or execution/session logs
- **AND** it SHALL NOT be listed in `artifacts`

### Requirement: REQ-CA-002 Task execution result supports transient output

Stage task results SHALL support optional transient execution output separately from durable artifact paths. Existing persisted task results without `output` SHALL remain readable.

#### Scenario: Task output records transient details
- **WHEN** a task records command excerpts, error summaries, agent session status, changed-file summaries, or fix evidence
- **THEN** that information SHALL be stored in task `output`
- **AND** older task result records without `output` SHALL still deserialize successfully
