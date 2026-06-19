## ADDED Requirements

### Requirement: Label catalog API exposes project definitions

`GET /api/projects/{projectRef}/labels/catalog` SHALL return the project's full label catalog as governed by the `label-catalog` capability: every definition (system-origin and user-origin) with its `key`, `description`, optional `supportedValues`, and `origin`. The endpoint SHALL NOT alter any Issue's labels and SHALL NOT invoke any AI model or agent.

#### Scenario: List the catalog
- **WHEN** the client requests `GET /api/projects/{projectRef}/labels/catalog` for a project with a system `refactor` definition and a user `module` definition
- **THEN** the response contains both definitions
- **AND** each entry includes `key`, `description`, `origin`, and `supportedValues` when present

#### Scenario: Catalog read is project-scoped
- **WHEN** the client requests the catalog for a project that has no user definitions
- **THEN** the response contains only the system-seeded definitions
- **AND** does not contain user definitions belonging to any other project

### Requirement: Label catalog API manages user-defined entries

The API SHALL support creating, updating, and removing user-origin catalog definitions governed by the `label-catalog` capability. `POST /api/projects/{projectRef}/labels/catalog` SHALL create a user-origin definition from `{ key, description, supportedValues? }`. `PATCH /api/projects/{projectRef}/labels/catalog/{key}` SHALL update an existing user-origin definition's `description` and/or `supportedValues`. `DELETE /api/projects/{projectRef}/labels/catalog/{key}` SHALL remove an existing user-origin definition and SHALL be idempotent for a missing key. An invalid key or empty description SHALL be rejected with HTTP 400 and a clear error, a duplicate key SHALL be rejected with HTTP 409 and a clear error, and in each rejected case the catalog SHALL NOT be persisted with the invalid entry.

#### Scenario: Create a user definition
- **WHEN** the client sends `POST /api/projects/{projectRef}/labels/catalog` with `{ "key": "module", "description": "Classifies the subsystem", "supportedValues": ["auth", "ui"] }`
- **THEN** the response is 201 and returns the created entry with `origin: user`

#### Scenario: Update a user definition
- **WHEN** the client sends `PATCH /api/projects/{projectRef}/labels/catalog/module` with a new `description`
- **THEN** the response returns the updated user-origin entry with the new description

#### Scenario: Update a missing user definition is not found
- **WHEN** the client sends `PATCH /api/projects/{projectRef}/labels/catalog/unknown` and no user definition exists for the key
- **THEN** the response is 404 and no entry is created or modified

#### Scenario: Remove a user definition
- **WHEN** the client sends `DELETE /api/projects/{projectRef}/labels/catalog/module`
- **THEN** the response is 204 and the entry is removed

#### Scenario: Remove a missing user definition is idempotent
- **WHEN** the client sends `DELETE /api/projects/{projectRef}/labels/catalog/unknown`
- **THEN** the response is 204 and no error is raised

#### Scenario: Create with duplicate key is rejected
- **WHEN** the client sends `POST` with key `module` while `module` already exists in the catalog
- **THEN** the response is 409 with a clear error
- **AND** the existing entry is unchanged

#### Scenario: Create with a system key is rejected
- **WHEN** the client sends `POST` with key `refactor` (a reserved system key)
- **THEN** the response is 409 with a clear error
- **AND** the system `refactor` definition is unchanged

#### Scenario: Create with invalid key is rejected
- **WHEN** the client sends `POST` with key `Module` (uppercase)
- **THEN** the response is 400 and the entry is not persisted

### Requirement: System-defined catalog entries are immutable via the API

The API SHALL reject any attempt to modify or remove a system-origin (`origin: system`) catalog definition. `PATCH` or `DELETE` on a system-origin key SHALL fail with HTTP 409 and SHALL NOT alter the definition.

#### Scenario: PATCH on a system definition is rejected
- **WHEN** the client sends `PATCH /api/projects/{projectRef}/labels/catalog/refactor`
- **THEN** the response is 409
- **AND** the `refactor` definition is unchanged

#### Scenario: DELETE on a system definition is rejected
- **WHEN** the client sends `DELETE /api/projects/{projectRef}/labels/catalog/refactor`
- **THEN** the response is 409
- **AND** the `refactor` definition remains in the catalog
