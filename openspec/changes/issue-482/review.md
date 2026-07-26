# Review: Issue 482

## Findings

### P1: Activity help does not list the JSON fields accepted at runtime

`mo activity list --help` prints a `JSON FIELDS` heading with no fields. [CommandPresentations.cs](/home/szf/.mohist/projects/workspaces/wr_dda5615218a9496aa68f2b30db2217a7/packages/cli/Mohist.Cli/CommandPresentations.cs:545) attaches no `JsonFields` for this leaf, while [MohistCliCommands.Activity.cs](/home/szf/.mohist/projects/workspaces/wr_dda5615218a9496aa68f2b30db2217a7/packages/cli/Mohist.Cli/MohistCliCommands.Activity.cs:11) accepts the 13 fields in `ActivityListDescriptor`, including `provenance`. The generic renderer only prints fields from presentation metadata at [CommandHelpRenderer.cs](/home/szf/.mohist/projects/workspaces/wr_dda5615218a9496aa68f2b30db2217a7/packages/cli/Mohist.Cli/CommandHelpRenderer.cs:147). This violates the leaf-help contract that `--json` help list the fields accepted at runtime. Attach the descriptor's fields (or derive them from the runtime descriptor) and add a help/runtime parity assertion such as `provenance`.

### P2: Long option names are concatenated with their descriptions in leaf help

[CommandHelpRenderer.cs](/home/szf/.mohist/projects/workspaces/wr_dda5615218a9496aa68f2b30db2217a7/packages/cli/Mohist.Cli/CommandHelpRenderer.cs:236) uses `PadRight(20)` without adding a separator when an option name already exceeds that width. As a result, `mo issue edit --help` renders `--inherit-workflow-profileClear the explicit...` and `--stage-model-variantsPer-stage...`, making the options difficult to identify and copy accurately. Render at least one separator after every option name, or place overflowing descriptions on a continuation line, and add a regression test for an option longer than the layout column.

## Verification

`dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj --no-restore` passed: 1,391 tests.

`npm test` passed.

<promise>FAIL</promise>
