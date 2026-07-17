### Requirement: Workflow tasks use one canonical declaration

Every Workflow task SHALL represent `with`, `expect`, `artifacts`, `setVars`, and `recovery` as independent sibling fields. `with` SHALL contain only input owned by the Action selected by `uses`; `expect` SHALL contain Workflow-owned completion requirements. The same task declaration SHALL apply to stage tasks, approval-feedback tasks, nested recovery tasks, retries, and runtime-generated tasks.

#### Scenario: A profile task declares Action input and completion requirements

- **WHEN** a task declares both `with` input and a top-level `expect` beside `artifacts`, `setVars`, or `recovery`
- **THEN** the Workflow Profile SHALL retain each field independently
- **AND** the system MUST NOT copy `expect` into `with`

#### Scenario: Feedback and recovery tasks use the canonical declaration

- **WHEN** an approval-feedback task or a task nested in a recovery handler declares any canonical task field
- **THEN** the system SHALL accept and retain that field with the same meaning as a stage task
- **AND** it MUST NOT use a reduced task shape for those task origins

### Requirement: The canonical declaration survives the complete task lifecycle

A task's top-level `expect` and its other definition fields SHALL remain in their corresponding fields through profile parsing, serialization, persisted loading, WorkflowRun materialization, dispatch, manual retry, automatic `retrySelf`, and runtime task insertion. Dispatch-time template expansion and per-attempt execution state SHALL affect only the dispatched values and execution state, not field ownership. No lifecycle path SHALL move a top-level completion contract into Action Input or silently discard it.

#### Scenario: A custom profile round-trips a top-level completion contract

- **WHEN** a custom Workflow Profile containing a task-level `expect` is saved and loaded again
- **THEN** the loaded task SHALL contain the same task-level completion contract
- **AND** its Action Input SHALL remain unchanged

#### Scenario: Retry preserves the original completion contract

- **WHEN** a task with a top-level `expect` is recreated by a user retry or by recovery `retrySelf`
- **THEN** the new attempt SHALL carry the same `expect` declaration
- **AND** the prior attempt and its declaration SHALL remain unchanged

#### Scenario: A dynamically generated task uses the canonical declaration

- **WHEN** a runtime task producer, including OpenSpec Build-task expansion, adds an agent task
- **THEN** the generated task SHALL use the same sibling `with` and `expect` fields as a profile task
- **AND** generated Action Input MUST NOT contain Workflow completion policy

### Requirement: Legacy agent-task input shapes fail actionably

Profile loading and task validation SHALL reject an inline-agent task that embeds Workflow completion policy in `with.expect` or supplies legacy execution configuration through `with.agent`. For this validation rule, inline-agent tasks include tasks selecting `mohist/opencode` and persisted tasks selecting the legacy `mohist/acp-agent` identifier. The error SHALL identify the task and invalid field and SHALL direct the author to task-level `expect` or explicit `with.options` as applicable. The system MUST NOT ignore, automatically rewrite, or execute the legacy shape.

#### Scenario: Legacy nested completion policy is rejected

- **WHEN** an inline-agent task is loaded with Workflow completion files, markers, or `failIf` under `with.expect`
- **THEN** loading or validation SHALL fail with an error naming that task and `with.expect`
- **AND** the error SHALL direct the author to move the declaration to task-level `expect`

#### Scenario: Legacy agent configuration is rejected

- **WHEN** an inline-agent task is loaded with `with.agent`
- **THEN** loading or validation SHALL fail with an error naming that task and `with.agent`
- **AND** the error SHALL direct the author to bind the selected Action's `options` explicitly

#### Scenario: A persisted or in-flight legacy task reaches dispatch

- **WHEN** a persisted or in-flight inline-agent task containing `with.expect` or `with.agent` bypassed profile ingestion
- **THEN** dispatch SHALL fail with an actionable migration error
- **AND** the system MUST NOT infer, rewrite, or silently drop the legacy fields

