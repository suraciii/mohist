### Requirement: Rendered Action input is validated before execution

For every task and individual check, the Runner SHALL expand the dispatch's `with` templates and then validate the resulting top-level input object against the selected Action manifest before invoking its execution function. The Action-owned rendered input made available for execution MUST have passed manifest validation and contain declared defaults. Validation MUST NOT narrow the Action's existing execution context or replace any existing implicit Variable reads in this change.

#### Scenario: Validate a task before Action execution
- **WHEN** a task's templates resolve to a `with` object that violates the selected Action manifest
- **THEN** the Runner SHALL fail validation before invoking the Action execution function

#### Scenario: Validate an individual check before Action execution
- **WHEN** a check's templates resolve to a `with` object that violates the selected Action manifest
- **THEN** that check SHALL fail validation before invoking the Action execution function
- **AND** the check failure SHALL retain the structured validation error

#### Scenario: Execute with valid rendered input
- **WHEN** all templates resolve and the rendered `with` object satisfies the selected Action manifest
- **THEN** the Runner SHALL invoke the Action exactly once with the validated input

### Requirement: Unknown input fields fail explicitly

Every top-level key in rendered `with` MUST be declared by the selected Action manifest or be the engine-reserved `working-directory` key. Any other key SHALL fail the task or individual check with structured platform error code `invalid-input`; the error message MUST identify the unknown field. Unknown fields MUST NOT be ignored or passed to the Action execution function.

#### Scenario: Reject a misspelled input field
- **WHEN** rendered `with` contains `commmand` but the selected Action declares `command` and not `commmand`
- **THEN** the task or individual check SHALL fail with error code `invalid-input`
- **AND** the error message SHALL identify `commmand` as unknown
- **AND** the Action execution function MUST NOT run

#### Scenario: Accept the engine-reserved working directory
- **WHEN** rendered `with` contains `working-directory` and all Action-owned fields satisfy the manifest
- **THEN** the Runner SHALL accept `working-directory` as engine input even though the Action manifest does not declare it
- **AND** the Action execution function SHALL receive the resolved work directory through its existing execution context rather than as an Action-owned input

### Requirement: Required inputs are enforced

If a manifest marks an input as required, rendered `with` MUST provide that field with a value matching its declared type. An omitted required field SHALL fail the task or individual check with structured platform error code `invalid-input`; the error message MUST identify the field and state that it is required. The Action execution function MUST NOT run after this failure.

#### Scenario: Reject an omitted required input
- **WHEN** an Action declares required string input `prompt` and rendered `with` omits `prompt`
- **THEN** the task or individual check SHALL fail with error code `invalid-input`
- **AND** the error message SHALL identify `prompt` as required
- **AND** the Action execution function MUST NOT run

### Requirement: Input types use exact JSON kinds

A supplied input value SHALL satisfy its manifest type only when its rendered JSON kind is the declared kind: string, finite number, boolean, non-null object that is not an array, or array. The Runner MUST NOT stringify objects, parse numeric strings, parse boolean-like strings, or otherwise coerce a supplied value to make it match. A mismatch SHALL fail with structured platform error code `invalid-input`, and the error message MUST identify the field, its expected type, and its actual type. Manifest validation applies to the declared top-level field and container type; constraints within an accepted object or array remain part of the Action's own semantics.

#### Scenario: Reject a numeric string for a number input
- **WHEN** an Action declares number input `timeout` and rendered `with.timeout` is the string `"30"`
- **THEN** the task or individual check SHALL fail with error code `invalid-input`
- **AND** the error message SHALL identify `timeout`, expected type `number`, and actual type `string`
- **AND** the Action execution function MUST NOT run

#### Scenario: Reject an object for a string input
- **WHEN** an Action declares string input `message` and rendered `with.message` is an object
- **THEN** the task or individual check SHALL fail with error code `invalid-input`
- **AND** the Runner MUST NOT serialize that object into a string

#### Scenario: Accept an object without imposing a nested schema
- **WHEN** an Action declares object input `options` and rendered `with.options` is a non-null JSON object
- **THEN** the top-level manifest type check SHALL accept `options`
- **AND** the Action SHALL remain responsible for any constraints on fields inside that object

### Requirement: Defaults are applied centrally

When rendered `with` omits an input with a manifest default, the Runner SHALL supply the declared default before invoking the Action. A caller-supplied value that matches the declared type SHALL take precedence over the default. The Runner MUST validate supplied values rather than replacing invalid supplied values with defaults.

#### Scenario: Apply an omitted input default
- **WHEN** an Action declares optional string input `remote` with default `origin` and rendered `with` omits `remote`
- **THEN** the Action execution function SHALL receive `remote` with value `origin`

#### Scenario: Preserve a valid explicit value
- **WHEN** an Action declares input `remote` with default `origin` and rendered `with.remote` is the string `upstream`
- **THEN** the Action execution function SHALL receive `remote` with value `upstream`

#### Scenario: Reject an invalid explicit value instead of defaulting
- **WHEN** an Action declares number input `timeout` with a numeric default and rendered `with.timeout` is a boolean
- **THEN** the task or individual check SHALL fail with error code `invalid-input`
- **AND** the Runner MUST NOT substitute the default
- **AND** the Action execution function MUST NOT run

### Requirement: Input validation failures participate in task recovery

A task input validation failure SHALL enter the same structured result and recovery matching path as other task failures. Recovery conditions SHALL be able to match `error.code=invalid-input`; if no eligible recovery handler handles the result, the task SHALL finish failed with the original validation error.

#### Scenario: Match recovery on invalid input
- **WHEN** task input validation produces `invalid-input` and an eligible recovery handler matches `error.code=invalid-input`
- **THEN** the Runner SHALL select that recovery handler according to the task's recovery rules
- **AND** the original validation error SHALL remain available as the recovery failure context

#### Scenario: Invalid input remains failed without recovery
- **WHEN** task input validation produces `invalid-input` and no eligible recovery handler matches
- **THEN** the task SHALL finish failed with error code `invalid-input`
- **AND** the error message SHALL preserve the actionable field-specific reason
