### Requirement: Prompts are an independent resource

Workflow Prompts (system catalog plus Project overrides) are read, written, and rendered by a dedicated Prompts component that MUST NOT be co-located with or depend on Profile CRUD. Prompts are a resource independent of Workflow Profile; Profile only references prompts via `${{ prompts.* }}`.

#### Scenario: prompt component has no Profile dependency

- **WHEN** the Prompts component is constructed or invoked to list, read, write, or render a prompt
- **THEN** it MUST NOT require a Profile collection provider or Profile CRUD dependency

### Requirement: System prompt catalog

The system (builtin) prompt catalog is read-only. Each system prompt exposes its key, display name, description, tags, optional stage, and body. System prompts SHALL be served unchanged regardless of Project configuration.

#### Scenario: listing system prompts

- **WHEN** the system prompt catalog is listed
- **THEN** every builtin key SHALL appear with its builtin displayName, description, tags, stage, and body, and source marked `system`

### Requirement: Project prompt override and new-key creation

A Project MAY override the body of any system prompt key, and MAY add prompt keys that do not exist in the system catalog. An override of an existing system key has source `project`; a key absent from the system catalog has source `project-new`.

#### Scenario: project overrides a system prompt body

- **WHEN** a Project sets a body for a key that exists in the system catalog
- **THEN** the effective prompt for that key SHALL return the Project body and source `project`

#### Scenario: project adds a new prompt key

- **WHEN** a Project sets a body for a key absent from the system catalog
- **THEN** the effective prompt for that key SHALL return the Project body and source `project-new`

### Requirement: Effective prompt resolution precedence

Resolving an effective prompt by key SHALL prefer a Project body over the system body. When no Project body exists for the key, the system prompt is used. When neither exists, resolution returns no prompt.

#### Scenario: project body wins over system

- **WHEN** both a system and a Project body exist for a key
- **THEN** the resolved prompt SHALL use the Project body

#### Scenario: system fallback when no project override

- **WHEN** a Project has no body for a key that exists in the system catalog
- **THEN** the resolved prompt SHALL use the system body with source `system`

#### Scenario: unknown key resolves to nothing

- **WHEN** a key exists in neither the system catalog nor any Project override
- **THEN** resolution SHALL return no prompt (null/absent)

### Requirement: Prompt list merges system and project keys

Listing effective prompts SHALL merge every system key with every Project key (union), sorted by key. For each key, the Project body wins when present; otherwise the system body is used. A stage filter, when supplied, SHALL exclude prompts whose stage does not match the requested stage (a prompt with no stage is always included).

#### Scenario: merged list with override and new key

- **WHEN** a Project overrides system key `proposal` and adds a new key `custom`
- **THEN** the list SHALL contain both `proposal` (Project body, source `project`) and `custom` (Project body, source `project-new`), plus all remaining system keys (system body)

#### Scenario: stage filter narrows the list

- **WHEN** prompts are listed with a stage filter `plan` and some system prompts declare a stage other than `plan`
- **THEN** only prompts whose stage is `plan` or null SHALL appear

### Requirement: Prompt deletion removes project override

Deleting a prompt key SHALL remove the Project override for that key. A subsequent resolution of that key SHALL fall back to the system body if one exists, or resolve to nothing otherwise.

#### Scenario: deleting a project override restores system body

- **WHEN** a Project override for a system key is deleted
- **THEN** resolving that key SHALL return the system body with source `system`

### Requirement: Prompt preview rendering

Rendering a prompt SHALL accept a prompt body and a variables JSON object, and SHALL return the rendered text, the set of missing variables, the resolution depth, and any errors. The render result MUST be identical to today for the same body and variables.

#### Scenario: rendering with complete variables

- **WHEN** a prompt body referencing `${{ vars.foo }}` is rendered with `{ "foo": "bar" }`
- **THEN** the rendered text SHALL contain `bar` and the missing-variables set SHALL be empty

### Requirement: Issue scope has no prompt override

Prompts are configurable only at the Project scope. The Issue scope SHALL NOT provide a prompt override or prompt write path.

#### Scenario: issue cannot override prompts

- **WHEN** an Issue-scoped variable or configuration path is queried for prompt overrides
- **THEN** no Issue prompt override SHALL be persisted or resolved; prompt resolution depends only on the system catalog and the Project scope