### Requirement: Task validation is Action-aware

Validation SHALL distinguish Workflow completion policy and legacy agent configuration from fields genuinely owned by the selected Action. An Action-owned input named `expect` or `agent` SHALL remain valid under `with`; it MUST NOT be rejected solely because an agent Action uses the same field name for a legacy shape. A task SHALL be able to declare an Action-owned `with.expect` and an independent task-level `expect` when the selected Action contract defines that input.

#### Scenario: GitHub PR status keeps its Action-owned expectation

- **WHEN** a `mohist/github-pr-status` check declares `with.expect: merged`
- **THEN** validation SHALL accept `expect` as input owned by that Action
- **AND** the value `merged` SHALL be delivered to the Action unchanged

#### Scenario: Action-owned and Workflow-owned expectations coexist

- **WHEN** a selected Action contract owns a `with.expect` field and the task also declares top-level `expect`
- **THEN** the Action SHALL receive only the explicitly declared `with.expect`
- **AND** Workflow completion SHALL use only the independent top-level `expect`

#### Scenario: Another Action owns an input named agent

- **WHEN** a non-agent Action contract defines an input named `agent` and a task declares that input under `with`
- **THEN** validation SHALL accept the Action-owned input
- **AND** it MUST NOT apply the `mohist/opencode` legacy-shape rule to that task

### Requirement: Built-in profiles conform without product-flow drift

Every built-in Workflow Profile SHALL use the canonical task declaration and the `mohist/opencode` Action contract for its agent tasks. Built-in `variables.agent` defaults SHALL contain no execution-backend discriminator or liveness configuration and SHALL reserve only `model` and optional `variant` meanings. Approval feedback SHALL explicitly bind `options: ${{ vars.agent }}`. Apart from activating previously discarded top-level completion requirements and making approval feedback honor explicit model selection, migration MUST preserve each profile's stage order, approval points, tasks, checks, Action-owned inputs, artifacts, `setVars`, recovery declarations, and delivery behavior.

#### Scenario: Built-in profiles contain no legacy agent-task fields

- **WHEN** the built-in profiles and their nested recovery and Build-task templates are validated
- **THEN** every agent task SHALL use `mohist/opencode` with explicit `options` where model configuration is intended
- **AND** no such task SHALL contain `with.agent` or Workflow completion policy under `with.expect`

#### Scenario: Approval feedback honors the effective model selection

- **WHEN** approval feedback runs for an issue whose effective `vars.agent` selects a model or variant
- **THEN** the feedback task SHALL pass that object through its explicit `with.options` binding
- **AND** it MUST NOT obtain model configuration through hidden injection

#### Scenario: The local built-in keeps direct integration behavior

- **WHEN** `mohist/local` is loaded after migration
- **THEN** it SHALL retain the ordered plan, build, check, and integrate stages with approval after plan and check
- **AND** its integrate stage SHALL retain sequential archive, squash-rebase, base-branch push, and health behavior
- **AND** its existing artifacts, checks, Action-owned inputs, `setVars`, and ordered recovery handlers SHALL remain unchanged except for the canonical agent input and completion fields

#### Scenario: The GitHub PR built-in keeps PR delivery behavior

- **WHEN** `mohist/github-pr` is loaded after migration
- **THEN** it SHALL retain the ordered plan, build, check, and integrate stages with approval after plan and check
- **AND** it SHALL retain draft PR creation, PR identity projection, check-stage push and ready transition, and integrate-stage archive, push, merge, and merge verification
- **AND** its existing artifacts, checks, Action-owned inputs, `setVars`, and ordered recovery handlers SHALL remain unchanged except for the canonical agent input and completion fields

#### Scenario: Previously discarded built-in expectations become effective

- **WHEN** a built-in task declares top-level completion requirements after migration
- **THEN** those requirements SHALL be retained and enforced
- **AND** failure to produce a required proposal, design, or task artifact SHALL fail that task's ordinary completion result
