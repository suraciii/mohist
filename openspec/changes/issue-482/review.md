# Review: Issue 482

## Findings

### P1: Resource and relationship actions remain outside the canonical verb vocabulary

`mo repo add` and `mo label add` create resources at [MohistCliCommands.Repository.cs](/home/szf/.mohist/projects/workspaces/wr_dda5615218a9496aa68f2b30db2217a7/packages/cli/Mohist.Cli/MohistCliCommands.Repository.cs:55) and [MohistCliCommands.Label.cs](/home/szf/.mohist/projects/workspaces/wr_dda5615218a9496aa68f2b30db2217a7/packages/cli/Mohist.Cli/MohistCliCommands.Label.cs:53), while the Epic/Issue relationship uses `link` and `unlink` at [MohistCliCommands.Epic.cs](/home/szf/.mohist/projects/workspaces/wr_dda5615218a9496aa68f2b30db2217a7/packages/cli/Mohist.Cli/MohistCliCommands.Epic.cs:214) and [MohistCliCommands.Epic.cs](/home/szf/.mohist/projects/workspaces/wr_dda5615218a9496aa68f2b30db2217a7/packages/cli/Mohist.Cli/MohistCliCommands.Epic.cs:254). The command-language spec reserves `create` for resource mutation and `add`/`remove` for relationship changes. Rename these registrations and all public examples, then assert the old forms are local usage failures with no request.

### P1: Agent instructions retain an unsupported stdin grammar

`mo agent create --instructions -` is still documented and accepted as stdin at [MohistCliCommands.Agent.cs](/home/szf/.mohist/projects/workspaces/wr_dda5615218a9496aa68f2b30db2217a7/packages/cli/Mohist.Cli/MohistCliCommands.Agent.cs:250) and directly reads standard input at [MohistCliCommands.Agent.cs](/home/szf/.mohist/projects/workspaces/wr_dda5615218a9496aa68f2b30db2217a7/packages/cli/Mohist.Cli/MohistCliCommands.Agent.cs:631). The specification permits stdin for long text only as `--<name>-file -` or `--file -`; there is no `--instructions-file` form. Add that canonical file option, route `-` through the shared resolver, and reject the standalone `--instructions -` syntax.

### P1: Local validation is still late and does not consistently return usage failures

`session followup` resolves the Project before validating the local text source at [MohistCliCommands.Session.cs](/home/szf/.mohist/projects/workspaces/wr_dda5615218a9496aa68f2b30db2217a7/packages/cli/Mohist.Cli/MohistCliCommands.Session.cs:253), then maps a missing or conflicting `--text`/`--text-file` source to exit code 1 at [MohistCliCommands.Session.cs](/home/szf/.mohist/projects/workspaces/wr_dda5615218a9496aa68f2b30db2217a7/packages/cli/Mohist.Cli/MohistCliCommands.Session.cs:260). `issue archive` has the same contract violation for mutually exclusive or missing local inputs at [MohistCliCommands.Issue.Lifecycle.cs](/home/szf/.mohist/projects/workspaces/wr_dda5615218a9496aa68f2b30db2217a7/packages/cli/Mohist.Cli/MohistCliCommands.Issue.Lifecycle.cs:106). The help specification requires invalid local input to exit 2 with scoped stderr usage and no Project resolution or remote work. Validate before resolution and route failures through `CommandHelpHook.RenderUsageFailure` with regression tests for each path.

### P1: Nested Agent Job help still prints HTTP implementation details

The generic raw-description fallback in [CommandHelpRenderer.cs](/home/szf/.mohist/projects/workspaces/wr_dda5615218a9496aa68f2b30db2217a7/packages/cli/Mohist.Cli/CommandHelpRenderer.cs:75) remains reachable for nested commands without presentation metadata. `mo agent job --help` currently prints `GETs .../agents/{agentId}/jobs` and `GETs .../agent-jobs/{jobId}` from [MohistCliCommands.Agent.cs](/home/szf/.mohist/projects/workspaces/wr_dda5615218a9496aa68f2b30db2217a7/packages/cli/Mohist.Cli/MohistCliCommands.Agent.cs:709) and [MohistCliCommands.Agent.cs](/home/szf/.mohist/projects/workspaces/wr_dda5615218a9496aa68f2b30db2217a7/packages/cli/Mohist.Cli/MohistCliCommands.Agent.cs:757). This violates the explicit ban on API routes and implementation details in help. Provide user-facing presentations for nested leaves or remove the raw fallback, and add a regression assertion for this group.

## Verification

`dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj --no-restore` passed: 1,378 tests.

<promise>FAIL</promise>
