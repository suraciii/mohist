## Why

`mo issue workflow` currently presents WorkflowRun reads as Issue actions even though its `status` command duplicates `mo run view --issue`, and its boundary text incorrectly directs users to the wrong place for Workflow Profile selection. This leaves the CLI with competing paths for the same Run information and makes the command tree diverge from the documented resource model.

## What Changes

- Retire the `mo issue workflow` subcommand tree, including its duplicate Run-status entry point and its nonfunctional timeline entry point.
- Make `mo run` the sole CLI area for reading a WorkflowRun by Run ID or by its bound Issue.
- Keep the ordered stage progression available through `mo run view`; do not add a separate `mo run timeline` path because it would duplicate that Run detail read.
- Keep Issue Workflow Profile selection on `mo issue create/edit` with `--workflow-profile` and `--inherit-workflow-profile` unchanged.
- Align CLI help, command-tree contracts, and the CLI reference with the resulting ownership boundary.

## Capabilities

- `workflow-run-cli`: The canonical CLI path for viewing a WorkflowRun, including Issue-number target resolution, retirement of the duplicate Issue workflow subtree, and help/reference discoverability.

## Impact

- **CLI:** `packages/cli/Mohist.Cli/` command registration, Issue workflow reads, Run reads, and their command/help contract tests.
- **Documentation:** `docs/cli-reference.md` command map and implementation-gap note.
- **Public command surface:** `mo issue workflow status` and `mo issue workflow timeline` are removed; `mo run view` remains the sole Run detail read, and no `mo run timeline` command is introduced.
- **No server, runner, web, dependency, Workflow Profile-selection, or workflow-state semantic changes.**
