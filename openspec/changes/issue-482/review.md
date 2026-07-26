# Review: Issue 482

## Findings

### P1: Agent creation still validates instructions after Project resolution

`agent create` resolves the Project at [MohistCliCommands.Agent.cs](/home/szf/.mohist/projects/workspaces/wr_dda5615218a9496aa68f2b30db2217a7/packages/cli/Mohist.Cli/MohistCliCommands.Agent.cs:287) before checking the required `--instructions`/`--instructions-file` source at [MohistCliCommands.Agent.cs](/home/szf/.mohist/projects/workspaces/wr_dda5615218a9496aa68f2b30db2217a7/packages/cli/Mohist.Cli/MohistCliCommands.Agent.cs:292). A missing source or unreadable instructions file therefore returns exit code 1 at line 294 rather than a scoped usage error, and can perform Project resolution first. The help contract requires invalid local input to exit 2 with a diagnostic and nearest usage before Project resolution or remote work. Resolve and validate instructions before resolving the Project, route failures through `CommandHelpHook.RenderUsageFailure`, and add no-request regression coverage for missing and unreadable inputs.

## Verification

`dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj --no-restore` passed: 1,386 tests.

<promise>FAIL</promise>
