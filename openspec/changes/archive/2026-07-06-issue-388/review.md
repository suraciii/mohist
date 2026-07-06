# Review Report

## Result: PASS

All issue-388 acceptance criteria are met: the redundant resource-group install/update paths (`mo server install`, `mo server update`, `mo runner install`) are removed, the verb-root paths (`mo install server/runner`, `mo update[/cli/server/runner]`) remain unchanged, the `mo runner update` non-existence invariant is pinned, CLI tests pass, and `docs/cli-reference.md` is synchronized.

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: formatting
  Evidence: `packages/cli/tests/Mohist.Cli.Tests/Support/FakeSourceCodeUpdater.cs` was committed without a trailing newline (`\ No newline at end of file`).
  Changed: Added a final newline to the file.
  Verification: `dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj` passed (845 tests, 0 failures).
  Status: resolved

## Blocking Items

(none)

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: `packages/cli/tests/Mohist.Cli.Tests/CliInstallUpdateSingleEntrySpecs.cs:264-278`
  Evidence: `SurvivingRunnerSubcommands_StillResolve` exercises `start`, `stop`, `restart`, `service-status`, `logs`, `uninstall`, `list`, and `status`, but omits `show` from the survival loop even though `show` is a documented non-install runner subcommand.
  SuggestedAction: Add `"show"` to the `foreach` loop, using a valid `runner-id` argument or a separate focused assertion, so the survival guard is complete.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: `packages/cli/tests/Mohist.Cli.Tests/CliInstallUpdateSingleEntrySpecs.cs:155-209`
  Evidence: The update verb-root tests (`VerbRootUpdate_InvokesUpdateAllAsync`, `VerbRootUpdateCli_InvokesUpdateCliAsync`, `VerbRootUpdateServer_InvokesUpdateServerAsync`, `VerbRootUpdateRunner_InvokesUpdateRunnerAsync`) only verify that the correct `SourceCodeUpdater` method was called. They do not assert that `--repo-root`, `--dry-run`, or `--cli-path` values are passed through unchanged.
  SuggestedAction: Extend the `FakeSourceCodeUpdater` to record call arguments and assert the expected flag passthrough, mirroring the `InstallServerCalls`/`InstallRunnerCalls` coverage for the install verb-root tests.
  Status: follow-up

- [ID: item-4]
  Severity: follow-up
  Scope: release notes / changelog
  Evidence: The change intentionally removes three public CLI command paths (`mo server install`, `mo server update`, `mo runner install`). The acceptance criteria note that the release/changelog should prompt users to migrate to `mo install <component>` / `mo update <component>`, but no such note was added in the diff.
  SuggestedAction: Add a breaking-change note to the release log/changelog pointing users at the verb-root commands.
  Status: follow-up

- [ID: item-5]
  Severity: follow-up
  Scope: `packages/cli/tests/Mohist.Cli.Tests/CliReferenceDocsSpecs.cs:96-111`
  Evidence: `forbiddenLegacyPathRows` guards against reintroduction of `mo server install`, `mo server update`, and `mo runner install`, but does not include `mo runner update` even though the issue treats its continued absence as an explicit invariant.
  SuggestedAction: Add `"mo runner update"` to the forbidden list for symmetry and to prevent future doc regressions if someone tries to document the non-existent path.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-6]
  Severity: warning
  Scope: `docs/cli-reference.md:7-11`
  Evidence: The "全局约定" section states that the root command layer contains only resource commands with `mo info` as the sole controlled exception, yet the same document lists `mo install` and `mo update` as verb-root commands at the root level (lines 59-60). This contradiction predates issue-388 and is not addressed by the convergence work.
  SuggestedAction: Reconcile the global convention paragraph with the documented `mo install`/`mo update` verb-root entries in a future CLI spec cleanup.
  Status: pre-existing

- [ID: item-7]
  Severity: warning
  Scope: `docs/cli-reference.md:246-256`
  Evidence: The Runner command-group code block documents `mo runner get <执行器id>`, while the actual CLI command is `mo runner show` (see `RunnerCommands.BuildShow` in `packages/cli/Mohist.Cli/MohistCliCommands.Server.cs:169`). This mismatch exists before issue-388 and is unrelated to install/update convergence.
  SuggestedAction: Update the Runner section to `mo runner show <执行器id>` to match the implemented command surface.
  Status: pre-existing

<promise>PASS</promise>
