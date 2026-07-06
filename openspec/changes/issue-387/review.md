# Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: test-quality
  Evidence: `CliSystemLogsCommandSpecs.System_Help_ListsLogsAlongsideInfo` asserted `Assert.Contains("info", stdout)` against `mo system --help`, with a comment claiming `info` was still a sibling "until T-005 relocates it later". T-005 has already landed, so `system` no longer exposes `info`; the assertion passed only because the group description mentions `mo info`. This was a false-positive that did not verify its stated intent.
  Verification: `dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj --filter "FullyQualifiedName~CliSystemLogsCommandSpecs"` → 7/7 passed.
  Status: resolved

- [ID: item-2]
  Severity: info
  Scope: documentation-consistency
  Evidence: `MohistCliCommands.Notify.cs:142` XML doc comment still said "Internal command flow used by `mo notify setup`" even though the command surface is now `mo notification setup`.
  Verification: `dotnet build Mohist.sln -p:SkipWebBuild=true` → 0 warnings, 0 errors.
  Status: resolved

- [ID: item-3]
  Severity: warning
  Scope: test-coverage / sibling-regression
  Evidence: `packages/server/tests/Mohist.Server.Tests/Specs/Project/Api/ProjectCliSpecs.cs:46` `Use_ByName_PersistsActiveProjectInCliState` invoked the removed legacy path `mo use <project>` and failed in the post-build snapshot. I verified against the pre-T-001 baseline (`git show 7abbd237e`) that this test was passing before issue #387 started, so the failure is a regression introduced by removing `mo use`, not a pre-existing failure as claimed in `progress.txt`. The test is also redundant with `ProjectUse_ByName_PersistsActiveProjectInCliState` directly above it, which already covers `mo project use`. Removed the redundant test.
  Verification: `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter "FullyQualifiedName~ProjectCliSpecs"` → 7/7 passed.
  Status: resolved

## Blocking Items

None.

## Follow-up Items

- [ID: item-4]
  Severity: follow-up
  Scope: `packages/cli/Mohist.Cli/MohistCliCommands.Notify.cs`
  Evidence: Two user-facing guidance strings still tell the user to "re-run 'mo notify setup'" (lines 413 and 601) even though the canonical path is now `mo notification setup`. The existing spec assertions only anchor on `Hermes webhook platform is not started.` and `docs/hermes-notifications.md`, so tests pass, but the stale command reference is confusing for users. Note: changing these strings would alter "output wording", which design D2 preserved byte-identical; handle as a post-migration wording cleanup.
  SuggestedAction: Update both strings to reference `mo notification setup`, and add assertions that pin the canonical command name in the relevant specs.
  Status: follow-up

- [ID: item-5]
  Severity: follow-up
  Scope: `design/mobile-pwa.md`
  Evidence: The design doc still references `mo notify setup` in two places (#352 callouts). T-006's doc sweep covered `docs/` but did not sweep `design/`, so this stale reference remains.
  SuggestedAction: Replace `mo notify setup` with `mo notification setup` in `design/mobile-pwa.md` to keep design docs consistent with the command surface.
  Status: follow-up

- [ID: item-6]
  Severity: follow-up
  Scope: `packages/cli/tests/Mohist.Cli.Tests/CliSystemInfoCommandSpecs.cs`
  Evidence: `Server_Help_ListsInfoSubcommand` uses the weak assertion `Assert.Contains("info", stdout)`, which could match any substring occurrence. It happens to be safe for `mo server --help` today, but an anchored row assertion (`\n  info `) would be more robust and consistent with the negative assertion in `System_Help_NoLongerListsInfo`.
  SuggestedAction: Strengthen the assertion to anchor on the System.CommandLine help row layout.
  Status: follow-up

- [ID: item-7]
  Severity: follow-up
  Scope: `packages/cli/tests/Mohist.Cli.Tests/CliProjectStatusCommandSpecs.cs`
  Evidence: `Project_Help_ListsStatusAlongsideOtherVerbs` uses the unanchored `Assert.Contains("use", stdout)`. The implementation notes acknowledge this is a false-positive trap because `use` is a substring of `repository`, `because`, etc. The combined set of assertions makes the overall test reliable, but anchoring would eliminate the risk.
  SuggestedAction: Replace the bare `Assert.Contains("use", stdout)` with an anchored `Assert.Contains("\n  use ", stdout)`.
  Status: follow-up

## Pre-existing or Out-of-scope Items

None. (The only test that failed in the post-build snapshot was the redundant server `Use_ByName_...` test; see Repaired item-3.)

## Verification Summary

- `dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj` → 829/829 passed.
- `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter "FullyQualifiedName~ProjectCliSpecs"` → 7/7 passed.
- `dotnet build Mohist.sln -p:SkipWebBuild=true` → 0 warnings, 0 errors.
- `rg --fixed-strings 'mo status|mo logs|mo use|mo notify|mo system info' docs/` → no matches.

## Notes

- The no-alias policy decision is correctly recorded in issue comment `cmt_4035bafdcf844f6b8d922b056ea34631`.
- `docs/cli-reference.md` gap table now only retains the `mo server install/update` vs `mo install/update` double-entry row, which is explicitly out of scope for issue #387.
- All five migrated command paths (`mo project status`, `mo system logs`, `mo project use`, `mo notification setup`, `mo server info`) are implemented, tested, and documented.
- `mo info` remains unchanged as the single controlled root-layer exception.

<promise>PASS</promise>
