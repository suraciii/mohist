### Requirement: Runtime-generated task inputs preserve whole-string variable references

Every task produced at runtime through the `addTasks` action callback, recovery handler expansion, approval feedback, or rebase recovery path SHALL persist the literal placeholder string for each `with` or `expect` field whose source value is a whole-string `${{ vars.* }}` reference. The value resolved against the generating action's dispatch-time variables SHALL NOT be substituted into the persisted `TaskRun.WithInput` or `TaskRun.ExpectInput`. Field values that are not whole-string references (objects, arrays, embedded references, or plain literals) SHALL be persisted byte-for-byte as declared.

#### Scenario: openspec-tasks subtask inherits agent placeholder

- **WHEN** a parent task running `mohist/openspec-tasks` declares `with.task.with.options: ${{ vars.agent }}` in the workflow YAML
- **AND** the action generates subtasks from the referenced `tasks.json`
- **THEN** each generated subtask's persisted `TaskRun.WithInput["options"]` SHALL equal the literal string `${{ vars.agent }}`
- **AND** the persisted value SHALL NOT be the agent object resolved from the parent task's dispatch-time variables

#### Scenario: Per-subtask override preserves placeholder

- **WHEN** an entry in `tasks.json` overrides `with.options: ${{ vars.agent }}` at the per-task level
- **THEN** the generated subtask's persisted `TaskRun.WithInput["options"]` SHALL equal the literal placeholder string
- **AND** the per-task override SHALL take precedence over the parent default during action-side merging without either value being expanded

#### Scenario: Recovery handler tasks preserve placeholders declared in YAML

- **WHEN** a workflow task declares a recovery handler whose task template contains `with.options: ${{ vars.agent }}`
- **AND** the handler fires and the runner returns the recovery task via `addTasks`
- **THEN** the persisted recovery `TaskRun.WithInput["options"]` SHALL equal the literal placeholder string

#### Scenario: Approval feedback and rebase recovery tasks preserve placeholders

- **WHEN** the system constructs the default approval feedback task or the rebase recovery task
- **THEN** the persisted `TaskRun.WithInput["options"]` SHALL equal the literal string `${{ vars.agent }}`
- **AND** the construct path SHALL NOT pre-resolve the placeholder against any dispatch-time variables

#### Scenario: Non-reference field values pass through unchanged

- **WHEN** a runtime task's `with` field contains an object, an array, an embedded reference, or a plain literal value
- **THEN** the persisted value SHALL match the source declaration byte-for-byte
- **AND** the runtime task-creation path SHALL NOT expand, partially resolve, or rewrite it

### Requirement: Dispatch-time resolution applies current Effective Stage Variables to runtime-generated tasks

When a runtime-generated task is dispatched — on first dispatch, on `mo issue retry`, or on rerun-from-stage — the dispatch-time template expander SHALL resolve every whole-string `${{ vars.* }}` reference in its `with` and `expect` using the Effective Stage Variables current at the dispatch moment. The previously-dispatched generating action's resolved variables SHALL NOT be carried forward; only the placeholder survives between dispatches.

#### Scenario: Fresh dispatch resolves placeholder to current variable

- **WHEN** a runtime-generated task with `WithInput["options"] = "${{ vars.agent }}"` is dispatched for the first time
- **AND** the current Effective Stage Variables define `vars.agent = { model: "model-a" }`
- **THEN** the dispatch payload delivered to the runner SHALL carry `with.options` as the resolved object `{ model: "model-a" }`
- **AND** the runner SHALL receive no residual `${{ }}` placeholder in that field

#### Scenario: Retry after variable change uses the new value

- **WHEN** a runtime-generated task attempt has failed
- **AND** the user changes Project, Issue, or Run Variables so that `vars.agent.model` changes from `model-a` to `model-b`
- **AND** the user runs `mo issue retry`
- **THEN** the new attempt's dispatch payload SHALL carry `with.options.model = "model-b"`
- **AND** the dispatch SHALL NOT reuse the model value resolved for the prior attempt

