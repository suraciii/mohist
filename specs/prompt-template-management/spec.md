## ADDED Requirements

### Requirement: Prompt template is a first-class resource

Mohist SHALL manage prompt templates as first-class resources with system (file) and project (DB) scopes. A template SHALL be addressable by a stable `key` and carry human-readable metadata (display name, description, tags, stage) and an interpolable body.

#### Scenario: Template identity is its key

- **WHEN** a system file `proposal.prompt` and a project row with `Key = "proposal"` exist
- **THEN** the project row's `Body` fully replaces the system body for the merged template
- **AND** queries that return templates SHALL address them by `key` (filename without extension for system, `Key` column for project)

#### Scenario: System templates are read-only

- **WHEN** a client calls any template mutation endpoint (PUT/DELETE) on a key that is only present as a system file
- **THEN** the call is recorded as a project override for that key (it does not modify the file)
- **AND** system `.prompt` files on disk are not written, deleted, or mutated by Mohist

#### Scenario: Project-unique key adds a new template

- **WHEN** a project has no system or DB row for key `deploy-checklist`
- **AND** a project override is created for `deploy-checklist` via `PUT /api/projects/{id}/templates/deploy-checklist/override`
- **THEN** the merged template list for that project includes `deploy-checklist` with `source: "project-new"`
- **AND** no other project is affected

### Requirement: System .prompt files declare YAML frontmatter metadata

System `.prompt` files SHALL begin with a YAML frontmatter block delimited by `---` lines, declaring optional `name`, `description`, `tags` (list of strings), and `stage` (string) fields. Files without frontmatter SHALL still load; missing fields SHALL default to `displayName = key`, `description = ""`, `tags = []`, `stage = null`.

#### Scenario: Well-formed frontmatter populates metadata

- **WHEN** `proposal.prompt` starts with `name: "Generate Proposal"`, `description: "..."`, `tags: [plan, openspec]`, `stage: plan`
- **THEN** the parsed `SystemTemplate` exposes those values on the corresponding fields
- **AND** the body is the full file content with the frontmatter block removed

#### Scenario: Missing frontmatter falls back to defaults

- **WHEN** a `.prompt` file has no `---` frontmatter delimiter
- **THEN** the parser returns a `SystemTemplate` with `DisplayName = key`, `Description = ""`, `Tags = []`, `Stage = null`
- **AND** the body is the full file content

#### Scenario: Partial frontmatter tolerates missing fields

- **WHEN** a `.prompt` file has frontmatter with only `name: "X"`
- **THEN** `DisplayName = "X"`, `Description = ""`, `Tags = []`, `Stage = null`
- **AND** the file still loads

#### Scenario: Malformed YAML frontmatter does not silently corrupt the body

- **WHEN** a `.prompt` file's frontmatter block fails YAML parsing
- **THEN** the parser SHALL reject the file with a parse error that identifies the key
- **AND** the loader SHALL NOT expose a half-parsed body

### Requirement: PromptTemplateEngine resolves `${{ path }}` against a variable object

Mohist SHALL provide a `PromptTemplateEngine` that renders a template body by replacing `${{ path.to.value }}` markers with values resolved from a JSON-object variable tree, mirroring the runner's `renderTemplate` semantics: up to 5 passes, unresolvable references left in place, and missing references recorded.

#### Scenario: Resolved string substitute

- **WHEN** the body is `Hello ${{ issue.number }}` and `variables.issue.number = 42`
- **THEN** the rendered output is `Hello 42`
- **AND** `missingVariables` is empty

#### Scenario: Nested path resolves through objects

- **WHEN** the body is `Owner: ${{ project.name }}` and `variables.project.name = "Mohist"`
- **THEN** the rendered output is `Owner: Mohist`

#### Scenario: Missing variable is left in place and recorded

- **WHEN** the body references `${{ issue.priority }}` and `variables.issue` lacks `priority`
- **THEN** the rendered output contains the literal `${{ issue.priority }}` token
- **AND** `missingVariables` includes `"issue.priority"`

