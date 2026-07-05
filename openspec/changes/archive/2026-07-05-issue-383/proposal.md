## Why

Two CLI command groups — top-level `mo repo` (`MohistCliCommands.Repository.cs`) and nested `mo project repo` (`MohistCliCommands.ProjectRepo.cs`) — drive the same `/api/projects/{id}/repositories` endpoints but with divergent parameter shapes, asymmetric subcommands, and a broken project-scope convention. Users cannot predict which path to use nor what each supports: `add` takes a positional `<name>` on one path and `--name` on the other; the "set as default" flag is `--default` on one and `--set-default` on the other; `update` exists only on the top-level and `set-default` only on the nested; and the top-level path accepts only `--project-id`, not `--project`, violating the scope-via-flag convention mandated by `design/cli.md`. This collapses the double-track into a single `mo repo` entry with project scope expressed via `--project`/`--project-id` (the kubectl `--namespace` pattern), closing the实装差距 already recorded in `docs/cli-reference.md`.

## What Changes

- **BREAKING**: Delete the nested `mo project repo` command group entirely (no alias). Users migrate to `mo repo --project`. Migration noted in release/changelog.
- `mo repo` becomes the single entry for repository management with the complete subcommand set: `list` / `add` / `update` / `set-default` / `delete`.
- Add `mo repo set-default <name>`, migrated from the nested group (the top-level currently lacks it).
- `name` is a positional argument consistently across `add` / `update` / `set-default` / `delete` (matches project/issue/epic/agent resource-identifier style).
- The "set as default" flag is unified to `--set-default` (verb form); the top-level's `--default` is dropped.
- Delete verb: `delete` becomes the primary name (spec 词表 alignment), with `remove` / `rm` as aliases (currently `remove` is primary).
- Fix the convention破口: every `mo repo` subcommand accepts both `--project` and `--project-id` via `ProjectRefOption()` (currently only `--project-id`).
- Output goes through the shared `OutputOption()` factory (`-o table|json`) instead of raw print calls.
- Server endpoints are untouched (one set already exists; the problem is purely the CLI double-track).

## Capabilities

- `repository-cli-commands`: The unified `mo repo` command surface — the complete subcommand set (`list` / `add` / `update` / `set-default` / `delete`), positional `<name>` argument, `--project` / `--project-id` scope flags, the `--set-default` flag name, `delete` as primary delete verb (with `remove` / `rm` aliases), and shared `OutputOption()` formatting. This is the contract the post-change command face must satisfy.

## Impact

- **CLI** (`packages/cli/Mohist.Cli/`):
  - `MohistCliCommands.Repository.cs` — rewrite to the unified face: add `set-default`, flip `remove`→`delete` primary, swap `--default`→`--set-default`, replace `ProjectIdOption()` with `ProjectRefOption()` on all subcommands, adopt `OutputOption()` + `PrintWithOutputAsync`.
  - `MohistCliCommands.ProjectRepo.cs` — **deleted**.
  - `MohistCliCommands.Project.cs:17` — remove the `ProjectRepoCommands.Build(api)` registration.
- **Tests** (`packages/cli/tests/Mohist.Cli.Tests/`): `CliRepositoryCommandSpecs.cs` updated to cover the new face — unified subcommands, `--project` 接通, `set-default`, `delete` primary naming, and rejection of the dropped `--default` flag. No real external dependencies, no wall clock (per `design/testing.md`).
- **Docs**: `docs/cli-reference.md` 实装差距表 — remove the repo double-track row (line 306) once collapsed; the repo section (lines 127–139) already matches the target form.
- **No server changes**: `/api/projects/{id}/repositories` endpoints stay as-is.
- **No schema migration**; the only breaking surface is the removed `mo project repo` path.
