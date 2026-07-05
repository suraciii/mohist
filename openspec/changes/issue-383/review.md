# Review Report

## Result: FAIL

## Repaired Items

_None._

## Blocking Items

- [ID: item-1]
  Severity: warning
  Scope: `packages/cli/Mohist.Cli/MohistCliCommands.Repository.cs`
  Evidence: The implementation still makes `repository` the canonical command name and `repo` only an alias: `new Command("repository", ...)` at `packages/cli/Mohist.Cli/MohistCliCommands.Repository.cs:10` and `repository.Aliases.Add("repo")` at line 11. The issue/spec require the single entry to be canonical `mo repo` with `repository` as alias (`openspec/changes/issue-383/specs/repository-cli-commands/spec.md:3-5`). Actual post-build help confirms the mismatch: `dotnet run --project packages/cli/Mohist.Cli/Mohist.Cli.csproj -- repo --help` prints `Usage: Mohist.Cli repository [command] [options]`. [disallowed:public-contract-change]
  SuggestedAction: Flip the command definition to `new Command("repo", ...)` and add `repository` as the alias. Add a regression assertion that `mo repo --help` usage advertises `repo`, while `mo repository ...` remains accepted as the alias.
  Verification: Run `dotnet run --project packages/cli/Mohist.Cli/Mohist.Cli.csproj -- repo --help` and confirm the usage line uses `repo`; run the CLI repository specs.
  Status: open

- [ID: item-2]
  Severity: blocking
  Scope: `specs/cli-project-repositories/spec.md`
  Evidence: An active root spec outside the issue workflow artifact boundary still mandates the removed command surface: `specs/cli-project-repositories/spec.md:3-5` says the CLI SHALL expose `mo project repo`, lines 17-29 specify `mo project repo add/set-default/remove`, and lines 84-96 say only `mo project repo list` accepts `--output` while mutating commands do not. This directly contradicts the candidate behavior and issue acceptance criteria that remove `mo project repo` and add `--output` to every `mo repo` subcommand. [disallowed:product-spec-change]
  SuggestedAction: Update/archive the active spec so the current product spec agrees with the new `mo repo` contract, or explicitly supersede it with the new repository CLI spec during integration.
  Verification: `grep -R "mo project repo" specs docs design packages -n` should return no active product/design spec requiring the removed command path; tests should still reject `mo project repo`.
  Status: open

- [ID: item-3]
  Severity: warning
  Scope: `release/CHANGELOG.md`
  Evidence: The issue and proposal call out a breaking path removal and migration note (`mo project repo` -> `mo repo --project`, and `--default` -> `--set-default`), but `release/CHANGELOG.md:1-44` only documents issue #381 workflow changes. A repo-wide markdown scan found the migration note only in workflow artifacts, not in release notes. [disallowed:public-contract-change]
  SuggestedAction: Add an unreleased changelog entry for issue #383 documenting the removed `mo project repo` path, the replacement `mo repo --project` form, the dropped `--default` flag, and the canonical `delete` verb.
  Verification: `grep -R "mo project repo\|mo repo --project\|--set-default" release/CHANGELOG.md` should show the migration note.
  Status: open

- [ID: item-4]
  Severity: test-gap
  Scope: `packages/cli/tests/Mohist.Cli.Tests/CliRepositoryCommandSpecs.cs`, `packages/server/tests/Mohist.Server.Tests/Specs/Api/ProjectCliRepositorySpecs.cs`
  Evidence: The acceptance criteria require every repo subcommand to accept both `--project` and `--project-id`, and require `mo repo` to be the single/canonical entry. The CLI specs cover `list --project`, `list --project-id`, and `add --project` (`CliRepositoryCommandSpecs.cs:92-106`, `243-273`) but do not cover `update --project`, `update --project-id`, `set-default --project-id`, or `delete --project-id`. The help test at `CliRepositoryCommandSpecs.cs:24-41` only asserts the subcommand names are present and would not catch item-1's canonical-name mismatch.
  SuggestedAction: Add a parameterized coverage matrix for `list/add/update/set-default/delete` with both `--project` and `--project-id`, plus a help/usage assertion for canonical `repo` and alias `repository`.
  Verification: Run `dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj --filter CliRepositoryCommandSpecs`.
  Status: open

