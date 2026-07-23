# Review — issue-480

## Findings

### F1 — Runner still exposes the removed local lifecycle surface and `show`

`packages/cli/Mohist.Cli/MohistCliCommands.Server.cs:73-91` still registers `runner start`, `stop`, `restart`, `service-status`, `logs`, and `uninstall` through `IServiceInstaller`. The same class registers `BuildShow(api)` at lines 90 and 146-184, while there is no `runner view` command. Consequently, the implementation violates the runner command-surface requirement and the acceptance criteria requiring `runner --help` to contain only `list`, `view`, and `status`, `runner show` to fail, and all old runner lifecycle paths to be removed without aliases. Remove those registrations and rename the detail command to `view`; retain only the Server API-backed read commands.

The current `packages/cli/tests/Mohist.Cli.Tests/CliRunnerCommandSpecs.cs` reinforces the old behavior rather than detecting it: lines 50-67 and 654-672 expect lifecycle verbs and `show` in help, and lines 498-651 invoke `runner show`. Replace these specs with assertions for `view` and for non-resolution/no installer calls on every removed path, otherwise the test suite will remain green while the acceptance criteria are false.

### F2 — Server update failure still emits a dead lifecycle hint

`packages/server/src/Mohist.Server/SystemInfo/SystemUpdateService.cs:555` still writes `Start the runner manually with: mo runner start` into the update failure log. That path is user-visible through Server system/update diagnostics and violates the issue requirement that old Runner/Server lifecycle paths be removed from hints. Update it to the canonical local command `mo service start runner`, and update the corresponding assertions in `packages/server/tests/Mohist.Server.SpecTests/Specs/SystemSpecs/SystemUpdateFailureRecoverySpecs.cs:79` and its second occurrence around line 165. The CLI-side hint changes do not cover this Server-side message.

## Verification

`dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj --no-restore` compiled the CLI and test projects, but test execution was unavailable because the .NET 11 runtime is not installed in the environment.

<promise>FAIL</promise>
