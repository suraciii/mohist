# Acceptance Evidence

## Complexity

- Run `scc packages/cli/Mohist.Cli --format csv --sort complexity` after the refactor.
- Expected evidence: none of `MohistCliCommands.Update*.cs`, `Update/*.cs`, `InfoCollector*.cs`, `InfoRenderer.cs`, `SystemdUnitParser.cs`, or `TableRenderer*.cs` appears in the top five single-file complexity rows.

## CLI Behavior

- Existing CLI command tests exercise table output strings and update/info command behavior: `dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj`.
- No CLI command registration, option, project reference, or package reference is intentionally changed by this repair.

## Out-of-Scope Runner Changes

- Runner files previously changed in this candidate were restored to the base branch to remove the unrelated ACP liveness regression from issue #254.
