### Requirement: Dispatch raw task declarations with an attempt snapshot
Each workflow-task dispatch SHALL carry the persisted `with` and task-level `expect` declarations without expanding template references. This requirement applies equally to statically declared tasks, tasks added by an Action, and recovery continuation tasks. The dispatch SHALL also carry the context required to render that attempt, including its Effective Stage Variables, prompt bodies, runtime context, and applicable failure context.

#### Scenario: A task declaration contains a variable reference
- **WHEN** a task declaring `with.options: ${{ vars.agent }}` and an `expect` reference is dispatched while `vars.agent.model` is `model-a`
- **THEN** the dispatch `with` and `expect` retain their `${{ ... }}` references, and the dispatch context snapshot contains the `model-a` value needed for that attempt

#### Scenario: An Action creates a follow-up task with a template
- **WHEN** an Action reports an `addTasks` task whose `with` or `expect` contains a template reference
- **THEN** the persisted follow-up task and its later dispatch retain the reference rather than a value rendered during the originating Action attempt

### Requirement: A dispatched attempt uses an immutable context snapshot
The context attached to a dispatched attempt SHALL be its rendering authority for the lifetime of that attempt. The Runner MUST NOT fetch or substitute newer Variables, prompts, runtime context, or failure context after receiving the dispatch. Configuration changes after dispatch SHALL affect only tasks that have not yet been dispatched and later attempts, including retries, recovery continuations, and stage reruns.

#### Scenario: Variables change after dispatch
- **WHEN** an attempt is dispatched with `vars.agent.model` set to `model-a` and the stage variable is subsequently changed to `model-b` before the Action is invoked
- **THEN** that dispatched attempt renders and executes with `model-a`

#### Scenario: A retry is dispatched after a variable change
- **WHEN** a prior attempt completes, `vars.agent.model` is changed from `model-a` to `model-b`, and a new retry attempt is dispatched
- **THEN** the retry receives a new context snapshot and renders `${{ vars.agent }}` using `model-b`

### Requirement: Render task input only at the Runner execution boundary
Immediately before Action input validation and invocation, the Runner SHALL render the raw task `with` and task-level `expect` against that dispatch's context snapshot. Rendering SHALL preserve JSON types for whole-value references and recurse through non-deferred objects and arrays. The Runner MUST NOT mutate the raw dispatch declaration, persisted task definition, Action effects, or retry source while rendering.

#### Scenario: A whole-value template resolves to an object
- **WHEN** an Action input declares `options: ${{ vars.agent }}` and the attempt snapshot supplies `vars.agent` as `{ "model": "model-a", "variant": "high" }`
- **THEN** the Action receives `options` as that JSON object rather than a serialized string, while the dispatch declaration still contains `${{ vars.agent }}`

#### Scenario: A required whole-value reference is unresolved
- **WHEN** a non-deferred `with` or `expect` field consists entirely of a template reference that is absent from the attempt snapshot
- **THEN** the attempt SHALL fail without invoking the Action

### Requirement: Validate one rendered Action input channel
The Runner SHALL apply manifest input validation after rendering immediate inputs and before invoking the Action. An Action MUST receive only the resulting validated input and its declared host capabilities; it MUST NOT receive raw `with`, raw task data, the Variables resource, or the complete dispatch context as an additional input channel.

#### Scenario: Rendered input violates the Action manifest
- **WHEN** rendering produces an unknown field, a missing required field, or a value of an invalid manifest type
- **THEN** the attempt SHALL fail as `invalid-input` and the Action SHALL not run

#### Scenario: An Action executes with valid rendered input
- **WHEN** rendering and manifest validation succeed
- **THEN** the Action receives only the validated rendered values and cannot access an alternate raw or dispatch-context input

### Requirement: Respect manifest-deferred input rendering
For a manifest input declared with `render: deferred`, the Runner SHALL preserve that top-level input value exactly through validation and Action invocation. All input fields not declared deferred SHALL be rendered using the attempt snapshot before validation, including nested objects and arrays.

#### Scenario: A deferred input propagates a nested template
- **WHEN** an Action declares `tasks` as `render: deferred` and receives a `tasks` value containing `${{ vars.agent }}`
- **THEN** the Action receives that nested reference unchanged so it can place the declaration in a later task

#### Scenario: A non-deferred nested value contains a template
- **WHEN** a non-deferred object or array input contains `${{ vars.agent }}` at a nested path
- **THEN** the Runner recursively renders that nested value before manifest validation and Action invocation

### Requirement: Render checks at the same execution boundary
Stage checks SHALL retain their declared `with` templates in the dispatch and SHALL be rendered by the Runner against the check attempt's context snapshot before the selected Action is validated and invoked.

#### Scenario: A check binds an Effective Stage Variable
- **WHEN** a stage check declares an Action input using `${{ vars.agent }}`
- **THEN** its Action receives the value rendered from that check dispatch's snapshot while the declared check input remains unmodified