#### Scenario: Recursive expansion up to 5 passes

- **WHEN** a variable's value contains another `${{ ... }}` token that itself resolves
- **THEN** the engine SHALL run up to 5 passes to expand transitive references
- **AND** shall not enter an infinite loop on self-referential or cyclic data

#### Scenario: Non-string values are JSON-stringified

- **WHEN** a variable's resolved value is an object, array, number, or boolean
- **THEN** the engine serializes it to a JSON string before substitution
- **AND** `null` resolves to the literal string `null`

#### Scenario: ExtractVariables returns sorted unique variable list

- **WHEN** `ExtractVariables` is called with body `Use ${{ openspecChangeDir }} and ${{ issue.number }} and ${{ openspecChangeDir }}`
- **THEN** the result is `{ variables: ["issue.number", "openspecChangeDir"] }` (sorted, deduplicated)
- **AND** no rendering is performed

### Requirement: ProjectTemplateRow stores project template overrides in SQLite

Mohist SHALL persist project template overrides in a `ProjectTemplates` SQLite table with primary key `(ProjectId, Key)`, columns `DisplayName`, `Description`, `TagsJson` (JSON array), `Stage` (nullable), `Body`, and `UpdatedAt` (UTC ISO 8601), plus an index on `(ProjectId, UpdatedAt)`. An EF migration named `20260601050000_AddProjectTemplates` SHALL create the table and index.

#### Scenario: One override per project+key

- **WHEN** a project tries to insert two rows with the same `(ProjectId, Key)`
- **THEN** the table's primary-key constraint prevents both rows from co-existing
- **AND** the application layer treats duplicate keys as an upsert (the second write updates the existing row) — the DB constraint is a safety net, not an error path
- **AND** no API call ever returns a 409 for a duplicate key

#### Scenario: Project has no row → falls back to system

- **WHEN** a project has no row for key `proposal`
- **THEN** the effective template for `proposal` is the system template
- **AND** `source` is reported as `system`

#### Scenario: Default TagsJson is an empty array

- **WHEN** a project row is created without explicit tags
- **THEN** `TagsJson` is `"[]"`
- **AND** the deserialized tags list is empty

### Requirement: IProjectTemplateStore provides CRUD for project overrides

Mohist SHALL expose an `IProjectTemplateStore` service with: `GetForProjectAsync(projectId)`, `GetAsync(projectId, key)`, `UpsertAsync(projectId, key, body)`, `DeleteAsync(projectId, key)`. `GetForProjectAsync` returns the full list of project overrides for a project, including display metadata, sorted by `UpdatedAt` descending.

#### Scenario: Upsert creates a new override

- **WHEN** a project has no row for key `proposal` and `UpsertAsync` is called with body `B`
- **THEN** a new row is inserted with `(ProjectId, "proposal", ..., body: "B", UpdatedAt: now)`

#### Scenario: Upsert updates an existing override

- **WHEN** a project already has a row for key `proposal`
- **THEN** `UpsertAsync` updates the existing row's body, metadata, and `UpdatedAt`
- **AND** the primary key is preserved

#### Scenario: Delete removes the override

- **WHEN** a project row exists for key `proposal` and `DeleteAsync` is called
- **THEN** the row is removed
- **AND** subsequent `GetForProjectAsync` no longer includes it
- **AND** subsequent effective-template lookups for `proposal` fall back to the system body

#### Scenario: GetForProjectAsync returns all rows for a project

- **WHEN** a project has overrides for keys `proposal`, `build`, `deploy-checklist`
- **THEN** `GetForProjectAsync` returns three rows
- **AND** the result excludes rows from other projects

### Requirement: prompts.* namespace merges system + project overrides at workflow start

`MohistDefaultIssueWorkflowProfile.BuildVariables` SHALL merge system templates (from `IPromptLoader`) with project overrides (from `IProjectTemplateStore`) for the issue's project into a single `prompts` dictionary. Project overrides with the same key fully replace the system body; project-unique keys add new entries. The merge SHALL happen before the variables JSON is handed to the runner, and the runner's `${{ prompts.xxx }}` resolution remains unchanged.

