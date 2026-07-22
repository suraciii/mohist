### Requirement: Variables have independent resource owners
Effective Workflow Variables SHALL be resolved only from the Project, Issue, and WorkflowRun Variables resources. Profile and Definition parsing, including live stage reads, MUST NOT extract, merge, or otherwise use embedded Definition Variables.

#### Scenario: Resolve Variables for a selected Profile
- **WHEN** a Project, Issue, or built-in Profile is selected for a WorkflowRun
- **THEN** its effective Variables are resolved without consulting the Profile or Definition for Variables

### Requirement: Variables preserve scope and stage precedence
Variable resolution SHALL deep-merge Project, Issue, and WorkflowRun Variables in that order, with each later resource overriding earlier values. For a selected stage, stage-specific values from those resources SHALL overlay the merged top-level values in the same resource order.

#### Scenario: Resolve conflicting scoped values for a stage
- **WHEN** Project, Issue, and WorkflowRun Variables each provide a value and one or more provide a value for the current stage
- **THEN** the effective value follows Project, then Issue, then WorkflowRun precedence, with selected-stage values overriding the merged top-level values

### Requirement: Built-in runs initialize required independent Variables
When a built-in workflow starts without explicit values, its Issue Variables SHALL contain `agent` as an empty object and its WorkflowRun Variables SHALL contain `archive` as an empty string marked as an initialization default. An initialization default MUST resolve below explicit Project, Issue, and selected-stage values; an explicit WorkflowRun write to `archive` MUST replace the default and retain normal WorkflowRun precedence.

#### Scenario: Start a built-in workflow without variable configuration
- **WHEN** a built-in workflow starts and no scope supplies `agent` or `archive`
- **THEN** `vars.agent` resolves to an empty object and `vars.archive` resolves to an empty string

#### Scenario: Resolve an explicit lower-scope archive value
- **WHEN** a WorkflowRun contains only its initialized `archive` default and Project, Issue, or the selected stage provides `archive`
- **THEN** `vars.archive` resolves to that explicit Project, Issue, or stage value rather than the initialized empty string

#### Scenario: Replace the archive default through a Run write
- **WHEN** `setVars` or an explicit WorkflowRun update writes `archive` after initialization
- **THEN** the initialized-default marker is removed and the written value participates as an explicit WorkflowRun top-level value, subject to the existing selected-stage overlay rules

### Requirement: Dispatch reads current WorkflowRun Variables
Workflow task dispatch, retries, and stage re-entry SHALL resolve Variables from the current WorkflowRun Variables resource rather than a snapshot or embedded Definition Variables. Values written by `setVars` MUST be visible to subsequent dispatches.

#### Scenario: Retry after the archive task updates archive
- **WHEN** an archive task writes `vars.archive` and a related task is retried or its stage is entered again
- **THEN** the subsequent dispatch resolves the updated `vars.archive` value from WorkflowRun Variables
