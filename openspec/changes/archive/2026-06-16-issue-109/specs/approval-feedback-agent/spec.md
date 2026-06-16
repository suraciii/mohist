## ADDED Requirements

### Requirement: Feedback task dispatch includes minimal approvalFeedback context

When scheduling an `apply-feedback` task, the dispatch context SHALL include a minimal `approvalFeedback` object with id, stage, creation timestamp, a short summary of the feedback, and a CLI command for reading the authoritative feedback payload. The full feedback body SHALL NOT be inlined into the dispatch context or the prompt.

#### Scenario: Dispatch context shape

- **WHEN** the workflow schedules an `apply-feedback` task
- **THEN** the task input SHALL include an `approvalFeedback` object
- **AND** the object SHALL contain `id`, `stage`, `createdAt`, `summary`, and `command`
- **AND** `command` SHALL be a stable CLI invocation such as `mo issue feedback show <issue-number> --feedback <id> --project-id <project-id> --output json`
- **AND** the user's full feedback body SHALL NOT be present in the dispatch context

#### Scenario: Summary is a short preview only

- **WHEN** the `approvalFeedback.summary` field is generated
- **THEN** it SHALL be a short human-readable preview of the feedback body
- **AND** agents SHALL be instructed to read the full body through the CLI command

### Requirement: Built-in apply-feedback.prompt instructs agent to read feedback via CLI

The system SHALL include a built-in `apply-feedback.prompt` that instructs the agent to read feedback through `mo issue feedback show --output json`, apply the feedback as required input, write a concise resolution summary, and leave approval decisions to Mohist and the user.

#### Scenario: Prompt instructions

- **WHEN** the built-in `apply-feedback.prompt` is rendered
- **THEN** it SHALL instruct the agent to read feedback through `mo issue feedback show ${{ issue.number }} --feedback ${{ approvalFeedback.id }} --project-id ${{ project.id }} --output json`
- **AND** it SHALL instruct the agent to read the current issue, stage artifacts, and relevant changed files
- **AND** it SHALL instruct the agent to treat the feedback as required input, not optional commentary
- **AND** it SHALL instruct the agent to apply only the changes needed to address the feedback
- **AND** it SHALL instruct the agent to write a concise feedback resolution summary
- **AND** it SHALL instruct the agent to not approve the stage itself

#### Scenario: Prompt explains why the task exists

- **WHEN** the prompt is rendered
- **THEN** it SHALL state that the task exists because the user requested changes at a specific approval gate
- **AND** the stage name SHALL be referenced from `${{ stage.name }}`

### Requirement: apply-feedback is the default feedback task

When a user requests changes and no custom feedback task is configured in the workflow profile, the system SHALL use the built-in `apply-feedback` task.

#### Scenario: Default task used when no custom config

- **WHEN** a user requests changes
- **AND** the workflow profile has no custom `approval.feedback.task` configuration
- **THEN** the system SHALL schedule the built-in `apply-feedback` task
- **AND** the task SHALL use the built-in `apply-feedback.prompt`

#### Scenario: Custom task overrides default

- **WHEN** a user requests changes
- **AND** the workflow profile defines a custom `approval.feedback.task`
- **THEN** the system SHALL schedule the configured custom task instead of the built-in `apply-feedback`

### Requirement: Agent does not approve the stage after applying feedback

The apply-feedback agent task SHALL apply the requested changes and write a resolution summary. It SHALL NOT approve the stage or advance the workflow. Approval SHALL remain a user decision through the normal approval gate.

#### Scenario: Agent leaves approval to Mohist

- **WHEN** the apply-feedback agent task completes
- **THEN** the stage SHALL return to approval via the normal workflow path
- **AND** the agent SHALL NOT set approval state or advance the stage
- **AND** the user SHALL see a new approval request with the feedback resolution visible in history

### Requirement: After feedback application checks rerun before re-approval

When the apply-feedback task completes, the workflow SHALL rerun relevant checks before requesting approval again. The stage SHALL not enter awaiting-approval state until checks pass.

#### Scenario: Checks rerun after feedback application

- **WHEN** the `apply-feedback` task completes successfully
- **THEN** the workflow SHALL rerun the stage checks that were previously passing
- **AND** the stage SHALL request approval only after all required checks pass again
- **AND** the previous approval evidence SHALL be invalidated

#### Scenario: Failed check after feedback blocks re-approval

- **WHEN** checks rerun after feedback application
- **AND** a check fails
- **THEN** the stage SHALL enter the normal check failure repair path
- **AND** approval SHALL NOT be requested until the failure is resolved
