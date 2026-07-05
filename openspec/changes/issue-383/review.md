# Review Report

## Result: FAIL

## Repaired Items

_None._

## Blocking Items

- [ID: item-1]
  Severity: warning
  Scope: `packages/cli/Mohist.Cli/MohistCliCommands.Repository.cs`
  Evidence: Every repo subcommand now accepts `--output`/`-o`, but the mutating subcommands call `PrintPostWithOutputAsync`, `PrintPatchWithOutputAsync`, and `PrintDeleteWithOutputAsync` without a table shape (`Repository.cs:96-105`, `160-163`, `195-198`, `232-234`). In table mode, `MohistCliApi.PrintEnvelopeAsync` parses a null shape as `TableShape.ProjectList` (`MohistCliApi.cs:751-768`, `814-820`), so a successful `mo repo add/update/set-default/delete ... -o table` renders through the project-list table path. For a normal repository object response, `TableRenderer.RenderProjectList` sees a non-array as an empty project list and prints `No projects` (`TableRenderer.Entities.cs:7-13`; `TableRenderer.cs:282-285`). This violates the acceptance criterion that all repo subcommands render via the shared output option as repository commands, and it creates misleading success output. [disallowed:product-behavior-change]
  SuggestedAction: Pass an appropriate repository table shape/fallback for mutating repo commands, or deliberately render a generic success/table representation that cannot say `No projects` for repository mutations. Add coverage for `repo add`, `repo update`, `repo set-default`, and `repo delete` with `-o table`.
  Verification: Add CLI specs that invoke each mutating repo subcommand with `-o table` against a successful repository response and assert the output is repository-appropriate and does not contain `No projects`; then run `dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj -p:SkipWebBuild=true --filter FullyQualifiedName~CliRepositoryCommandSpecs`.
  Status: open

- [ID: item-2]
  Severity: test-gap
  Scope: `packages/cli/tests/Mohist.Cli.Tests/CliRepositoryCommandSpecs.cs`
  Evidence: The new tests cover `list -o table`, `list -o json`, `add -o json`, and invalid output for `add` (`CliRepositoryCommandSpecs.cs:354-458`), but they do not exercise `-o table` on any mutating repo command. That gap misses item-1 even though the issue explicitly requires all repo subcommands to use `OutputOption()` and the candidate added `--output` to add/update/set-default/delete. [disallowed:test-coverage]
  SuggestedAction: Add regression tests for table output on the mutating repo subcommands, preferably one shared theory over add/update/set-default/delete.
  Verification: The targeted CLI repository spec command above should include and pass the new table-mode cases.
  Status: open

## Follow-up Items

_None._

## Pre-existing or Out-of-scope Items

- [ID: item-3]
  Severity: warning
  Scope: `packages/cli/tests/Mohist.Cli.Tests/CliReferenceDocsSpecs.cs` / `docs/cli-reference.md`
  Evidence: The full CLI test project currently fails three unchanged doc-consistency tests: missing `mo issue comment add <number>`, missing output/project flag prose, and missing `mo server start`. The same expected strings are already absent from `master:docs/cli-reference.md`, and this candidate only adds `mo repo` to the root command list and removes the resolved repo double-track gap row. This is unrelated existing docs/test drift.
  SuggestedAction: Fix the CLI reference doc/test drift in a separate docs issue, or update the stale assertions if the current CLI reference intentionally changed.
  Status: pre-existing

- [ID: item-4]
  Severity: info
  Scope: test execution
  Evidence: `dotnet test Mohist.sln -p:SkipWebBuild=true` did not complete as a solution run: server tests reported `Passed: 2616, Skipped: 3`, then the run ended with `Test Run Aborted` / `Test host process crashed`. Targeted repo-related tests were run separately and passed.
  SuggestedAction: Investigate the solution-level test host crash separately if it reproduces outside this issue.
  Status: out-of-scope

## Verification Summary

- Issue details read with `mo issue show 383 --project-id proj_f6c141d63b6243bfbb481737b2243b87`.
- Reviewed workflow artifacts under `openspec/changes/issue-383/`: `proposal.md`, `design.md`, `tasks.json`, `specs/repository-cli-commands/spec.md`, and `self-review.md`.
- Reviewed all changed product files: `docs/cli-reference.md`, `release/CHANGELOG.md`, `specs/cli-project-repositories/spec.md`, `packages/cli/Mohist.Cli/MohistCliCommands.Repository.cs`, `packages/cli/Mohist.Cli/MohistCliCommands.Project.cs`, `packages/cli/tests/Mohist.Cli.Tests/CliRepositoryCommandSpecs.cs`, `packages/server/tests/Mohist.Server.Tests/Specs/Api/ProjectCliRepositorySpecs.cs`, and `packages/server/tests/Mohist.Server.Tests/Specs/Project/Api/ProjectCliSpecs.cs`.
- `dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj -p:SkipWebBuild=true --filter FullyQualifiedName~CliRepositoryCommandSpecs`: PASS, 32/32.
- `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj -p:SkipWebBuild=true --filter FullyQualifiedName~ProjectCliRepositorySpecs`: PASS, 24/24.
- `dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj -p:SkipWebBuild=true`: FAIL, 3 unchanged `CliReferenceDocsSpecs` failures, recorded as pre-existing/out-of-scope.
- `dotnet test Mohist.sln -p:SkipWebBuild=true`: ABORTED after server test pass summary, recorded as out-of-scope.

<promise>FAIL</promise>
