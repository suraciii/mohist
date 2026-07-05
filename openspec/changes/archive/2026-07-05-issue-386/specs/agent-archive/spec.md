### Requirement: `mo agent archive` is the canonical name for archive behavior

`mo agent archive <name-or-id>` SHALL be the canonical command name for the existing agent archive behavior. The command SHALL resolve the agent ref (name or `agent_` id) client-side, then DELETE `/api/projects/{projectId}/agents/{agentId}`, and on success SHALL print `Agent {name} ({id}) archived`. The canonical name reflects the command's actual behavior (the server method is `ArchiveAsync`, the output message says "archived", and archiving is reversible).

#### Scenario: Archive by id resolves and deletes

- **WHEN** a caller runs `mo agent archive agent_<id>` against a resolved project
- **THEN** the CLI SHALL DELETE `/api/projects/{projectId}/agents/<id>` and print `Agent {name} ({id}) archived` on success

#### Scenario: Archive by name resolves via the agent list

- **WHEN** a caller runs `mo agent archive <name>` where `<name>` is not an `agent_` id
- **THEN** the CLI SHALL resolve the name to an agent id via the agent list, then DELETE the resolved agent
- **AND** SHALL print `Agent {name} ({id}) archived` on success

#### Scenario: Unresolved agent fails locally

- **WHEN** a caller runs `mo agent archive <name-or-id>` and the agent cannot be resolved
- **THEN** the CLI SHALL print an error to stderr and exit non-zero without sending a DELETE

### Requirement: `delete` is a transitional alias of `archive`

`mo agent delete <name-or-id>` SHALL be retained as a transitional alias of `archive` with identical arguments, behavior, endpoint, and exit codes. The alias is a name-only flip with no semantic change; it exists solely to keep scripts written against the prior `delete` name working.

#### Scenario: `delete` behaves identically to `archive`

- **WHEN** a caller runs `mo agent delete <name-or-id>` with any project scoping flags
- **THEN** the CLI SHALL produce the same request, output, and exit code as `mo agent archive <name-or-id>` with the same flags
