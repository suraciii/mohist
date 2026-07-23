### Requirement: Clean Variables resource paths

Project, Issue, and Run Variables SHALL be exposed as resources under clean `/variables` paths, not under `workflow-profile/variables`:
- Project: `/api/projects/{projectRef}/variables`
- Issue: `/api/projects/{projectRef}/issues/{number}/variables`
- Run: `/api/workflow-runs/{workflowRunId}/variables`

#### Scenario: Run variables are addressed on the clean path
- **WHEN** a client requests `GET /api/workflow-runs/{id}/variables`
- **THEN** the Run scope's own Variables are returned

#### Scenario: Legacy workflow-profile variable routes are gone
- **WHEN** a client requests `/api/workflow-runs/{id}/workflow-profile/variables`
- **THEN** the route is not mapped (the resource lives at `/variables`)

### Requirement: Scope-local read and write

`GET` SHALL return only the addressed scope's own stored Variables without merging other scopes. `PUT` SHALL replace that scope's entire Variables document. `PATCH` SHALL deep-merge a partial Variables document into that scope. A `null` value within a PATCH body SHALL act as a delete instruction that clears the addressed field in the current scope; `null` SHALL NOT be persisted.

#### Scenario: GET returns only the addressed scope
- **WHEN** Project has `a=1` and Run has `b=2`, and `GET /api/projects/{id}/variables` is requested
- **THEN** the response contains only Project's own values, not Run's `b`

#### Scenario: PATCH deep-merges one scope
- **WHEN** `PATCH /api/workflow-runs/{id}/variables` is sent with `{ "vars": { "change": { "prNumber": 42 } } }`
- **THEN** only the Run scope is changed, and the merge follows object-recursive, scalar-replace, array-whole-replace rules

### Requirement: Write-boundary validation

Write boundaries SHALL reject, with an actionable domain error, any Variables document whose `vars` root or any `stages.<stage>.vars` root is not a JSON object. Write boundaries SHALL reject invalid JSON and key paths that cannot address a value. On rejection, the scope's original Variables SHALL remain unchanged.

#### Scenario: Non-object vars root is rejected
- **WHEN** a `PUT` or `PATCH` provides a `vars` value that is a JSON array or scalar
- **THEN** the request is rejected with a domain error, and the scope's Variables document is unchanged

#### Scenario: Invalid JSON is rejected
- **WHEN** a `PATCH` body cannot be parsed as JSON
- **THEN** the request is rejected with a domain error, and the scope's Variables document is unchanged

#### Scenario: Invalid key path is rejected
- **WHEN** a write targets a key path that cannot address a field
- **THEN** the request is rejected with a domain error, and the scope's Variables document is unchanged

### Requirement: unset clears the scope without persisting null

An unset (a `null` in a PATCH that removes a key) SHALL delete the current scope's workflow-wide or Stage declaration. The resulting persisted document SHALL contain no `null` placeholder that masks inheritance; a subsequent effective read SHALL inherit the value from the prior scope.

#### Scenario: Cleared key is not stored as null
- **WHEN** a Run key is unset via a PATCH carrying `null`, and the Run Variables document is persisted
- **THEN** the stored document has no `null` for that key, and effective resolution inherits the Issue or Project value

#### Scenario: Clearing a stage value restores stage inheritance
- **WHEN** an Issue's `stages.check.vars.agent.variant` is unset
- **THEN** the Issue's `check` Stage Variables no longer declare `agent.variant`, and the effective `check` stage inherits it from a prior scope

### Requirement: Effective Variables is a Run-only read-only derived fact

Effective Variables SHALL be a read-only, non-persisted projection derived under a WorkflowRun from Project → Issue → Run precedence. It SHALL be exposed only under a Run resource and SHALL NOT be a writable resource, and Project and Issue SHALL NOT expose effective read endpoints. Without a stage, the effective read returns Effective Workflow Variables; with a stage, it returns Effective Stage Variables.

#### Scenario: Effective read merges all scopes
- **WHEN** `GET /api/workflow-runs/{id}/variables/effective` is requested
- **THEN** the response is the Project → Issue → Run merge, not any single scope's stored value

#### Scenario: Effective read by stage applies stage overlays
- **WHEN** `GET /api/workflow-runs/{id}/variables/effective?stage=check` is requested
- **THEN** the response is the Effective Stage Variables for `check`, applying stage overlays after the workflow-wide merge

#### Scenario: Effective is not writable
- **WHEN** `PUT` or `PATCH` is attempted on `/api/workflow-runs/{id}/variables/effective`
- **THEN** the method is not mapped; the effective resource is read-only

#### Scenario: Effective key-path lookup
- **WHEN** `GET /api/workflow-runs/{id}/variables/effective/{keyPath}` is requested
- **THEN** the response is the value at that dotted path within the effective (optionally stage-scoped) merge, or an absent indicator if the path is not present

### Requirement: Variables owned only by Project, Issue, and Run

Variables SHALL be owned solely by Project, Issue, and WorkflowRun. A WorkflowProfile SHALL only reference variables through `${{ vars.* }}`; it SHALL NOT own, declare, validate against, or constrain the set of variable keys.

#### Scenario: Profile references but does not own variables
- **WHEN** a Profile Definition is parsed and a task uses `${{ vars.agent.model }}`
- **THEN** the value is resolved from Project, Issue, and Run Variables at dispatch, and the Profile holds no authoritative variable declarations

### Requirement: Attempt context snapshot invariant

Effective Variables SHALL be resolved for each dispatch from the then-current Variables, and the resolved snapshot SHALL be immutable for that attempt's lifetime. Later Variable changes SHALL affect only not-yet-dispatched tasks, manual retries, and recovery continuations; an already-dispatched (accepted) attempt SHALL retain its own context snapshot, rendered input, and recorded results.

#### Scenario: New attempts use latest variables
- **WHEN** Run Variables change after task-1 is dispatched and before task-2 is dispatched
- **THEN** task-2's attempt snapshot reflects the updated variables, while task-1's accepted attempt keeps its original snapshot

#### Scenario: Retry uses the current effective variables
- **WHEN** a failed task is retried after Variables change
- **THEN** the retry attempt resolves Effective Variables at retry time and uses those values

### Requirement: setVars writes Run workflow-wide variables

A task `setVars` action output SHALL be projected to a Run-only PATCH body containing `vars` and SHALL NOT produce a `stages` parameter. It SHALL target the renamed Run Variables resource path and follow the same merge and validation rules as any other Run write.

#### Scenario: setVars patches only Run workflow-wide variables
- **WHEN** a `setVars` action output is applied
- **THEN** a PATCH is sent to `/api/workflow-runs/{id}/variables` with a `vars` projection, no `stages`, and the Run scope is the only scope changed
