# Review: Issue 482

## Findings

### P1: Issue create and edit help omit their runtime JSON fields

`mo issue create --help` and `mo issue edit --help` both render an empty `JSON FIELDS` section. [CommandPresentations.cs](/home/szf/.mohist/projects/workspaces/wr_dda5615218a9496aa68f2b30db2217a7/packages/cli/Mohist.Cli/CommandPresentations.cs:157) and [CommandPresentations.cs](/home/szf/.mohist/projects/workspaces/wr_dda5615218a9496aa68f2b30db2217a7/packages/cli/Mohist.Cli/CommandPresentations.cs:164) attach no `JsonFields`, although both runtime handlers parse `--json` against `IssueDescriptor` at [MohistCliCommands.Issue.CrudWrites.cs](/home/szf/.mohist/projects/workspaces/wr_dda5615218a9496aa68f2b30db2217a7/packages/cli/Mohist.Cli/MohistCliCommands.Issue.CrudWrites.cs:65) and line 267. The generic renderer prints fields solely from that metadata at [CommandHelpRenderer.cs](/home/szf/.mohist/projects/workspaces/wr_dda5615218a9496aa68f2b30db2217a7/packages/cli/Mohist.Cli/CommandHelpRenderer.cs:147). This violates the leaf-help runtime-parity requirement. Attach `IssueDescriptor` fields to both presentations, preferably from the existing descriptor authority, and add help/runtime parity coverage for create and edit.

### P2: Public documentation still advertises removed command paths

The command-language migration left four user-facing references to commands that now fail parser validation: [repositories.md](/home/szf/.mohist/projects/workspaces/wr_dda5615218a9496aa68f2b30db2217a7/docs/repositories.md:21) uses `mo repo update`; [epics.md](/home/szf/.mohist/projects/workspaces/wr_dda5615218a9496aa68f2b30db2217a7/docs/epics.md:106), line 109, and line 153 use `mo epic show`. The canonical paths are `mo repo edit` and `mo epic view`. Update the examples and prose, then extend the documentation/path guard so these removed verbs cannot be reintroduced.

### P2: The checked-in Mohist Skill discovery stub directs Agents to a removed command

[.agents/skills/mohist/SKILL.md](/home/szf/.mohist/projects/workspaces/wr_dda5615218a9496aa68f2b30db2217a7/.agents/skills/mohist/SKILL.md:8) tells Agents to run `mo skills get mohist`, but the canonical tree only supports `mo skill view mohist`. The stub is the first guidance loaded for Mohist work, so following it prevents an Agent from obtaining the packaged Skill. Update the stub to the canonical invocation and add it to the command-example/path validation sweep.

## Verification

`dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj --no-restore` passed: 1,392 tests.

`npm test` passed.

<promise>FAIL</promise>
