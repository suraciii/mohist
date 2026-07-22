### Requirement: Unified variable command surface

`project variable`, `issue variable`, and `run variable` SHALL each expose the `list`, `get`, `set`, and `unset` verbs. The three scopes SHALL share one key-value language with identical key path, stage, and value-type rules; only the addressing differs.

#### Scenario: All three scopes expose the four verbs
- **WHEN** the command tree is inspected for `project`, `issue`, and `run`
- **THEN** each scope exposes a `variable` group containing exactly `list`, `get`, `set`, and `unset` leaves

#### Scenario: Legacy variable flags are removed
- **WHEN** help is read for `project` and `issue` workflow config commands
- **THEN** the `--var`, `--stage-var`, and `--vars-file` flags are absent, and the only variable write path is the `variable set` leaf

### Requirement: Dotted key path addressing

Every `variable` leaf SHALL address values by a dot-separated key path identical to the `${{ vars.* }}` template path. A multi-segment path (`agent.model`) SHALL traverse nested object fields.

#### Scenario: Nested key path is read and written consistently
- **WHEN** `issue variable set 42 agent.model openai/gpt-5` is run, then `issue variable get 42 agent.model` is run
- **THEN** the get returns `openai/gpt-5`, matching the same nested path used by `${{ vars.agent.model }}`

### Requirement: Stage selection via --stage

Each scope's `variable` leaf SHALL accept `--stage <stage>`. When `--stage` is present, the command SHALL read or write that scope's Stage Variables for the named stage. When `--stage` is absent, the command SHALL read or write that scope's workflow-wide Variables.

#### Scenario: Stage-scoped set is isolated from workflow-wide value
- **WHEN** `issue variable set 42 review.strict true` and `issue variable set 42 review.strict --value-json true --stage check` are both run
- **THEN** a workflow-wide `get 42 review.strict` returns the string `true`, while `get 42 review.strict --stage check` returns the boolean `true`

### Requirement: Positional string value has no type coercion

`set <key> <value>` SHALL store the positional value verbatim as a JSON string. No positional value SHALL be implicitly converted to boolean, number, object, or array.

#### Scenario: Numeric-looking positional value stays a string
- **WHEN** `issue variable set 42 change.prNumber 42` is run and then read back
- **THEN** the stored value is the string `"42"`, not the number `42`

### Requirement: Typed value via --value-json and mutual exclusion

`set <key> --value-json <json>` SHALL preserve the parsed JSON type (boolean, number, object, or array). The positional value and `--value-json` SHALL be mutually exclusive, and exactly one SHALL be required for `set`.

#### Scenario: Boolean is preserved through --value-json
- **WHEN** `issue variable set 42 review.strict --value-json true --stage check` is run and read back
- **THEN** the stored value is the boolean `true`

#### Scenario: Both value inputs rejected locally
- **WHEN** `set <key> <value> --value-json <json>` is invoked with both inputs
- **THEN** the command fails locally with a usage error and exit code 2, without contacting any service

#### Scenario: Neither value input rejected locally
- **WHEN** `set <key>` is invoked without a positional value and without `--value-json`
- **THEN** the command fails locally with a usage error and exit code 2, without contacting any service

### Requirement: unset re-inherits from the parent scope

`unset` SHALL delete the current scope's workflow-wide or Stage value for the given key. After `unset`, reading the effective value SHALL inherit the parent scope's value for that key. The persisted Variables document SHALL NOT retain a `null` to mask inheritance.

#### Scenario: unset restores an inherited value
- **WHEN** a Project has `agent.model` set, an Issue overrides it, then `issue variable unset 42 agent.model` is run
- **THEN** the Issue scope no longer declares `agent.model`, and effective resolution returns the Project value

#### Scenario: unset on a stage value restores that stage's inheritance
- **WHEN** `issue variable unset 42 agent.variant --stage check` is run
- **THEN** the Issue's `check` Stage Variables no longer declare `agent.variant`, and that stage inherits from prior scopes

### Requirement: Scope-local list and get

A scope's `list` and `get` SHALL return only that scope's own stored Variables and SHALL NOT merge values from other scopes. `--effective` SHALL NOT be accepted on `project variable` or `issue variable`.

#### Scenario: Project get does not surface Issue or Run values
- **WHEN** Project has no own value for `change.prNumber` but Run does
- **THEN** `project variable get change.prNumber` reports the key absent in the Project scope, not the Run value

#### Scenario: effective flag rejected outside run
- **WHEN** `project variable list --effective` or `issue variable get <key> --effective` is invoked
- **THEN** the command fails locally with a usage error and exit code 2

### Requirement: Run-only effective read

Only `run variable` SHALL offer an `--effective` option on `list` and `get`. With `--effective`, the command SHALL return the read-only merge of Project → Issue → Run; with `--stage`, it SHALL return the Effective Stage Variables. `--effective` SHALL be read-only and SHALL NOT be combinable with `set` or `unset`.

#### Scenario: effective list returns the merged value
- **WHEN** `run variable list <run> --effective` is run where Project sets `a=1` and Run sets `b=2`
- **THEN** the merged result contains both `a=1` and `b=2`

#### Scenario: effective get by stage applies stage overlay
- **WHEN** `run variable get <run> agent.variant --effective --stage check` is run
- **THEN** the returned value reflects the Effective Stage Variables for `check`, after applying stage overlays in scope order

#### Scenario: effective cannot write
- **WHEN** `run variable set <run> <key> <value> --effective` is invoked
- **THEN** the command fails locally with a usage error and exit code 2

### Requirement: Run target resolution

`run variable` leaves SHALL accept exactly one target: a positional WorkflowRun ID, or `--issue <number>`. Providing both or neither SHALL be a local usage error that fails before any service call.

#### Scenario: Issue number resolves to the bound run
- **WHEN** `run variable list --issue 42` is run
- **THEN** the command resolves issue 42's bound WorkflowRun and lists its Run Variables

#### Scenario: Both target inputs rejected locally
- **WHEN** `run variable get wr_abc 42 agent.model` (both ID positional and issue implied) is attempted
- **THEN** the command fails locally with a usage error and exit code 2, without a service call

### Requirement: Shared CLI contract conformance

The `variable` commands SHALL follow the shared CLI contract: a single `--project <name-or-id>` input for Project-scoped selection, `--json <fields>` for output field selection, results exclusively on stdout with diagnostics exclusively on stderr, and the standard exit outcomes. `--json` SHALL remain output-only and SHALL NOT be interpretable as a value input to `set`.

#### Scenario: --json selects output fields
- **WHEN** `run variable list <run> --json vars` is run
- **THEN** stdout contains only the `vars` field of the Variables resource

#### Scenario: --json is not a value input
- **WHEN** `set <key> --json <json>` is attempted as a value
- **THEN** the command does not treat `--json` as the stored value; value input still requires the positional value or `--value-json`

### Requirement: Local input validation before remote calls

Invalid key paths, missing or duplicated value inputs, and invalid target combinations SHALL be rejected locally with a specific diagnostic on stderr and a non-zero exit code, without contacting any service.

#### Scenario: Invalid key path rejected locally
- **WHEN** `set` is invoked with a malformed key path
- **THEN** the command fails locally with a usage error and exit code 2, and no service request is made
