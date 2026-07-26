# Review: Issue 482

## Findings

### P1: Output-mode command help still omits the JSON fields accepted at runtime

The shared [OutputOption](/home/szf/.mohist/projects/workspaces/wr_dda5615218a9496aa68f2b30db2217a7/packages/cli/Mohist.Cli/MohistCliCommands.cs:74) is used by many resource commands and implements field selection: `mo workflow list --json` locally prints `id`, `name`, `displayName`, `description`, and `isDefault`, while `--json number,title` is rejected as invalid fields. But `mo workflow list --help` renders an empty `JSON FIELDS` section because [CommandHelpRenderer.cs](/home/szf/.mohist/projects/workspaces/wr_dda5615218a9496aa68f2b30db2217a7/packages/cli/Mohist.Cli/CommandHelpRenderer.cs:147) only prints optional presentation metadata, which is absent for that leaf; `mo skill list --help` has the same defect. This violates the required help/runtime field parity across every resource leaf using `OutputOption`. Associate each such command with its existing resource-output descriptor (or make the output option carry that descriptor) and add a parity sweep covering both `OutputOption` and `JsonSelectionOption` leaves.

## Verification

`dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj --no-restore` passed: 1,396 tests.

`npm test` passed.

<promise>FAIL</promise>
