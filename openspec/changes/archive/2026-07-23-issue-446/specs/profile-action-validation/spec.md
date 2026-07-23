### Requirement: Profile save judges Action usage against the Runner-reported catalog

After the Workflow Definition validator accepts a Definition, the Profile save entry SHALL validate the `uses` and `with` of every task and check against the latest Action catalog reported by a Runner. The catalog SHALL be the sole judge of `uses` identity and Action input contracts at save time. Every task and check position SHALL be judged: stage tasks, stage checks, approval-feedback tasks, and tasks nested inside recovery handlers. The catalog check SHALL operate on the single Runner model; merging catalogs across multiple Runners is out of scope. This catalog-backed check SHALL replace the transitional inline-agent `with` guard in its entirety.

#### Scenario: valid uses and with are accepted
- **WHEN** a Profile save carries tasks and checks whose `uses` name declared Actions and whose `with` fields satisfy those Actions' input declarations
- **THEN** the save SHALL succeed and produce no Action-contract errors

#### Scenario: every task and check position is judged
- **WHEN** an unknown `uses` appears in a stage task, a stage check, an approval-feedback task, and a task nested in a recovery handler
- **THEN** the save SHALL reject with an Action-contract error at each of those positions

#### Scenario: the transitional guard is subsumed
- **WHEN** an inline-agent task such as `mohist/opencode` declares a legacy `with.agent` key that the Action catalog does not declare as an input
- **THEN** the catalog check SHALL reject it as an unknown input field
- **AND** no separate transitional guard SHALL remain to special-case legacy keys

#### Scenario: catalog check does not re-implement Definition rules
- **WHEN** a Definition contains a Definition-language error such as an unknown structural field or a malformed template namespace reference
- **THEN** the catalog check SHALL neither report nor correct it
- **AND** those rules SHALL remain owned solely by the Definition validator

### Requirement: Unknown uses are rejected

A task or check whose `uses` does not name an Action present in the catalog SHALL be rejected at save. The error SHALL identify the task or check by its id, name the Action (`uses` value), carry the YAML path of that task or check, and be labeled as an Action-contract error.

#### Scenario: misspelled uses is rejected
- **WHEN** a task declares `uses: mohist/opencodee` and no Action named `mohist/opencodee` exists in the catalog
- **THEN** the save SHALL reject with an Action-contract error naming `mohist/opencodee`

#### Scenario: error identifies task, action, path, and source
- **WHEN** a check `lint` at `stages[0].checks[0]` declares an unknown `uses`
- **THEN** the error SHALL name the check id `lint`, the Action name, the path `stages[0].checks[0]`, and the Action-contract source

### Requirement: Removed Actions are distinguished from unknown ones

When `uses` names an Action recorded as a tombstone, the save SHALL reject it with a distinct "removed" outcome that surfaces the tombstone's guidance, and MUST NOT report it as a merely unknown Action. A removed Action and an unknown Action SHALL produce distinguishable messages.

#### Scenario: tombstoned uses reports removed with guidance
- **WHEN** a task declares `uses: mohist/acp-agent` and the catalog records `mohist/acp-agent` as a tombstone
- **THEN** the save SHALL reject with a message identifying the Action as removed
- **AND** the error SHALL include the tombstone's guidance text

#### Scenario: removed and unknown produce distinct messages
- **WHEN** one task references a tombstoned Action and another references a name that never existed
- **THEN** the two errors SHALL be distinguishable, the first as removed and the second as unknown

### Requirement: Unknown with fields are rejected

For a task or check whose `uses` resolves to a declared Action, every top-level `with` key that the Action catalog does not declare SHALL be rejected at save, except the engine-reserved `working-directory` key. The error SHALL name the field and the Action.

#### Scenario: unknown with field is rejected
- **WHEN** an Action declares input `command` and a task supplies `with.commmand`
- **THEN** the save SHALL reject naming `commmand` as an unknown input of that Action

#### Scenario: engine-reserved working-directory is not treated as unknown
- **WHEN** a task supplies `with.working-directory` alongside Action-declared inputs
- **THEN** the save SHALL NOT reject `working-directory` as an unknown field

### Requirement: Missing required inputs are rejected

For a resolved Action, a required input that `with` omits SHALL be rejected at save, mirroring dispatch-time validation. The error SHALL name the field. A catalog input is never simultaneously required and defaulted (the manifest forbids the combination), and applying defaults is a dispatch-time concern, so the save-time required check considers only whether the field is present.

#### Scenario: omitted required input is rejected
- **WHEN** an Action declares a required input `prompt` and a task's `with` omits `prompt`
- **THEN** the save SHALL reject naming `prompt` as required

#### Scenario: an optional input may be omitted
- **WHEN** an Action declares an optional input `remote` (defaulted or not) and a task's `with` omits `remote`
- **THEN** the save SHALL NOT reject the omission

