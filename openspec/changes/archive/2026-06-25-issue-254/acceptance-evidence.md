# Acceptance Evidence

## Complexity

- Command: `scc packages/cli/Mohist.Cli --by-file --format csv --sort complexity`.
- Top five single-file complexity rows after review repairs:
  - `packages/cli/Mohist.Cli/WindowsScheduledTaskInstaller.cs`: complexity 88.
  - `packages/cli/Mohist.Cli/MohistCliCommands.Issue.cs`: complexity 86.
  - `packages/cli/Mohist.Cli/DeliveryFailureGuidance.cs`: complexity 79.
  - `packages/cli/Mohist.Cli/MohistCliCommands.Agent.cs`: complexity 53.
  - `packages/cli/Mohist.Cli/SystemdServiceInstaller.cs`: complexity 52.
- Result: none of `MohistCliCommands.Update*.cs`, `Update/*.cs`, `InfoCollector*.cs`, `InfoVerboseCollector.cs`, `InfoSource*.cs`, `InfoExecStartTokenizer.cs`, `InfoRenderer.cs`, `SystemdUnitParser.cs`, or `TableRenderer*.cs` appears in the top five rows.

## CLI Behavior

- Command tests: `dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj`.
- Byte-for-byte guard: command specs assert exact stdout/stderr for table output, `mo info` rendering, and `mo update` messages, while internal extraction specs cover moved collaborators without weakening public CLI assertions.
- Command surface audit: review repair did not add or remove `new Command(...)` definitions or change `packages/cli/Mohist.Cli/Mohist.Cli.csproj` references; the only CLI command wiring change remains the internal `InfoCommands` renderer resolution from the original refactor.
- Exit-code guard: existing CLI command specs execute through `MohistCliCommands.RunAsync(...)` and assert return codes for covered commands.

## Scope Cleanup

- Out-of-scope runner, web, and non-CLI server/web test changes were restored to `master` content during review repair.
- Remaining non-workflow product changes are limited to `packages/cli/` implementation/tests and directly related CLI-info/update server tests.
