## ADDED Requirements

### Requirement: Workflow YAML supports approval feedback task configuration

The workflow profile YAML SHALL support an `approval.feedback` section that defines what task to execute when a user requests changes at an approval gate. The configuration SHALL be minimal and SHALL only describe the feedback task identity, not a full feedback schema.

#### Scenario: Default feedback task configuration

- **WHEN** the workflow YAML defines:
  ```yaml
  approval:
    feedback:
      task:
        id: apply-feedback
        title: Apply approval feedback
        uses: mohist/acp-agent
        with:
          session: ${{ stage.name }}
          prompt: ${{ prompts.apply-feedback }}
  ```
- **THEN** the workflow engine SHALL schedule this task when feedback is created
- **AND** the task SHALL use the configured session name and prompt

#### Scenario: Feedback task uses shared task execution primitives

- **WHEN** `approval.feedback.task` is configured
- **THEN** the task SHALL resolve through the standard task loader and handler registries
- **AND** the task execution policy SHALL follow the same contract as other agent-session tasks

#### Scenario: No custom feedback task falls back to built-in default

- **WHEN** the workflow YAML has no `approval.feedback` section
- **THEN** the system SHALL use the built-in `apply-feedback` task with the built-in `apply-feedback.prompt`

### Requirement: Feedback task configuration does not define feedback schema

The workflow YAML `approval.feedback` section SHALL NOT define a feedback schema, data shape, validation rules, or runtime state fields. Feedback is runtime state, not workflow definition data.

#### Scenario: YAML contains only task identity

- **WHEN** the `approval.feedback.task` configuration is inspected
- **THEN** it SHALL contain task id, title, uses, and with configuration
- **AND** it SHALL NOT contain feedback field definitions, validation rules, category enums, severity levels, or data shapes

### Requirement: Prompt reference in feedback task uses standard template variables

The feedback task prompt reference SHALL support the standard workflow template variables including `${{ prompts.apply-feedback }}`, `${{ stage.name }}`, `${{ issue.number }}`, and `${{ project.id }}`.

#### Scenario: Prompt variable substitution

- **WHEN** the feedback task is dispatched
- **THEN** `${{ prompts.apply-feedback }}` SHALL resolve to the built-in or custom apply-feedback prompt content
- **AND** `${{ stage.name }}` SHALL resolve to the current stage name
- **AND** `${{ issue.number }}` SHALL resolve to the current issue number
- **AND** `${{ project.id }}` SHALL resolve to the current project id

### Requirement: Feedback is gateway-scoped, not stage-scoped in YAML

The `approval.feedback` section SHALL be at the workflow root level (shared by all stages with approval gates), not duplicated per-stage. Stage-specific feedback task overrides SHALL NOT be supported initially.

#### Scenario: Single feedback configuration for all stages

- **WHEN** the workflow YAML has one `approval.feedback` section
- **AND** multiple stages have approval gates (Plan, Check)
- **THEN** the same feedback task configuration SHALL apply when feedback is created at any approval gate
- **AND** the `stage.name` template variable SHALL reflect the actual stage where feedback was requested

#### Scenario: Per-stage feedback overrides are not supported

- **WHEN** the workflow YAML is loaded
- **THEN** the system SHALL NOT look for per-stage `approval.feedback` configuration inside individual stage definitions
