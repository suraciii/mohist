## Why

When an agent or user wants to label an issue, they first need to know which labels this project uses, what each one is for, and when to apply it. No such catalog exists today: the `refactor` usage rule is hardcoded inside the mohist skill text, and `useLabels()` can only reverse-infer free-text label keys from existing issues. An agent that creates issues via the skill has no way to learn the project's label vocabulary except that one hardcoded line. A project-scoped, advisory label catalog — readable by agents through `mo label list` — closes this gap and turns scattered hardcoded guidance into queryable data.

## What Changes

- Introduce a project-level **label catalog**: a store of label definitions, each carrying `{ key, description, supportedValues?, origin }`.
- Seed the catalog with **system built-in** definitions, beginning with `refactor` (migrated out of the hardcoded "Refactor label discipline" section in the mohist skill) plus common classification dimensions such as `kind`/`type`/`module`/`context`.
- Let users **add / update / remove their own catalog entries** via API (Web UI follows later).
- Expose the catalog through **`mo label list`** (prints key / description / origin) and **`mo label add` / `mo label remove`** management commands, backed by HTTP API endpoints.
- The catalog is **purely advisory**: it describes and recommends labels but does **not** validate or constrain Issue labels — an issue may carry a label absent from the catalog, and a catalog entry may never be used. **No server-side AI and no agent invocation** is introduced.

## Capabilities

### New Capabilities
- `label-catalog`: Project-level label definition store — the `LabelDefinition` model (`key`, `description`, optional `supportedValues`, `origin: system|user`), system built-in seeds (starting with `refactor`), user-defined entry lifecycle (add/update/remove), and the advisory non-enforcement contract between the catalog and Issue labels.

### Modified Capabilities
- `http-api`: Expose the label catalog for reading and management under the project label routes; reconcile the new catalog surface with the current distinct-keys `GET /api/labels` endpoint.
- `cli-interface`: `mo label list` surfaces the catalog (key / description / origin); add `mo label add` / `mo label remove` subcommands for catalog management.

## Impact

- **New server module**: a project-scoped `Label/` domain + persistence for `LabelDefinition`, modeled on the project-scoped Epic aggregate rather than attached to the Issue aggregate. The Issue aggregate is unchanged.
- **System seed data**: a built-in catalog seeded on demand; the `refactor` definition migrates from the mohist skill's hardcoded spec into catalog data.
- **API surface**: new/extended endpoints under `/api/projects/{projectRef}/labels` for catalog read + CRUD; the existing distinct-keys endpoint is re-scoped or complemented.
- **CLI**: `packages/cli/Mohist.Cli/MohistCliCommands.Label.cs` — `list` output surfaces catalog data; new `add` / `remove` subcommands.
- **Out of scope (non-goals)**: no enforcement of catalog membership on Issue labels; no server-side AI classification; the mohist skill does not yet consume the catalog (separate issue); no label display metadata (colors); no Web management UI (API + CLI first).