#### Scenario: Rerun-from-stage after variable change uses the new value

- **WHEN** a workflow run is rerun from a stage whose prior attempt produced runtime-generated tasks
- **AND** Project, Issue, or Run Variables have changed since the prior attempt
- **THEN** the new stage attempt SHALL re-execute the generating action
- **AND** every runtime task produced in the new attempt SHALL resolve its `${{ vars.* }}` placeholders against the then-current variables at its dispatch

#### Scenario: Stage overlay takes effect on runtime tasks at dispatch

- **WHEN** a runtime-generated task with `WithInput["options"] = "${{ vars.agent }}"` is dispatched in a stage whose Effective Stage Variables override `vars.agent` to a different model than the top-level Workflow Variables
- **THEN** the dispatch payload SHALL resolve `with.options` using the stage-overlaid value
- **AND** the top-level Workflow Variables value SHALL NOT be used in preference to the stage overlay

#### Scenario: Embedded references remain literal at dispatch

- **WHEN** a runtime-generated task's `with` field contains an embedded reference that is not a whole-string `${{ vars.* }}` expression
- **THEN** the dispatch payload SHALL preserve the literal text unchanged
- **AND** the dispatch SHALL NOT attempt partial substitution on it

### Requirement: Statically-declared and runtime-generated tasks are indistinguishable under variable live adjustment

A change to Project, Issue, or Run Variables (top-level or stage overlay) SHALL take effect identically on the next dispatch of any task that has not yet been dispatched, and on any retry, regardless of whether the task was declared statically in the workflow YAML or produced at runtime by an action. Once a task attempt has been dispatched, its persisted input SHALL remain immutable; subsequent variable changes SHALL affect only subsequent dispatches of that task (via retry or rerun) and dispatches of other not-yet-dispatched tasks.

#### Scenario: Static and dynamic tasks both pick up edited variable on retry

- **WHEN** a stage contains both a statically-declared task and a runtime-generated task whose `with.options` is `${{ vars.agent }}`
- **AND** both tasks have failed
- **AND** the user edits the stage's agent model and runs `mo issue retry` on each
- **THEN** both retries SHALL resolve `with.options` against the edited variable
- **AND** both SHALL receive the same new model value

#### Scenario: Already-dispatched task input is not rewritten

- **WHEN** a runtime-generated task has been dispatched with its placeholder resolved against the then-current variables
- **AND** Project, Issue, or Run Variables subsequently change
- **THEN** the persisted `TaskRun.WithInput` SHALL remain unchanged
- **AND** only the next dispatch of that task SHALL observe the new variables

### Requirement: Pre-existing baked runtime tasks are not migrated

Runtime tasks generated before this change whose persisted `TaskRun.WithInput` contains the resolved literal value (rather than the placeholder string) SHALL NOT be rewritten by the system. The live-adjustment guarantee SHALL apply only to tasks generated after this change. Rerun-from-stage on a previously-baked run SHALL produce placeholder-carrying tasks under the new behavior, because the generating action re-executes under the new rules.

#### Scenario: Legacy baked task on retry still uses baked value

- **WHEN** a runtime task was persisted before this change with `WithInput["options"] = { "model": "model-a" }` (the resolved literal)
- **AND** the user changes `vars.agent` and runs `mo issue retry`
- **THEN** the retry SHALL dispatch with `with.options.model = "model-a"`
- **AND** the system SHALL NOT rewrite the persisted input to introduce a placeholder

#### Scenario: Rerun-from-stage on a baked run produces placeholder-carrying tasks

- **WHEN** a workflow run contains pre-existing baked runtime tasks from a prior stage attempt
- **AND** the user runs rerun-from-stage on that stage
- **THEN** the new stage attempt SHALL re-execute the generating action under the post-change behavior
- **AND** every runtime task produced in the new attempt SHALL carry `${{ vars.* }}` placeholders per the new requirement
- **AND** dispatches of those new tasks SHALL resolve against the then-current variables
