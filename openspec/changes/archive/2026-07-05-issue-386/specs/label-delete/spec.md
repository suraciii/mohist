### Requirement: `mo label delete` is the canonical name for catalog removal

`mo label delete <key>` SHALL be the canonical command name for removing a label definition from the project catalog. The command SHALL resolve the project via `--project` / `--project-id` (active project when neither is given) and DELETE `/api/projects/{projectId}/labels/catalog/{key}`. This is a name-only canonical/alias flip from the prior `remove` primary; the behavior and endpoint are unchanged.

#### Scenario: Delete by key removes the catalog entry

- **WHEN** a caller runs `mo label delete <key>` against a resolved project
- **THEN** the CLI SHALL DELETE `/api/projects/{projectId}/labels/catalog/{key}`

#### Scenario: Project scoping honors --project / --project-id

- **WHEN** a caller runs `mo label delete <key> --project-id <id>`
- **THEN** the CLI SHALL DELETE `/api/projects/<id>/labels/catalog/{key}`

### Requirement: `remove` and `rm` are aliases of `delete`

`mo label remove <key>` and `mo label rm <key>` SHALL be aliases of `mo label delete <key>` with identical arguments, behavior, endpoint, and exit codes. Both aliases are name-only with no semantic change.

#### Scenario: `remove` behaves identically to `delete`

- **WHEN** a caller runs `mo label remove <key>` with any project scoping flags
- **THEN** the CLI SHALL produce the same request and exit code as `mo label delete <key>` with the same flags

#### Scenario: `rm` behaves identically to `delete`

- **WHEN** a caller runs `mo label rm <key>` with any project scoping flags
- **THEN** the CLI SHALL produce the same request and exit code as `mo label delete <key>` with the same flags
