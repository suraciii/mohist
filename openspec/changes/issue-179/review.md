# Review Report

## Result: PASS

## Repaired Items

_None._

## Blocking Items

_None._

## Follow-up Items

_None._

## Pre-existing or Out-of-scope Items

_None identified._

## Acceptance Criteria Evidence

- Close no longer unlinks issues: `EpicGrain.SetStatusAsync("closed")` maps the terminal status and releases only `EpicActiveIssues` slots (`packages/server/src/Mohist.Server/Epic/Grains/EpicGrain.cs:269-280`); `ApplyPendingEvents` now only drains domain events and explicitly does not remove `EpicIssueRow` (`packages/server/src/Mohist.Server/Epic/Grains/EpicGrain.cs:595-604`). Covered by `Close_SetsStatusToClosedAndRetainsEpicIssueLinks` (`packages/server/tests/Mohist.Server.Tests/Specs/Epic/Domain/EpicLifecycleSpecs.cs:187`) and `SetStatusAsync_Closed_PreservesEpicIssueRows` (`packages/server/tests/Mohist.Server.Tests/Specs/Epic/Grain/EpicMembershipSpecs.cs:343`).
- Terminal epic membership can coexist with a new non-terminal membership: `IX_EpicIssues_ProjectId_IssueId` is now non-unique (`packages/server/src/Mohist.Server/Infrastructure/Data/Db/MohistDbContext.cs:216-229`), terminal target epics do not create active slots (`packages/server/src/Mohist.Server/Epic/Grains/EpicGrain.cs:94-103`), and active conflict detection reads `EpicActiveIssues` (`packages/server/src/Mohist.Server/Epic/Grains/EpicGrain.cs:67-74`). Covered by `LinkIssueAsync_IssueInTerminalEpic_CanLinkToNewNonTerminalEpic_AndKeepsTerminalMembership` (`packages/server/tests/Mohist.Server.Tests/Specs/Epic/Grain/EpicMembershipSpecs.cs:95`).
- Two non-terminal memberships are still rejected: `EpicActiveIssues` is keyed by `(ProjectId, IssueId)` (`packages/server/src/Mohist.Server/Infrastructure/Data/Db/MohistDbContext.cs:232-239`), `LinkIssueAsync` rejects a conflicting active owner before insert and translates a race-time `DbUpdateException` into the existing duplicate-membership error shape (`packages/server/src/Mohist.Server/Epic/Grains/EpicGrain.cs:70-74`, `packages/server/src/Mohist.Server/Epic/Grains/EpicGrain.cs:106-119`). Covered by `LinkIssueAsync_SecondNonTerminalMembership_ThrowsDuplicate`, `LinkIssueAsync_TerminalPlusSecondNonTerminal_ThrowsDuplicate_AndKeepsTerminalRow`, and `ActiveMembershipSlot_PreventsTwoNonTerminalOwners_WhenPrechecksRace` (`packages/server/tests/Mohist.Server.Tests/Specs/Epic/Grain/EpicMembershipSpecs.cs:191`, `packages/server/tests/Mohist.Server.Tests/Specs/Epic/Grain/EpicMembershipSpecs.cs:222`, `packages/server/tests/Mohist.Server.Tests/Specs/Epic/Grain/EpicMembershipSpecs.cs:257`).
- Explicit unlink remains scoped: `UnlinkIssueAsync` removes only the current epic/issue `EpicIssueRow` and releases only the matching active slot (`packages/server/src/Mohist.Server/Epic/Grains/EpicGrain.cs:138-144`, `packages/server/src/Mohist.Server/Epic/Grains/EpicGrain.cs:482-487`). Covered by `UnlinkIssueAsync_RemovesOnlyThatMembership_AndLeavesOthersIntact` and `UnlinkIssueAsync_OnMultiMemberEpic_RemovesOnlyTheSpecifiedMembership` (`packages/server/tests/Mohist.Server.Tests/Specs/Epic/Grain/EpicMembershipSpecs.cs:291`, `packages/server/tests/Mohist.Server.Tests/Specs/Epic/Grain/EpicMembershipSpecs.cs:319`), plus API unlink coverage in `EpicLifecycleSpecs` (`packages/server/tests/Mohist.Server.Tests/Specs/Epic/Domain/EpicLifecycleSpecs.cs:249`).
- Closed epic progress/history remains readable: `EpicQuerier.GetLinkedIssuesAsync` and list progress read retained `EpicIssues` regardless of epic status (`packages/server/src/Mohist.Server/Epic/Services/EpicQuerier.cs:232-286`). Covered by `Close_SetsStatusToClosedAndRetainsEpicIssueLinks` and `ListAsync_IncludesClosedEpicWithRetainedMembers` (`packages/server/tests/Mohist.Server.Tests/Specs/Epic/Domain/EpicLifecycleSpecs.cs:187`, `packages/server/tests/Mohist.Server.Tests/Specs/Epic/Grain/EpicMembershipSpecs.cs:421`).
- `primaryEpic` reflects only non-terminal membership: `IssueQuerier` skips terminal epic owners before assigning `PrimaryEpic` (`packages/server/src/Mohist.Server/Issue/Services/IssueQuerier.cs:1258-1289`). Covered by non-terminal, terminal-only, and re-home scenarios in `IssueQuerierPrimaryEpicSpecs` (`packages/server/tests/Mohist.Server.Tests/Specs/Issue/Querier/IssueQuerierPrimaryEpicSpecs.cs:38`, `packages/server/tests/Mohist.Server.Tests/Specs/Issue/Querier/IssueQuerierPrimaryEpicSpecs.cs:65`, `packages/server/tests/Mohist.Server.Tests/Specs/Issue/Querier/IssueQuerierPrimaryEpicSpecs.cs:124`).
- Migration/data-safety path is covered: the migration drops the old unique index, creates `EpicActiveIssues`, and backfills only non-terminal owners (`packages/server/src/Mohist.Server/Infrastructure/Data/Migrations/20260628022822_DropEpicIssueMembershipUniqueIndex.cs:19-55`). `Migration_BackfillsActiveMembershipSlotsOnlyForNonTerminalOwners` migrates from the pre-issue-179 migration and verifies idle/running/paused slots are created while done/closed rows remain historical-only (`packages/server/tests/Mohist.Server.Tests/Specs/Epic/Domain/EpicActiveIssueMigrationSpecs.cs:17-72`).
- Adjacent retry/recovery routing now follows only the active owner: `EpicQuerier.GetEpicIdForIssueAsync` reads `EpicActiveIssues` and filters terminal epics (`packages/server/src/Mohist.Server/Epic/Services/EpicQuerier.cs:112-129`), and both work-completed and issue-closed handlers have re-home coverage in `EpicAutoDoneHandlerSpecs` (`packages/server/tests/Mohist.Server.Tests/Specs/Events/Epic/EpicAutoDoneHandlerSpecs.cs:46`, `packages/server/tests/Mohist.Server.Tests/Specs/Events/Epic/EpicAutoDoneHandlerSpecs.cs:285`).

## Verification

- `npm test` passed. Captured summaries: server `.NET` tests `Failed: 0, Passed: 2897, Skipped: 14, Total: 2911`; web tests `Test Files 171 passed (171)`, `Tests 2454 passed | 1 skipped (2455)`; runner tests `Test Files 48 passed | 3 skipped (51)`, `Tests 664 passed | 23 skipped (687)`.

<promise>PASS</promise>