- [ID: item-5]
  Severity: minor
  Scope: `docs/cli-reference.md`
  Evidence: The root command list in `docs/cli-reference.md:31-51` omits `mo repo` even though the same document now describes Repository as a top-level resource at `docs/cli-reference.md:127-139`. With this issue making `mo repo` the single repository-management entry, the product reference remains internally inconsistent. [disallowed:product-doc-change]
  SuggestedAction: Add `mo repo` to the root command list alongside the other top-level resources.
  Verification: Re-read `docs/cli-reference.md` and confirm the root list and Repository section both describe the same top-level command.
  Status: open

- [ID: item-6]
  Severity: cleanup
  Scope: `packages/server/tests/Mohist.Server.Tests/Specs/Project/Api/ProjectCliSpecs.cs`
  Evidence: `RepositoryAdd_WithPathOnly_IsRejectedWithValidationError` at `packages/server/tests/Mohist.Server.Tests/Specs/Project/Api/ProjectCliSpecs.cs:236` no longer exercises a path-only command. It now invokes `repository add backend --git-url /proj/backend` at line 258 and asserts that the request body contains `gitUrl` at lines 264-270. The stale test name makes future failures misleading.
  SuggestedAction: Rename the test to describe the current behavior, or remove it if the path-only fallback is intentionally gone and already covered elsewhere.
  Verification: Run `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter ProjectCliSpecs`.
  Status: open

## Follow-up Items

_None._

## Pre-existing or Out-of-scope Items

- [ID: item-7]
  Severity: warning
  Scope: npm dependency audit
  Evidence: During `dotnet build Mohist.sln` and focused server test runs, npm audit output reported 9 vulnerabilities (3 moderate, 3 high, 3 critical). The candidate did not change package manifests or lockfiles, so this appears pre-existing and unrelated to the repo CLI change.
  SuggestedAction: Track dependency audit cleanup separately if not already covered.
  Status: pre-existing

## Acceptance Criteria Evidence

- Nested `mo project repo` registration is removed: `packages/cli/Mohist.Cli/MohistCliCommands.Project.cs:12-18` no longer adds `ProjectRepoCommands.Build(api)`, and `packages/cli/Mohist.Cli/MohistCliCommands.ProjectRepo.cs` is deleted.
- `mo repo` behavior is implemented through `RepositoryCommands`: subcommands are added at `packages/cli/Mohist.Cli/MohistCliCommands.Repository.cs:13-17`; `set-default` is implemented at lines 169-202; `delete` with `remove`/`rm` aliases is implemented at lines 204-238.
- Positional names and `--set-default` are implemented for `add`, `update`, `set-default`, and `delete` at `packages/cli/Mohist.Cli/MohistCliCommands.Repository.cs:58-70`, `114-128`, `172-178`, and `209-215`.
- `ProjectRefOption()` is wired on each subcommand at `packages/cli/Mohist.Cli/MohistCliCommands.Repository.cs:26`, `62`, `119`, `173`, and `210`; output options are wired at lines 27, 63, 120, 174, and 211.
- Focused verification passed: `dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj --filter CliRepositoryCommandSpecs` (21 passed), `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter ProjectCliRepositorySpecs` (24 passed), `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter ProjectCliSpecs` (8 passed), `dotnet build Mohist.sln` (0 warnings, 0 errors), and full `npm test` (69 runner test files / 924 runner tests passed after .NET test phase completed successfully). An earlier `npm test` attempt hit the 120s tool timeout and was rerun with a 300s timeout.

<promise>FAIL</promise>
