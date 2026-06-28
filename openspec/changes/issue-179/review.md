# Review Report

## Result: FAIL

## Repaired Items

_None._

## Blocking Items

- [ID: item-1]
  Severity: test-gap
  Scope: `packages/server/src/Mohist.Server/Infrastructure/Data/Migrations/20260628022822_DropEpicIssueMembershipUniqueIndex.cs`
  Evidence: The candidate adds `EpicActiveIssues` as the database-enforced active-membership slot (`MohistDbContext.cs:232-239`) and backfills it from existing non-terminal `EpicIssues` rows during migration (`20260628022822_DropEpicIssueMembershipUniqueIndex.cs:48-55`). That backfill is now part of the data-safety invariant: without it, already-linked non-terminal epics upgraded from an existing database would have no active slot, so `LinkIssueAsync` would not see their ownership in `GetActiveMembershipOwnerAsync` (`EpicGrain.cs:63-74`, `454-472`) and could allow a second non-terminal membership. The new tests exercise fresh migrated schemas and runtime writes, but I did not find a test that applies this migration to a pre-existing database containing `EpicIssues` rows and asserts `EpicActiveIssues` is populated for `idle`/`running`/`paused` owners and not for `done`/`closed` owners. Repair considered but disallowed: adding a migration/backfill regression test for the upgrade path is not a formatting or obvious local repair; it changes coverage around a data-safety behavior.
  SuggestedAction: Add a migration test that creates a database at the pre-issue-179 model or otherwise seeds the old schema with active and terminal epic memberships, runs the migration, and verifies `EpicActiveIssues` contains exactly the non-terminal owners while terminal history remains only in `EpicIssues`.
  Verification: `npm test` passed, but the inspected tests seed link rows after `Database.Migrate()` (`EpicMembershipSpecs.cs:519-529`, `IssueQuerierPrimaryEpicSpecs.cs:222-289`, `EpicAutoDoneHandlerSpecs.cs:550-574`) or test the slot table directly, so they do not exercise upgrade-time backfill from an existing `EpicIssues` table.
  Status: open

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: `openspec/changes/issue-179/design.md`, `openspec/changes/issue-179/proposal.md`, `openspec/changes/issue-179/tasks.json`
  Evidence: The final implementation introduces the `EpicActiveIssues` active-slot table and uses it for duplicate checks/reconcile routing, but the design artifact still describes D2 as relaxing the unique index and enforcing the invariant "in application code only" with a known weakened concurrency guarantee (`design.md:36-40`, `design.md:64-67`). The proposal/tasks mention relaxing `IX_EpicIssues_ProjectId_IssueId` but do not describe the added active-slot table. This is not a product deliverable defect, but it is a traceability/spec-sync risk for integrate readers because the candidate no longer matches the documented decision trade-off.
  SuggestedAction: Update the workflow artifacts before integration to describe the active-slot table, its migration backfill, and the fact that the DB still hard-enforces one non-terminal owner per issue.
  Status: follow-up

## Pre-existing or Out-of-scope Items

_None identified._

## Acceptance Criteria Evidence

- Close no longer unlinks issues: `EpicGrain.ApplyPendingEvents` now only drains domain events (`EpicGrain.cs:595-604`), while `SetStatusAsync("closed")` releases only active slots and preserves `EpicIssues` (`EpicGrain.cs:269-280`). Covered by `Close_SetsStatusToClosedAndRetainsEpicIssueLinks` and `SetStatusAsync_Closed_PreservesEpicIssueRows`.
- Terminal epic membership can coexist with a new non-terminal membership: `IX_EpicIssues_ProjectId_IssueId` is non-unique (`MohistDbContext.cs:216-229`), terminal owners do not create active slots (`EpicGrain.cs:94-103`), and duplicate checks consult `EpicActiveIssues` (`EpicGrain.cs:67-74`). Covered by `LinkIssueAsync_IssueInTerminalEpic_CanLinkToNewNonTerminalEpic_AndKeepsTerminalMembership`.
- Two non-terminal memberships are rejected: `EpicActiveIssues` has primary key `(ProjectId, IssueId)` (`MohistDbContext.cs:232-239`), and `LinkIssueAsync` maps active-owner conflicts to the existing duplicate message (`EpicGrain.cs:70-74`, `106-119`). Covered by sequential duplicate specs and `ActiveMembershipSlot_PreventsTwoNonTerminalOwners_WhenPrechecksRace`.
- Explicit unlink remains scoped: `UnlinkIssueAsync` removes only the current epic/issue row and its active slot (`EpicGrain.cs:138-144`, `482-487`). Covered by `UnlinkIssueAsync_RemovesOnlyThatMembership_AndLeavesOthersIntact` and API unlink specs.
- Closed epic progress/history remains readable: `EpicQuerier.GetLinkedIssuesAsync` reads retained `EpicIssues` regardless of epic status (`EpicQuerier.cs:232-264`). Covered by close/detail/list specs.
- `primaryEpic` reflects only non-terminal membership: `IssueQuerier` skips terminal epic owners before assigning `PrimaryEpic` (`IssueQuerier.cs:1258-1289`). Covered by `IssueQuerierPrimaryEpicSpecs.cs` scenarios for non-terminal, terminal-only, and re-homed memberships.
- Adjacent recovery routing now follows the active owner: `EpicQuerier.GetEpicIdForIssueAsync` reads `EpicActiveIssues` and filters terminal epic rows (`EpicQuerier.cs:112-129`), with work-completed and issue-closed re-home tests in `EpicAutoDoneHandlerSpecs.cs`.

## Verification

- `npm test` passed. Captured summary: server `.NET` tests `Failed: 0, Passed: 2896, Skipped: 14, Total: 2910`; web tests `171 passed`, `2454 passed | 1 skipped`; runner tests `48 passed | 3 skipped`, `664 passed | 23 skipped`.

<promise>FAIL</promise>
