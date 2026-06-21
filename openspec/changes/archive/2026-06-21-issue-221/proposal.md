## Why

The label catalog already exists as server data and is reachable through the API and `mo label list/add/remove`, but users have no visual way to curate it. To recall which label keys a project recommends, what each means, or which values it suggests, they must read CLI output or remember keys by hand — so the catalog never becomes the clear, agent-facing classification guidance it was meant to be. Two gaps remain from the labels epic: a project-scoped Web management surface for the catalog, and a `mo label update` command to match the already-shipped `PATCH /api/projects/{projectRef}/labels/catalog/{key}` endpoint. Closing them lets users maintain rich classification prompts for agents without hardcoding labels into issue text.

## What Changes

- **Web UI — project label catalog management page**: a project-scoped surface (concrete placement per existing UI structure, e.g. under Project Settings or the project detail view) to view and curate the catalog:
  - **List** every entry, showing `key`, `description`, `supportedValues`, and `origin` (system/user).
  - **Add** user definitions via a form: `key`, `description`, and optional `supportedValues` (multi-line or comma-separated input, parsed to a string array).
  - **Edit** an existing entry's `description` and `supportedValues`; `key` is immutable.
  - **Delete** user-defined entries; system entries (`origin: system`) are not deletable (hide or disable the action).
  - **Validation**: `key` must match `^[a-z0-9]([-a-z0-9]*[a-z0-9])?$`; `description` must be non-empty; invalid input is rejected with a clear error.
- **CLI — `mo label update <key>`**: new subcommand calling `PATCH /api/projects/{projectRef}/labels/catalog/{key}`. Supports partial updates — `--description` and/or `--supported-values`; omitted fields keep their current value. Surfaces clear errors for an unknown key, validation failure, or an attempt to modify a system entry.
- **No server or domain change**: the catalog CRUD API (including PATCH) and the `LabelDefinition` model already exist; this issue only consumes them. No breaking API changes.

## Capabilities

### New Capabilities

(none — the `label-catalog` capability already owns the data/domain contract, including that user definitions are modifiable.)

### Modified Capabilities

- `cli-interface`: add `mo label update <key>` to the existing `mo label` command group (peer to `list`/`add`/`remove`), with partial-field update semantics and clear error handling for unknown key, invalid input, and system-entry protection.
- `web-ui`: add a project-scoped label catalog management page — list/add/edit/delete user entries, protect system entries from deletion, and enforce client-side validation matching the `label-catalog` rules. Distinct from the existing issue-level `LabelEditor`, which edits an issue's free key-value labels.

## Impact

- **Web** (`packages/web/src/`): new catalog management page/widget plus its API queries/mutations against the existing `GET/POST/PATCH/DELETE /api/projects/{projectRef}/labels/catalog` endpoints; a navigation entry under the project surface. The issue-level `LabelEditor` (`entities/issue/lib/label-editor`) is unchanged — it edits issue key-value labels, a separate concern from curating the catalog.
- **CLI** (`packages/cli/Mohist.Cli/MohistCliCommands.Label.cs`): add a `BuildUpdate` subcommand and register it alongside `list`/`add`/`remove`; reuse existing project resolution, key validation (`LabelDelta.ValidateKey`), and output handling. Tests extend `packages/cli/tests/Mohist.Cli.Tests/CliLabelCatalogSpecs.cs`.
- **Server**: no code change required — `LabelsRoutes.cs` already exposes the PATCH endpoint and `LabelCatalogService.UpdateAsync`. Existing server tests (`LabelCatalogApiSpecs.cs`) already cover PATCH behavior and must keep passing.
- **Domain model**: unchanged (`LabelDefinition` `{ key, description, supportedValues?, origin }`).
- **No breaking changes** to the HTTP API, persistence, or the issue label primitive.