### Requirement: Constant-value type mismatches are rejected

For a declared input supplied with a constant value containing no template expression, the value's JSON kind SHALL belong to the finite set of kinds declared in the catalog, using the same exact-kind rule as dispatch-time validation — string, finite number, boolean, non-null object that is not an array, or array — with no coercion, no stringification of objects, and no parsing of numeric or boolean strings. A mismatch SHALL be rejected at save, naming the field, the accepted kinds in canonical order, and the actual kind.

#### Scenario: numeric string for a number input is rejected
- **WHEN** an Action declares number input `timeout` and a task supplies `with.timeout: "30"`
- **THEN** the save SHALL reject identifying `timeout`, expected kind `number`, and actual kind `string`

#### Scenario: object for a string input is rejected
- **WHEN** an Action declares string input `message` and a task supplies an object value for `with.message`
- **THEN** the save SHALL reject and MUST NOT serialize the object into a string to make it match

#### Scenario: a value matching a declared union kind is accepted
- **WHEN** an Action declares input `prompt` accepting `string` or `object` and a task supplies either a string or a non-null object
- **THEN** the save SHALL accept the value without changing it

#### Scenario: an optional explicit null is treated as absent
- **WHEN** an Action declares an optional input and a task supplies an explicit `null` for it
- **THEN** the save SHALL treat the null as absent rather than a type mismatch

#### Scenario: an explicit null on a required input is rejected
- **WHEN** an Action declares a required input `prompt` and a task supplies `with.prompt: null`
- **THEN** the save SHALL reject it as a type mismatch, since `null` matches no declared kind
- **AND** the behavior SHALL mirror dispatch, where a present-but-null required value fails the kind check

### Requirement: Template-expression inputs are validated by field name only

A declared `with` input whose value contains a template expression (`${{ }}`) SHALL be validated only by field name against the catalog at save; its value type SHALL NOT be asserted. Type checking of such inputs SHALL remain the Runner's responsibility after the expression is rendered at dispatch.

#### Scenario: a template-valued declared input is accepted without a type assertion
- **WHEN** an Action declares string input `prompt` and a task supplies `with.prompt: ${{ vars.buildPrompt }}`
- **THEN** the save SHALL accept the input without asserting the rendered value will be a string

#### Scenario: a template value does not excuse an unknown field name
- **WHEN** a task supplies `with.ghost: ${{ vars.x }}` and the Action does not declare `ghost`
- **THEN** the save SHALL reject naming `ghost` as an unknown input

#### Scenario: value-type checking is deferred to dispatch
- **WHEN** a template-valued input would render to a wrong type at runtime
- **THEN** the save SHALL NOT reject it
- **AND** the Runner's dispatch-time validation SHALL remain responsible for rejecting the rendered mismatch

### Requirement: An absent catalog does not reject the save

When no Runner Action catalog is available at save time, the save SHALL NOT be rejected for Action-contract reasons, and the save outcome SHALL state that Action-contract validation was not performed.

#### Scenario: save succeeds without a catalog
- **WHEN** no Runner has reported a catalog and a Profile save otherwise carries a valid Definition
- **THEN** the save SHALL succeed

#### Scenario: the outcome reports validation was skipped
- **WHEN** Action-contract validation is skipped for want of a catalog
- **THEN** the save outcome SHALL explicitly state that Action-contract validation was not performed

### Requirement: Action errors compose with Definition errors by shared path and distinct source

Action-contract errors and Definition-language errors produced during one save SHALL share the same YAML-path convention and SHALL be distinguishable by their source label. Action-contract errors SHALL carry the Action source. The two error sets SHALL be reported together in one result; no rule SHALL be owned by both the Definition validator and the catalog check.

#### Scenario: both error kinds are reported together
- **WHEN** a save carries both a Definition-language error and an Action-contract error
- **THEN** a single rejection SHALL carry both errors

#### Scenario: action errors are labeled with the action source
- **WHEN** an Action-contract error and a Definition-language error share a YAML path
- **THEN** they SHALL remain distinguishable by their source label

### Requirement: Dispatch-time validation remains the authoritative fail-closed boundary

Save-time Action-contract validation is advisory early feedback. It SHALL NOT alter, relax, or bypass the Runner's dispatch-time input validation, which SHALL remain the authoritative fail-closed boundary that validates rendered `with` against the executing Runner's local manifest before any Action execution function runs.

#### Scenario: a profile that passes save is still validated at dispatch
- **WHEN** a Profile that passed save-time Action-contract validation is dispatched
- **THEN** the Runner SHALL still validate the rendered `with` against its local manifest before executing the Action

#### Scenario: dispatch fail-closed behavior is unchanged
- **WHEN** rendered `with` violates the Runner's local manifest at dispatch
- **THEN** the Runner SHALL fail the task or check with `invalid-input` and MUST NOT invoke the Action execution function
