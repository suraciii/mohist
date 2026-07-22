### Requirement: Preserve the original declaration for self-retry
When a matching recovery handler requests `retrySelf`, the generated retry task SHALL copy the triggering task's raw `with` and task-level `expect` declarations, including template references. It MUST NOT copy values rendered for the triggering attempt. The self-retry SHALL also preserve the task identity fields, artifacts, `setVars`, recovery declaration, and the decremented remaining recovery budget.

#### Scenario: A self-retry follows a rendered model selection
- **WHEN** a task with `with.options: ${{ vars.agent }}` is rendered as model-a and its matching recovery handler requests `retrySelf`
- **THEN** the generated retry task persists `with.options` as `${{ vars.agent }}` rather than the model-a object

#### Scenario: A self-retry preserves the completion contract
- **WHEN** a task with a task-level `expect` declaration triggers a `retrySelf` recovery handler
- **THEN** the generated retry task carries the original unrendered `expect` declaration alongside its original unrendered `with` declaration

### Requirement: Self-retry renders against its own dispatch snapshot
Each self-retry SHALL become a new attempt whose raw declaration is rendered only with that retry's dispatch snapshot. A variable change made before the retry is dispatched SHALL be reflected in the retry, while the rendered values of the triggering attempt SHALL have no effect on it.

#### Scenario: Recovery retry uses an updated model
- **WHEN** an attempt executes `${{ vars.agent }}` as model-a, triggers `retrySelf`, the stage's `vars.agent` is changed to model-b, and the self-retry is dispatched
- **THEN** the retry's Action invocation and recorded Session model use model-b

#### Scenario: Manual retry follows a recovery-created attempt
- **WHEN** a recovery-created self-retry has retained `${{ vars.agent }}` and a user changes the stage variable before manually retrying that failed retry
- **THEN** the manual retry retains the template declaration and renders it using the variable value in the manual retry's dispatch snapshot

### Requirement: Retain recovery behavior apart from retry input source
Recovery SHALL continue to select handlers from the triggering result's output and error context, expand only `${{ failure.* }}` references in handler-created task declarations against that triggering attempt, and add handler tasks before an optional self-retry. A matching recovery SHALL decrement the remaining budget once; an exhausted or non-matching recovery SHALL not create follow-up tasks. A manual retry SHALL begin a new recovery round with the configured budget.

#### Scenario: Handler task uses triggering failure output
- **WHEN** a matching recovery handler creates a task containing `${{ failure.output.changeId }}` and `${{ vars.agent }}`
- **THEN** the handler-created task substitutes `failure.output.changeId` from the triggering attempt while retaining `${{ vars.agent }}` for its own later dispatch

#### Scenario: Recovery budget is exhausted
- **WHEN** a task reaches a matching recovery handler with no remaining recovery budget
- **THEN** the Runner SHALL not add handler tasks or a self-retry and SHALL preserve the ordinary result outcome

#### Scenario: Manual retry starts a new recovery round
- **WHEN** a task has exhausted automatic self-retries and a user manually retries it
- **THEN** the new attempt SHALL start with the configured recovery budget rather than the exhausted remaining value
