### Requirement: Per-scope variable persistence

Workflow Variables are read and written per scope — Project, Issue, and Run. Each scope is served by its own Store, keyed by its domain identity (`projectId` for Project, `projectId`+`issueNumber` for Issue, `workflowRunId` for Run). A scope's Store MUST NOT depend on a Profile definition or a Profile CRUD class to read or write that scope's variables, because Variables are an independent resource from Workflow Profile.

#### Scenario: reading a scope that has no persisted record

- **WHEN** variables are read for a Project, Issue, or Run that has no persisted variable row
- **THEN** the Store SHALL return an empty `VariableBundle` (no `vars`, no `stages`)

#### Scenario: writing variables to a scope

- **WHEN** variables are set for a scope
- **THEN** the Store SHALL persist the bundle so a subsequent read of the same scope returns the written values

#### Scenario: variable Store is decoupled from Profile CRUD

- **WHEN** the Project, Issue, or Run variable Store is constructed or invoked
- **THEN** it MUST NOT require a Profile collection provider or Profile CRUD dependency, and MUST NOT read or write any Profile definition

### Requirement: Variable shape validation

Every variable write (set or patch) at every scope SHALL be shape-validated before persistence. The top-level `vars` and each `stages.<stage>.vars` MUST be JSON objects when present; any other value type MUST be rejected before persistence.

#### Scenario: non-object vars rejected

- **WHEN** a write carries a top-level `vars` that is a JSON array or scalar
- **THEN** the Store SHALL throw before persisting, and no variable row SHALL be created or modified

#### Scenario: non-object stage vars rejected

- **WHEN** a write carries `stages.<stage>.vars` that is not a JSON object
- **THEN** the Store SHALL throw before persisting

### Requirement: Project-scope agent key sanitization

At the Project write boundary only, the Store SHALL sanitize `vars.agent` and each `stages.<stage>.vars.agent` down to the converged `{model, variant}` whitelist, dropping legacy runtime/liveness keys (e.g. `type`, `livenessQuietThresholdMs`, `runtime`). The sanitized result is what is persisted and returned.

#### Scenario: legacy agent keys dropped on project write

- **WHEN** a Project variable write carries `vars.agent` with `model`, `variant`, and `runtime`
- **THEN** the persisted and returned bundle SHALL contain only `model` and `variant` under `vars.agent`; `runtime` SHALL be absent

#### Scenario: per-stage agent keys sanitized on project write

- **WHEN** a Project variable write carries `stages.plan.vars.agent` with legacy keys
- **THEN** the persisted bundle SHALL contain only `{model, variant}` under that stage's `vars.agent`

### Requirement: Issue-scope runtime rejection

At the Issue write boundary, the Store SHALL reject any write where `vars.agent.runtime` or `stages.<stage>.vars.agent.runtime` is present. Unlike the Project scope, the Issue scope does not silently drop runtime; it refuses the write.

#### Scenario: issue write with agent runtime rejected

- **WHEN** an Issue variable set or patch carries `vars.agent.runtime`
- **THEN** the Store SHALL throw and SHALL NOT persist the change

### Requirement: Patch deep-merge semantics

A variable patch SHALL deep-merge into the scope's current variables: nested object fields are merged field-by-field with the patch overlay taking precedence, and per-stage vars merge independently under their stage key. The Store SHALL return the fully merged result.

#### Scenario: patch overlays a nested field

- **WHEN** the current scope vars are `{ "agent": { "model": "a", "variant": "low" } }` and a patch sets `{ "agent": { "variant": "high" } }`
- **THEN** the merged result SHALL be `{ "agent": { "model": "a", "variant": "high" } }`

#### Scenario: patch merges an unknown stage additively

- **WHEN** the current scope has no `stages.build` and a patch provides `stages.build.vars`
- **THEN** the merged result SHALL contain `stages.build` with the patched vars, and existing stages SHALL be unchanged

### Requirement: Effective variable merge precedence across scopes

Resolving the effective variables for a Run SHALL merge the three scopes in order Project → Issue → Run, where each later scope deep-merges over the previous one and wins on conflicting keys. The resolver MUST read each scope from its own Store and MUST NOT depend on Profile definition resolution.

#### Scenario: run scope overrides issue and project

- **WHEN** Project vars set `agent.variant` to `low`, Issue vars set it to `medium`, and Run vars set it to `high`
- **THEN** the resolved effective `agent.variant` SHALL be `high`

#### Scenario: absent scopes contribute nothing

- **WHEN** only Project variables exist for a Run (no Issue or Run scope)
- **THEN** the resolved effective variables SHALL equal the Project variables

### Requirement: Stage-resolved effective variables

For a given stage, the resolved effective vars SHALL be the workflow-wide effective vars overlaid with that stage's `vars` (stage vars win on conflict). When a stage has no vars, the workflow-wide effective vars are used unchanged.

#### Scenario: stage vars overlay workflow-wide vars

- **WHEN** effective workflow-wide vars are `{ "agent": { "model": "a" } }` and the requested stage's vars are `{ "agent": { "variant": "high" } }`
- **THEN** the stage-resolved vars SHALL be `{ "agent": { "model": "a", "variant": "high" } }`
