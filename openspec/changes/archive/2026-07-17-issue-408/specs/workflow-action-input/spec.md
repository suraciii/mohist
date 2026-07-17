### Requirement: Action Input is the explicitly rendered `with` declaration

Before invoking an Action, Workflow SHALL render task `with` independently from task-level `expect`. The selected Action SHALL receive the rendered `with` object as its only Workflow-variable-derived configurable input. A task-level completion contract MUST NOT be copied into Action Input. A field explicitly declared under `with` SHALL remain present when it belongs to the selected Action's own input contract.

#### Scenario: Task completion policy is absent from Action Input

- **WHEN** a task declares `with.prompt` and a top-level `expect`
- **THEN** the Action SHALL receive `prompt` through its input
- **AND** the Action Input MUST NOT contain the task-level `expect`

#### Scenario: An Action-owned field remains explicit input

- **WHEN** the selected Action owns a field named `expect` and the task explicitly declares it under `with`
- **THEN** the Action SHALL receive that field
- **AND** the system MUST NOT replace it with the task-level completion contract

### Requirement: Whole-value variable expansion preserves JSON types

When one template expression occupies an entire value, expansion SHALL replace that value with the resolved JSON value without converting it to text. Objects, arrays, numbers, booleans, strings, and `null` SHALL preserve their JSON types. An expression embedded within surrounding text SHALL remain string interpolation.

#### Scenario: An options object remains an object

- **WHEN** `vars.agent` resolves to `{ "model": "provider/model", "variant": "high" }` and a task declares `options: ${{ vars.agent }}`
- **THEN** rendered Action Input SHALL contain `options` as that JSON object
- **AND** it MUST NOT contain a serialized JSON string

#### Scenario: Non-object whole values retain their types

- **WHEN** whole-value bindings resolve to an array, number, boolean, string, or `null`
- **THEN** each rendered input value SHALL retain the corresponding JSON type and value

#### Scenario: Embedded references interpolate as text

- **WHEN** a variable reference appears within additional literal text rather than occupying the entire value
- **THEN** the rendered value SHALL be a string containing the resolved textual representation

### Requirement: Variables enter Action Input only through explicit bindings

Effective `vars.*` values SHALL affect an Action only where the task's `with` declaration explicitly references them. Workflow MUST NOT synthesize an omitted Action field from a same-named variable, inject `vars.agent` as `agent` or `options`, or deep-merge a same-named variable object into a literal input object. An Action MUST NOT use effective Workflow Variables as fallback for an omitted business or configuration input. Dedicated runtime context such as workspace identity and cancellation remains separate from Workflow Variables and is not implicit Action configuration.

#### Scenario: Omitted options stay omitted

- **WHEN** effective Variables contain `vars.agent` but the task declares no `with.options`
- **THEN** Action Input SHALL contain neither `options` nor a synthesized `agent` field
- **AND** the model or variant in `vars.agent` MUST NOT change that Action execution

#### Scenario: A same-named variable object does not merge into literal input

- **WHEN** `vars.options` contains `{ "variant": "high" }` and the task declares literal `with.options: { "model": "provider/model" }`
- **THEN** the Action SHALL receive only `{ "model": "provider/model" }` for `options`
- **AND** `variant` MUST NOT be added implicitly

#### Scenario: An omitted business input cannot fall back to Variables

- **WHEN** an Action input field is omitted while an effective `vars.*` value exists for the same business fact
- **THEN** the Action SHALL observe the field as omitted
- **AND** it MUST NOT read the effective Variables document to supply that field

### Requirement: Explicit bindings use the effective Variable hierarchy

Project, Issue, Run, and current-Stage Variables SHALL retain their existing precedence and recursive object-merge semantics. An explicit `${{ vars.* }}` binding SHALL resolve from the resulting effective value. Removing implicit Action Input injection MUST NOT change how the effective Variables document itself is computed.

#### Scenario: Explicit options receive the merged effective object

- **WHEN** Project Variables provide `agent.model`, Issue Variables override that model, and current-Stage Variables provide `agent.variant`
- **THEN** `options: ${{ vars.agent }}` SHALL render the Issue-selected model and Stage-selected variant in one object
- **AND** the source Variable documents SHALL remain unchanged

#### Scenario: Unreferenced effective Variables do not affect input

- **WHEN** the same effective Variable object exists but the task does not reference it under `with`
- **THEN** the Action Input SHALL be identical to the declared literal `with` input

### Requirement: Input is resolved at each dispatch and then fixed

Workflow SHALL resolve explicit input bindings using the effective Variables available for that dispatch. A task already dispatched SHALL retain its rendered Action Input. A task not yet dispatched and a newly dispatched retry SHALL use the latest effective Variables, but only for bindings the task explicitly declares.

#### Scenario: Updating a variable affects only later explicit bindings

- **WHEN** a variable changes after one task attempt was dispatched and before a later task or retry is dispatched
- **THEN** the first attempt SHALL retain its original rendered input
- **AND** the later dispatch SHALL receive the updated value at each explicit reference

### Requirement: Unresolved explicit bindings fail before Action execution

If a whole-value Action Input binding references a value that does not exist, dispatch or execution SHALL fail with an actionable error identifying the task and unresolved path. The system MUST NOT pass the unresolved expression through as Action Input and MUST NOT replace it through hidden fallback.

#### Scenario: A required options binding is undefined

- **WHEN** a task declares `options: ${{ vars.agent }}` and `vars.agent` is undefined for that dispatch
- **THEN** the task SHALL fail before the Action is invoked
- **AND** the error SHALL identify `vars.agent` as the unresolved reference
