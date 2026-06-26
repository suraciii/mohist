## Why

The server already exposes per-issue workflow-profile overrides — template, variables, and per-prompt edits — through 10 endpoints under `/api/projects/{p}/issues/{n}/workflow-profile/*`, but the CLI has no entry point for any of them. Per-issue prompt overrides are the single highest-leverage knob an agent has to reshape its own behavior within a fixed workflow, yet today they are reachable only by clicking through Web UI or dropping to curl. Agent-driven and scripted flows are broken because the agent's own interface (the CLI) cannot drive the most important self-tuning surface.

## What Changes

### New `mo issue workflow config` command group

A new `config` subcommand under the existing `mo issue workflow` parent, deliberately separated from the runtime subcommands (`status`, `timeline`) since it manages configuration state rather than run state. Pure CLI wiring — no server changes.

- **`mo issue workflow config get <num>`** — `GET /{n}/workflow-profile`. Returns the three sections (template, variables, prompts) at once. Supports `-o table|json`.
- **`mo issue workflow config set <num> [flags]`** — a single composite set command whose flags decide what changes, so one invocation can atomically touch multiple config categories (aligned with the server's PUT/PATCH semantics):
  - `--template <yaml|@file>` → `PUT /{n}/workflow-profile/template`
  - `--var k=v` (repeatable) → `PATCH /{n}/workflow-profile/variables`
  - `--stage-var <stage>.k=v` (repeatable) → staged variable entries
  - `--prompt <key>=<body|@file>` (repeatable) → `PUT /{n}/workflow-profile/prompts/{key}` (`@file` reads the body from a file)
- **`mo issue workflow config clear <num> [flags]`** — composite clear:
  - `--template` → `DELETE /{n}/workflow-profile/template` (falls back to project/system template)
  - `--var k` (repeatable) → clears via `PATCH` setting `k` to `null` (deep-merge servers cannot delete)
  - `--prompt <key>` → `DELETE /{n}/workflow-profile/prompts/{key}`
- **`mo issue workflow config preview <num> <key>`** — `POST /{n}/workflow-profile/prompts/{key}/preview`. Renders the final prompt text under current variables + template, for diagnosing "why did the agent receive this prompt".
- All subcommands support `--project/--project-id` and `-o table|json`.
- `mo issue workflow config --help` lists `get / set / clear / preview`.

### Possible server-side micro-adjustment

- If the server's `PATCH /variables` (deep merge) cannot honor a `null` value as "clear", a minimal server tweak is in scope so that CLI `clear --var k` can remove a variable rather than overwrite it. **BREAKING** only in the narrow sense that a previously-cleared-by-`null` variable is removed instead of stored as null; no other behavior changes.

## Capabilities

### New Capabilities

_None._ The underlying server endpoints and domain behaviors already exist; this change wires them to the CLI.

### Modified Capabilities

- `cli-interface`: the `mo issue workflow` command group gains a `config` subcommand group (`get` / `set` / `clear` / `preview`) that drives the existing workflow-profile template/variables/prompts endpoints. Adds `--template`, `--var`, `--stage-var`, `--prompt` flag semantics (including `@file` body reading for template and prompt, and `k=v` / `stage.k=v` forms for variables) and `-o table|json` / `--project`-ref support across all four verbs.
- `issue-workflow-profile`: if the server needs to honor `null` as a variable-clear signal on `PATCH /variables`, the workflow-profile variable-clear requirement is refined to support explicit removal via null (deep-merge servers otherwise cannot delete keys).

## Impact

- **CLI** (`packages/cli/Mohist.Cli/MohistCliCommands.Issue.cs`, `BuildWorkflow`): add a `config` parent subcommand with `get`/`set`/`clear`/`preview` children, all routing through `MohistCliApi` to the existing workflow-profile endpoints. New table renderers for the three-section profile response (`TableRenderer.IssueTemplates.cs` / `TableRenderer.Issues.cs`).
- **Server** (`packages/server/`): no new endpoints. At most a small adjustment to the `PATCH /variables` handler to treat `null` values as "remove this key" — only if current behavior does not already support it.
- **API consumers**: unchanged. The CLI becomes a first-class client of endpoints that were already public.
- **Tests**: CLI integration tests for each subcommand (success path per the acceptance criteria: template set/clear, variable set/clear, stage-var, prompt inline + `@file`, preview render) plus error-passthrough cases (e.g. invalid template YAML surfaces the server error). No new server tests unless the variable-clear tweak is needed.
- **Not affected**: `mo issue workflow status/timeline` (runtime subcommands), `mo issue rebase`, attachment/metrics CLIs, `--model`/`--stage-models` (those live on `mo issue update` per #161), server domain model.
