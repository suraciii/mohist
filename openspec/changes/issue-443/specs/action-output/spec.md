### Requirement: Action success output has one structured shape

Every successful Action SHALL produce either a JSON object or `null` as its public output. The runner SHALL preserve that value as structured JSON through task completion, runner reporting, server receipt, and `TaskRun` persistence; no boundary SHALL serialize the object into a string and later parse it to recover the public output. Output object fields SHALL remain Action-owned and SHALL NOT require a field declaration or platform-wide output schema.

#### Scenario: Object output remains structured end to end

- **WHEN** an Action succeeds with output `{ "prNumber": 42, "prUrl": "https://example.test/pr/42" }`
- **THEN** the runner report SHALL carry that JSON object rather than a string containing serialized JSON
- **AND** the persisted task output SHALL contain the same fields and JSON value types

#### Scenario: Null is a valid success output

- **WHEN** an Action succeeds without public output
- **THEN** its public output SHALL be `null`
- **AND** the task SHALL NOT fail solely because the Action produced `null`

#### Scenario: Serialized object text is not accepted as structured output

- **WHEN** an Action reports a successful output whose value is the string `{"answer":42}`
- **THEN** the task SHALL fail with an actionable diagnostic stating that successful Action output must be an object or `null`
- **AND** the system MUST NOT parse the string into an object or persist it as a wrapped string output

#### Scenario: Non-object JSON value fails the task

- **WHEN** an Action reports a successful output that is an array, string, number, or boolean
- **THEN** the task SHALL fail with an actionable output-shape diagnostic
- **AND** the invalid value MUST NOT be exposed as successful task output

### Requirement: core/process exposes named output fields

On successful command execution, `core/process` SHALL return an output object with exactly the public fields `stdout` and `exitCode`. `stdout` SHALL contain the command's standard output with leading and trailing whitespace removed, and `exitCode` SHALL contain the numeric process exit code.

#### Scenario: Successful process output is addressable

- **WHEN** a `core/process` command succeeds with standard output `release-ready` and exit code `0`
- **THEN** the Action output SHALL equal `{ "stdout": "release-ready", "exitCode": 0 }`
- **AND** `output.stdout` SHALL resolve to `release-ready`
- **AND** `output.exitCode` SHALL resolve to the number `0`

### Requirement: Other built-in Action output contracts are preserved

Every built-in Action other than `core/process` SHALL return its established public output as a structured object with unchanged field names, values, JSON types, and meanings. The output MUST NOT add a transport wrapper around those Action-owned fields.

#### Scenario: Existing built-in output fields remain unchanged

- **WHEN** a built-in Action other than `core/process` succeeds with its established output fields
- **THEN** each established field SHALL remain available under the same name with the same JSON value and type
- **AND** the output SHALL be the Action-owned object itself rather than a transport wrapper or serialized string

### Requirement: setVars projects structured output atomically

`setVars` SHALL resolve each declared source path directly against the successful Action output object after the Action returns and before the task reports completion. If every source path exists, the runner SHALL patch the corresponding Run Variables with the resolved JSON values. If any source path is absent, or the Action output is `null`, the task SHALL fail with a diagnostic naming the missing source path and the Run Variables SHALL remain unchanged.

#### Scenario: Process fields are projected into Run Variables

- **WHEN** a successful `core/process` task returns `{ "stdout": "release-ready", "exitCode": 0 }`
- **AND** its `setVars` maps `release.result` from `output.stdout` and `release.exitCode` from `output.exitCode`
- **THEN** Run Variables SHALL contain `release.result = "release-ready"` and `release.exitCode = 0`
- **AND** the task SHALL complete successfully

#### Scenario: Missing source field fails without a partial patch

- **WHEN** `setVars` maps one source path that exists and another source path `output.missing` that is absent from the Action output
- **THEN** the task SHALL fail with a diagnostic naming `output.missing`
- **AND** none of that task's projected Run Variable changes SHALL be applied

#### Scenario: Null output cannot satisfy setVars

- **WHEN** an Action succeeds with `null` output and the task declares a `setVars` source path `output.value`
- **THEN** the task SHALL fail with a diagnostic naming `output.value`
- **AND** Run Variables SHALL remain unchanged

### Requirement: Output consumers read the same structured object

Task-output references and recovery `when: output.*` matching SHALL read the same structured object produced by the Action and retained as the task output. These consumers MUST NOT maintain independent string-parsing or string-wrapping behavior, and whole-value task-output references SHALL preserve the referenced JSON type.

#### Scenario: Later task reads core/process fields

- **WHEN** task `build` completes through `core/process` with output `{ "stdout": "artifact.zip", "exitCode": 0 }`
- **AND** a later task uses `${{ tasks.build.outputs.stdout }}` and `${{ tasks.build.outputs.exitCode }}` as whole-value references
- **THEN** the references SHALL resolve to `"artifact.zip"` and the number `0`, respectively

#### Scenario: Recovery matches an output field

- **WHEN** an Action's final structured output contains `{ "promise": "FAIL" }`
- **AND** the task has remaining recovery budget and declares `when: output.promise=FAIL`
- **THEN** the recovery handler SHALL match the `promise` field from that output object
- **AND** the declared recovery work SHALL be scheduled

### Requirement: Task details expose structured output

Task status and timeline APIs SHALL expose task output as a JSON object or `null`, and task-detail views SHALL render the structured output without reparsing a JSON string. Existing structured JSON presentation and field visibility SHALL remain unchanged for valid built-in Action outputs.

#### Scenario: Completed task output is displayed

- **WHEN** a completed task has persisted output `{ "stdout": "release-ready", "exitCode": 0 }`
- **THEN** the task status and timeline API output field SHALL contain that JSON object rather than serialized object text
- **AND** the task detail view SHALL display both `stdout` and `exitCode` with their values

#### Scenario: Null output has no fabricated display value

- **WHEN** a completed task has `null` output
- **THEN** the task status and timeline API output field SHALL be `null`
- **AND** the task detail view MUST NOT fabricate an empty object or wrapped string output