#### Scenario: Project override replaces system body

- **WHEN** the system has `proposal = "A"` and the project has override `proposal = "B"`
- **THEN** `payload.prompts.proposal == "B"`
- **AND** the runner receives the overridden body in `prompts.proposal`

#### Scenario: Project-unique key adds new entry

- **WHEN** the system has no `deploy-checklist` and the project has override `deploy-checklist = "..."`
- **THEN** `payload.prompts.deploy-checklist == "..."`
- **AND** other keys are unaffected

#### Scenario: System key with no override keeps system body

- **WHEN** the system has `build = "..."` and the project has no row for `build`
- **THEN** `payload.prompts.build` is the system body

### Requirement: workflow start-work fails with 400 when prompts.* keys are unknown

When a workflow definition (or any stage's task) references `prompts.<key>`, the server SHALL validate that `<key>` exists in the merged `prompts` dictionary (system + project) before start-work. If any referenced key is missing, the API SHALL return HTTP 400 with a `missing_prompts` code and the list of missing keys.

#### Scenario: Valid key passes validation

- **WHEN** a workflow task references `prompts.proposal` and `proposal` exists in the merged map
- **THEN** start-work proceeds normally
- **AND** no missing-prompt error is returned

#### Scenario: Unknown key fails start-work with 400

- **WHEN** a workflow task references `prompts.does-not-exist`
- **AND** no system file and no project row for that key exists
- **THEN** start-work returns HTTP 400
- **AND** the response body's `code` is `missing_prompts`
- **AND** the response body's `details.missingKeys` includes `["does-not-exist"]`

#### Scenario: Project override makes a key resolvable

- **WHEN** a workflow task references `prompts.deploy-checklist`
- **AND** the project has an override row for `deploy-checklist`
- **THEN** start-work proceeds normally even though the system has no such file

### Requirement: Project template overrides emit audit events

Mohist SHALL emit an `IEventStore` event on each successful override upsert (`project_template_changed`) and delete (`project_template_deleted`). The event payload SHALL include `key`, `before` (state prior to the change, or `null` for delete of a row that did not exist), `after` (the new state, or `null` for delete), and `source: "user"`. The events SHALL be visible in the existing Activity timeline.

#### Scenario: Upsert emits project_template_changed

- **WHEN** a project override is created or updated via `PUT /api/projects/{id}/templates/{key}/override`
- **THEN** a `project_template_changed` event is appended with the new body and metadata
- **AND** the event includes `key`, `before` (or `null`), `after`, and `source: "user"`

#### Scenario: Delete emits project_template_deleted

- **WHEN** a project override is removed via `DELETE /api/projects/{id}/templates/{key}/override`
- **THEN** a `project_template_deleted` event is appended
- **AND** the event includes `key`, `before` (the deleted row), and `source: "user"`

#### Scenario: Audit events surface in the Activity timeline

- **WHEN** a user views the Activity timeline for an issue or project
- **THEN** `project_template_changed` and `project_template_deleted` events appear alongside existing timeline entries
- **AND** they identify the changed key and the actor (`source: "user"`)

### Requirement: REST API exposes system and project templates

Mohist SHALL expose the following REST endpoints under `/api` for prompt template management. All endpoints SHALL return the standard `{ success, data, error, code, details }` envelope. The system-template endpoint is read-only; project endpoints scope by the resolved `ProjectId`.

```
GET  /api/templates/system                                          # 12 system templates
GET  /api/projects/{id}/templates                                   # all effective (system + project merged) with source tag
GET  /api/projects/{id}/templates/{key}                             # single effective template
GET  /api/projects/{id}/templates/{key}/override                    # 200 with override body; 404 if not overridden
PUT  /api/projects/{id}/templates/{key}/override                    # create or update override; body: { displayName, description, tags, stage, body }
DELETE /api/projects/{id}/templates/{key}/override                  # remove override; subsequent GET shows system body
POST /api/projects/{id}/templates/{key}/preview                     # render body with provided variables
POST /api/templates/extract-variables                              # static variable extraction from arbitrary body
```

#### Scenario: List system templates

- **WHEN** a client calls `GET /api/templates/system`
- **THEN** the response is `{ success: true, data: SystemTemplate[] }`
- **AND** the array contains all 12 built-in system templates with their parsed frontmatter and body
- **AND** the array is sorted by `key`

#### Scenario: List effective project templates

- **WHEN** a client calls `GET /api/projects/{id}/templates`
- **THEN** the response returns one entry per known template key (system + project-unique)
- **AND** each entry reports `source: "system" | "project-override" | "project-new"`
- **AND** each entry exposes the effective body, display name, description, tags, and stage

#### Scenario: Get single effective template

- **WHEN** a client calls `GET /api/projects/{id}/templates/{key}`
- **THEN** the response returns the effective template (project override if present, otherwise system)
- **AND** the entry's `source` field reflects which one was used

#### Scenario: Get override returns 404 when no override exists

- **WHEN** a client calls `GET /api/projects/{id}/templates/{key}/override` for a key without a project row
- **THEN** the response is HTTP 404 with `code: "not_found"`

#### Scenario: Put override creates or updates

- **WHEN** a client calls `PUT /api/projects/{id}/templates/{key}/override` with `{ displayName, description, tags, stage, body }`
- **THEN** the row is created if absent, or its fields are updated if present
- **AND** the response is 200 with the stored row
- **AND** the audit event `project_template_changed` is appended

#### Scenario: Put override validates required fields

- **WHEN** a client calls `PUT /api/projects/{id}/templates/{key}/override` with a missing or empty `body`
- **THEN** the response is HTTP 400 with `code: "bad_request"`
- **AND** no row is created or updated

#### Scenario: Delete override removes the row

- **WHEN** a client calls `DELETE /api/projects/{id}/templates/{key}/override` and a project row exists
- **THEN** the row is removed
- **AND** the response is 200
- **AND** the audit event `project_template_deleted` is appended

#### Scenario: Delete override is idempotent

- **WHEN** a client calls `DELETE /api/projects/{id}/templates/{key}/override` and no project row exists
- **THEN** the response is 200
- **AND** no audit event is appended

#### Scenario: Preview renders body with provided variables

- **WHEN** a client calls `POST /api/projects/{id}/templates/{key}/preview` with `{ variables: { ... } }`
- **THEN** the response is `{ success: true, data: { rendered: string, missingVariables: string[], depth: number } }`
- **AND** `rendered` is the body after `${{ ... }}` resolution (5 passes max)
- **AND** `missingVariables` lists paths that could not be resolved
- **AND** `depth` reports how many passes produced a change

#### Scenario: Extract-variables is stateless and key-agnostic

- **WHEN** a client calls `POST /api/templates/extract-variables` with `{ body: "Use ${{ openspecChangeDir }}" }`
- **THEN** the response is `{ success: true, data: { variables: ["openspecChangeDir"] } }`
- **AND** the result is sorted and deduplicated
- **AND** no project context is required

### Requirement: Template key is immutable and body is atomic

Mohist SHALL treat the template `key` as immutable; renaming a key requires delete + create. The override `body` SHALL be a single atomic string and SHALL be replaced wholesale on update (no deep-merge with the system body). A row's primary key SHALL be `(ProjectId, Key)`, preventing duplicate keys within the same project.

#### Scenario: Same-key override fully replaces body

- **WHEN** the system body is `A` and the project override body is `B`
- **THEN** the effective body is `B`
- **AND** the system body is not deep-merged with `B`

#### Scenario: Duplicate key within project is rejected

- **WHEN** two `PUT` calls for the same `(ProjectId, Key)` arrive
- **THEN** the second call updates the existing row
- **AND** the table never holds two rows for the same `(ProjectId, Key)`

#### Scenario: Key is not exposed as editable in API

- **WHEN** a client calls `PUT /api/projects/{id}/templates/{key}/override`
- **THEN** the URL path's `{key}` is the authoritative key
- **AND** any `key` field in the request body is ignored
