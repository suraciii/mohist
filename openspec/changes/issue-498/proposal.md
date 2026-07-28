## Why

`mo issue workflow` currently presents WorkflowRun reads as Issue actions even though its `status` command duplicates `mo run view --issue`, and its boundary text incorrectly directs users to the wrong place for Workflow Profile selection. This leaves the CLI with competing paths for the same Run information and makes the command tree diverge from the documented resource model.

## What Changes

- Retire the `mo issue workflow` subcommand tree, including its duplicate Run-status entry point.
- Make `mo run` the sole CLI area for reading a WorkflowRun by Run ID or by its bound Issue.
- Move the existing workflow timeline read into `mo run` so its information remains available through the Run command surface.
- Keep Issue Workflow Profile selection on `mo issue create/edit` with `--workflow-profile` and `--inherit-workflow-profile` unchanged.
- Align CLI help, command-tree contracts, and the CLI reference with the resulting ownership boundary.

## Capabilities

- `workflow-run-cli`: The canonical CLI paths for viewing a WorkflowRun and its timeline, including Issue-number target resolution, retirement of duplicate Issue workflow reads, and help/reference discoverability.

## Impact

- **CLI:** `packages/cli/Mohist.Cli/` command registration, Issue workflow reads, Run reads, and their command/help contract tests.
- **Documentation:** `docs/cli-reference.md` command map and implementation-gap note.
- **Public command surface:** `mo issue workflow status` and `mo issue workflow timeline` are removed; equivalent Run status and timeline reads are reached through `mo run`.
- **No server, runner, web, dependency, Workflow Profile-selection, or workflow-state semantic changes.**
