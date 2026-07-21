### Requirement: Capability-gated Action results request workflow effects
Only an Action declaring `add-tasks` or `write-vars` SHALL be permitted to request the corresponding workflow effect in a successful Action result. A result from an Action without the required declaration that requests either effect MUST be treated as an invalid Action result, MUST NOT apply the requested effect, and SHALL fail with platform error code `unexpected-error`.

#### Scenario: Reject an undeclared task-addition request
- **WHEN** an Action that does not declare `add-tasks` returns a successful result requesting additional tasks
- **THEN** the Runner SHALL fail the task with error code `unexpected-error`
- **AND** it MUST NOT append any requested task

#### Scenario: Reject an undeclared variable-write request
- **WHEN** an Action that does not declare `write-vars` returns a successful result requesting variable writes
- **THEN** the Runner SHALL fail the task with error code `unexpected-error`
- **AND** it MUST NOT persist any requested variables

### Requirement: The executor owns result-carried task additions
An Action declaring `add-tasks` SHALL request follow-up tasks by including structured task additions in its successful result. The Action MUST NOT call the server to append tasks directly. After the task has completed all executor-owned completion and postcondition checks, the executor SHALL carry the requested task additions in the task result reported to the server, which remains the authority that appends them to the WorkflowRun.

#### Scenario: Load OpenSpec tasks through the task result
- **WHEN** `mohist/openspec-tasks` successfully reads a valid `tasks.json`
- **THEN** its successful result SHALL request the generated follow-up tasks
- **AND** the executor SHALL report those tasks through the completed task result without a direct Action-to-server append call

#### Scenario: A task does not complete after requesting additions
- **WHEN** an Action requests follow-up tasks but the task later fails completion evaluation or an executor postcondition
- **THEN** the Runner MUST NOT report the requested task additions for appending
- **AND** the WorkflowRun MUST NOT receive those additions

### Requirement: The executor owns result-carried variable writes
An Action declaring `write-vars` SHALL request workflow-variable writes by including a JSON object in its successful result. The Action MUST NOT patch Run Variables directly. After the task has completed all executor-owned completion and postcondition checks, the executor SHALL persist the requested variables through the Run Variables patch path before reporting task completion. A variable-patch failure SHALL fail the task and SHALL preserve an actionable failure message.

#### Scenario: Persist declared variable writes after task completion
- **WHEN** an Action declaring `write-vars` completes successfully with a variable-write request
- **THEN** the executor SHALL patch those variables through the Run Variables patch path
- **AND** the task result SHALL retain the Action's existing public output

#### Scenario: Variable persistence fails
- **WHEN** the executor cannot persist variables requested by a completed Action
- **THEN** the task SHALL finish failed
- **AND** the failure message SHALL identify the variable patch failure

### Requirement: Result-carried effects preserve Action contracts
Task additions and variable writes SHALL remain executor-private effect fields rather than public Action output. The Runner SHALL preserve each built-in Action's existing `with` contract, public output, and business error codes while migrating its effects to the result path. The OpenSpec Actions SHALL produce the same follow-up task count, task content, and variable values as before the migration.

#### Scenario: Preserve OpenSpec observable behavior
- **WHEN** an OpenSpec Action runs with the same valid input before and after this change
- **THEN** the WorkflowRun SHALL receive equivalent follow-up tasks and variable values
- **AND** the Action's public output and error codes SHALL remain unchanged
