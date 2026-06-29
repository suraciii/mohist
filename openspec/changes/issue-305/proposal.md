## Why

Several product capabilities are reachable only through Web UI or curl, even though their server endpoints already exist and they are actions a CLI/script-driven user actively triggers — not passive views. A user cannot, from the CLI, manage a project's workflow templates, default template, variables, or prompt overrides (they can only do this per-issue); they cannot push a followup instruction into a running issue workflow session (only generic `AgentSession` followup is exposed); and the one command that batch-archives every completed issue ships undocumented and untested. These are functional entry points — configuration, triggering, state change — that belong in the CLI by the "entry-point-in-CLI, display-only-in-Web" invariant, so the CLI can drive the full workflow-config and run-intervention lifecycle without falling back to the UI.

## What Changes

### Project-level workflow configuration (mirrors `mo issue workflow config`)

A new `workflow` subgroup under `mo project`, split into template-entity CRUD and a `config` group that mirrors the existing per-issue `get / set / clear / preview` organization:

- **`mo project workflow template <list|create|show|update|delete>`** — CRUD over `GET/POST/PUT/DELETE /api/projects/:id/workflow-templates[/{tid}]`. `create`/`update` accept an inline YAML body or `@file`.
- **`mo project workflow config get`** — `GET /workflow-profile`, surfacing default-template id and variables (and prompt overrides) in one view. `-o table|json`.
- **`mo project workflow config set`** — composite writes driven by flags:
  - `--default-template <id>` → `PUT /workflow-profile/default-template`
  - `--var k=v` / `--stage-var <stage>.k=v` → incremental `PATCH /workflow-profile/variables`
  - `--vars-file <file>` (or equivalent full-replace flag) → wholesale `PUT /workflow-profile/variables` (the project API uniquely supports full replace, unlike issue-level which only merges)
  - `--prompt <key>=<body|@file>` → `PUT /workflow-profile/prompts/{key}`
- **`mo project workflow config clear`** — `--default-template` → `DELETE /workflow-profile/default-template`; `--var k` → remove via variables PATCH; `--prompt <key>` → `DELETE /workflow-profile/prompts/{key}`.
- **`mo project workflow config preview <key>`** — `POST /workflow-profile/prompts/{key}/preview`.
- All subcommands accept `--project/--project-id` and `-o table|json`.

### Followup into a running issue workflow session

- **`mo issue session followup <num> <name> --text <text|--text-file|--text-stdin>`** — `POST /api/projects/:id/issues/:num/sessions/:name/followup`, mirroring the existing `mo agent session followup`. Prints the delivery status; surfaces `404 session_inactive`/`503 runner_offline` honestly. Fits naturally under the existing `mo issue session` group (alongside `show`/`transcript`/`compact`/`reset`).

### Batch archive of completed issues

- **`mo issue archive --all-completed`** — the flag and `POST /issues/archive-completed` wiring already exist but are undocumented and untested. This change adds CLI reference documentation under the Issue command group and test coverage so the entry point meets the same bar as the other new verbs.

No server API changes. No Web UI changes.

## Capabilities

### New Capabilities

_None._ Every backing endpoint already exists; this change is pure CLI wiring plus documentation/tests.

### Modified Capabilities

- `cli-interface`: the `mo project` command group gains a `workflow` subgroup — `template` CRUD (`list`/`create`/`show`/`update`/`delete`) and a `config` group (`get`/`set`/`clear`/`preview`) covering project default-template, variables (incremental merge **and** full replace), and prompt overrides, including `@file` body reading, `k=v`/`stage.k=v` variable forms, `-o table|json`, and `--project`-ref support. The `mo issue session` group gains a `followup` verb driving the existing issue-session followup endpoint with faithful surfacing of `session_inactive`/`runner_offline` states. The pre-existing `mo issue archive --all-completed` batch-archive verb is formally specified (it currently ships undocumented and untested).

## Impact

- **CLI** (`packages/cli/Mohist.Cli/`): new `MohistCliCommands.Project.Workflow.cs` (or extend `MohistCliCommands.Project.cs`) for the `mo project workflow` subgroup; extend `MohistCliCommands.Issue.cs` `BuildSession` to add the `followup` verb. New table renderers for the project template / profile response in `TableRenderer.*`. All routing goes through `MohistCliApi` to existing endpoints.
- **Server** (`packages/server/`): no endpoint, contract, or domain changes.
- **Docs** (`docs/cli-reference.md`): document `mo project workflow template|config`, `mo issue session followup`, and `mo issue archive --all-completed` under their respective command groups (Issue 管理 / 项目管理).
- **Tests** (`packages/cli/tests/Mohist.Cli.Tests/`): CLI command specs for each new verb (template CRUD round-trip, config get/set/clear/preview incl. variable replace-vs-merge, prompt `@file`, followup success + `session_inactive`/`runner_offline` surfacing, batch-archive success) using the existing `FakeCommandExecutor`/`RecordingHttpHandler` harness.
- **Not affected**: `mo issue workflow config` (issue-level, unchanged), `mo agent session followup` (generic AgentSession, unchanged), server domain model, Web UI, metrics/inbox/agent-ops views (explicitly out of scope as display-only surfaces).
