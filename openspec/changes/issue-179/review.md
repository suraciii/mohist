# Review Report

## Result: FAIL

## Repaired Items

_None._

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Epic/Services/EpicQuerier.cs` and `packages/server/src/Mohist.Server/Events/Subscriptions/EpicAutoDoneHandler.cs`
  Evidence: `EpicQuerier.GetEpicIdForIssueAsync` still selects the first `EpicIssues` row for an issue without joining `Epics` or filtering terminal owners (`EpicQuerier.cs:111-118`). After this change, a re-homed issue can legitimately have both a retained terminal membership and a new non-terminal membership. The terminal-event recovery path calls this helper (`EpicAutoDoneHandler.cs:108-117`) to decide which epic grain receives `ReconcileAfterTerminalAsync`. In the new retained-link state, it can dispatch work-completed / issue-closed events to the closed/done epic instead of the active/running epic, so the active epic may not auto-mark done or advance its next issue. This violates the issue's adjacent retry/recovery-path requirement and the `primaryEpic` rule's broader invariant that active ownership is the non-terminal membership. Repair considered but disallowed: this changes product behavior and event-routing semantics, not a local cleanup.
  SuggestedAction: Change `GetEpicIdForIssueAsync` or add a dedicated recovery lookup so terminal-event reconciliation selects the issue's non-terminal epic membership, preferably with a deterministic query joining `EpicIssues` to `Epics` and excluding `done`/`closed`. Add a re-home regression test where an issue linked to a closed/done epic and a running epic emits `IssueWorkCompleted` or `IssueClosed`, and assert the dispatched grain key is the running epic.
  Verification: `npm test` currently passes, but there is no re-home coverage for `GetEpicIdForIssueAsync` or the terminal-event handlers. Existing event tests only seed a single membership per issue (`EpicAutoDoneHandlerSpecs.cs:25-44`, `238-259`) and therefore do not exercise the new multi-membership state.
  Status: open

- [ID: item-2]
  Severity: warning
  Scope: `packages/server/src/Mohist.Server/Epic/Grains/EpicGrain.cs` and `packages/server/src/Mohist.Server/Infrastructure/Data/Db/MohistDbContext.cs`
  Evidence: The unique database index on `(ProjectId, IssueId)` is relaxed to non-unique (`MohistDbContext.cs:215-228`, migration `20260628022822_DropEpicIssueMembershipUniqueIndex.cs:22-32`), while the new "at most one non-terminal epic" rule is enforced only by the pre-insert query in `EpicGrain.LinkIssueAsync` (`EpicGrain.cs:79-116`). Since Orleans serializes per epic grain, not per issue, two different epic grains can both pass the read-side conflict check before either insert is visible, then both write non-terminal memberships. That leaves the system in a state the acceptance criteria forbids: one issue in two non-terminal epics. The design acknowledges this as a risk, but the post-build candidate still has no hard guard or cleanup path. Repair considered but disallowed: fixing this requires a public data-safety decision, such as a denormalized active-membership slot, partial unique index strategy, issue-scoped coordinator grain, or retry/reconciliation policy.
  SuggestedAction: Add a hard concurrency guard for non-terminal ownership or explicitly narrow and document the acceptance criterion if eventual duplicate repair is acceptable. Add a regression test that simulates two concurrent link attempts into different non-terminal epics for the same issue and verifies only one non-terminal row can commit.
  Verification: Existing tests cover sequential duplicate rejection (`EpicMembershipSpecs.cs:191-217`, `222-252`) but not concurrent cross-epic insertion after the index relaxation. `npm test` passes with this gap.
  Status: open

## Follow-up Items

- [ID: item-3]
  Severity: follow-up
  Scope: `openspec/changes/issue-179/tasks.json`
  Evidence: Both T-001 and T-002 still have `passes: false` (`tasks.json:24`, `tasks.json:43`) even though the candidate includes implementation commits and `npm test` is green. This is not a product deliverable defect, but it weakens traceability for integrate/check readers trying to determine task completion from the artifact.
  SuggestedAction: If `tasks.json` is intended as completion evidence, update those task pass flags before integration or clarify that completion is tracked outside this artifact.
  Status: follow-up

## Pre-existing or Out-of-scope Items

_None identified._

## Acceptance Criteria Evidence

- Close no longer unlinks issues: `EpicGrain.ApplyPendingEvents` is now a no-op drain (`EpicGrain.cs:545-554`), and close persists only status changes through `SetStatusAsync` (`EpicGrain.cs:239-273`). Covered by `Close_SetsStatusToClosedAndRetainsEpicIssueLinks` (`EpicLifecycleSpecs.cs:187-212`) and `SetStatusAsync_Closed_PreservesEpicIssueRows` (`EpicMembershipSpecs.cs:309-338`).
- Terminal epic membership can coexist with a new non-terminal membership sequentially: the duplicate query excludes `done`/`closed` owners (`EpicGrain.cs:79-88`), and the EF index is non-unique (`MohistDbContext.cs:221-228`). Covered by `LinkIssueAsync_IssueInTerminalEpic_CanLinkToNewNonTerminalEpic_AndKeepsTerminalMembership` (`EpicMembershipSpecs.cs:95-126`).
- Sequential second non-terminal membership still rejects: `EpicGrain.cs:90-94` throws the existing message mapped to `DUPLICATE_EPIC_MEMBERSHIP` in `EpicRoutes.cs:76-80`. Covered by `EpicMembershipSpecs.cs:191-217` and `222-252`.
- Explicit unlink remains scoped to one membership: `UnlinkIssueAsync` removes only the current epic/issue row (`EpicGrain.cs:119-140`). Covered by `EpicMembershipSpecs.cs:257-304` and the API-level unlink specs in `EpicLifecycleSpecs.cs:246-289`.
- Closed epic progress/history remains readable: `EpicQuerier.GetLinkedIssuesAsync` still reads `EpicIssues` independent of epic status (`EpicQuerier.cs:215-254`). Covered by `EpicLifecycleSpecs.cs:204-212` and `EpicMembershipSpecs.cs:366-388`.
- `primaryEpic` projection skips terminal memberships: `IssueQuerier` filters `EpicProgress.IsTerminal(epic.Status)` before assigning `PrimaryEpic` (`IssueQuerier.cs:1258-1289`). Covered by `IssueQuerierPrimaryEpicSpecs.cs:38-220`.

## Verification

- `npm test` passed. Evidence from the captured output: server `.NET` tests reported `Failed: 0, Passed: 2892, Skipped: 14, Total: 2906`; web workspace reported `Test Files 171 passed`, `Tests 2454 passed | 1 skipped`; runner workspace reported `Test Files 48 passed | 3 skipped`, `Tests 664 passed | 23 skipped`.

<promise>FAIL</promise>
