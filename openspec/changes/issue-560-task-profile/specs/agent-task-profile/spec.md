### Requirement: Agent definitions carry purpose and declared permissions

An Agent definition SHALL persist an optional purpose and a declared
permission list. Every Agent create, update, list, and detail projection SHALL
include both fields. Purpose is independent from description; an existing
description SHALL NOT be inferred as a purpose.

#### Scenario: Creating and reading a task declaration

- **WHEN** a caller creates an Agent with a purpose and valid permissions
- **THEN** the create response and later list and detail reads SHALL return
  the same purpose and permissions

#### Scenario: Updating one task declaration field

- **WHEN** a caller updates only the purpose or only the permissions
- **THEN** the other declared field SHALL remain unchanged

#### Scenario: Clearing optional declarations

- **WHEN** a caller patches purpose to null or permissions to an empty list
- **THEN** the persisted read model SHALL return a null purpose or empty
  permission list respectively

### Requirement: Permission declarations use one closed Server vocabulary

The Server SHALL accept only `repo:read`, `repo:write`, `issue:read`,
`issue:write`, `epic:read`, `epic:write`, and `artifact:publish` in an Agent
permission declaration. Omission remains valid.

#### Scenario: Rejecting an unknown permission

- **WHEN** a create or update request contains an unknown, empty, or non-string
  permission term
- **THEN** the Server SHALL reject it with `invalid_agent_permissions`, name
  the invalid term or declaration problem and accepted vocabulary, and leave
  the definition unchanged

### Requirement: CLI and Web author and render the same declaration

The CLI and Web profile editor SHALL both create, update, and clear purpose
and permissions through the Agent definition API. CLI view and the Web Agent
list/detail SHALL render the persisted purpose or permission scope without
deriving either field from runtime state.

#### Scenario: A Web edit is visible in CLI view

- **WHEN** the Web editor saves a purpose and permission declaration
- **THEN** a subsequent CLI view of that Agent SHALL render the same fields

#### Scenario: A CLI clear is visible in Web

- **WHEN** the CLI clears the purpose and permissions
- **THEN** the Web list/detail and editor SHALL show the cleared fields

### Requirement: Permission declarations do not alter launch behavior

Saving a purpose or permission declaration SHALL not add a Runner tool policy,
launch input override, or mutation of an already-created Job's launch facts.
