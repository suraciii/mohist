### Requirement: `mo project use <project>` is the single entry for setting the active project

After this change there SHALL be exactly one command path for setting the active project: `mo project use <project>`, where `<project>` is a project name or id. The duplicate root-level entry (`mo use <project>`) SHALL be removed. The surviving `mo project use` command SHALL keep its behavior unchanged — it SHALL resolve the identifier, `POST /api/projects/{id}/use`, persist `activeProjectId` to local project state, and print the same `Active project: <name> (<id>)` confirmation and the same error/guidance text on failure.

#### Scenario: the root command exposes no use subcommand

- **WHEN** a caller runs `mo --help`
- **THEN** the listed top-level subcommands SHALL NOT include `use`

#### Scenario: the surviving entry keeps the same behavior

- **WHEN** a caller runs `mo project use <identifier>`
- **THEN** the CLI SHALL resolve `<identifier>` and issue `POST /api/projects/<identifier>/use` with an empty body
- **AND** on success SHALL write `activeProjectId` to local project state
- **AND** SHALL print `Active project: <name> (<id>)` to stdout
- **AND** SHALL exit 0

#### Scenario: failure handling carries over unchanged

- **WHEN** a caller runs `mo project use <identifier>` and the server rejects the request (e.g. unknown project) or cannot be reached
- **THEN** the CLI SHALL emit the same error/guidance text the previous `mo use` and `mo project use` emitted
- **AND** SHALL exit non-zero
- **AND** SHALL NOT modify local project state

#### Scenario: no positional argument is accepted on the root

- **WHEN** a caller runs `mo use <project>` after this change
- **THEN** the CLI SHALL fail to resolve `use` as a top-level command
- **AND** SHALL exit non-zero with a parse error (unless the legacy path is retained as a uniform alias per the `root-command-shape` policy)
