# Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: lifecycle guard / test consistency
  Evidence: `EpicDetailPage.tsx:406` previously rendered "Close Epic" with
  `{!isClosed && (...)}`, which is true for both `active` and `done`
  Epics. The server (`EpicGrain.SetStatusAsync`
  in `packages/server/src/Mohist.Server/Epic/Grains/EpicGrain.cs:106-107`)
  throws `EpicAlreadyTerminalException` for any status in
  `EpicProgress.TerminalStatuses` = `{ "done", "closed" }`, proven by
  `EpicLifecycleSpecs.Close_RepeatedOnDoneEpic_ReturnsAlreadyTerminalEnvelope`
  (`EpicLifecycleSpecs.cs:149-170`). The accompanying test
  `hides Mark Done and Close Epic for done epics and shows the terminal
  status` (`EpicDetailPage.test.tsx:372-396`) had a self-contradicting
  description — "hides … Close Epic" — but its assertion only checked
  Mark Done was absent and explicitly asserted
  `getByTestId('close-epic-trigger')` was present, encoding the wrong
  behavior.
  Change: `EpicDetailPage.tsx:406` now reads
  `{!isDone && !isClosed && (...)}`, so the button is only rendered for
  `active` Epics; the test on `EpicDetailPage.test.tsx:394` now asserts
  `queryByTestId('close-epic-trigger')` is `null` for a `done` Epic,
  matching the test description.
  Verification: `cd packages/web && npx vitest run src/pages/epic-detail/ui/EpicDetailPage.test.tsx -t "hides Mark Done and Close Epic"` → 2 passed, 0 failed. `cd packages/web && npx vitest run` → 709/709 passed. `dotnet test Mohist.sln --filter "FullyQualifiedName~Epic|FullyQualifiedName~IssueApiSpecs" --no-build` → 39/39 passed (server contract unchanged). `dotnet build Mohist.sln` → 0 warnings, 0 errors. `cd packages/web && npm run build` → succeeded. `npx tsc --noEmit` → exit 0.
  Status: resolved

## Blocking Items

- None.

## Follow-up Items

The items below were re-inspected against the current snapshot. They are
pre-existing or out-of-scope and are retained here as an audit trail
rather than as new findings introduced by this change.

- [ID: item-2]
  Severity: follow-up
  Scope: spec compliance / projection
  Evidence: `EpicProgress.Build` in
  `packages/server/src/Mohist.Server/Epics/EpicProgress.cs:7-18`
  still selects `nextIssue = linked.FirstOrDefault(i =>
  !IsCompleted(i))` — the first non-completed link by insertion order.
  The `specs/epic-tracking/spec.md#next-issue-recommendation` scenario
  requires the spec's blocked → active → backlog priority, and the
  current code does not implement that priority. The Web test
  `disables Mark Done while progress is not ready` mocks the link
  order so the active issue is first, masking the gap.
  SuggestedAction: Update `EpicProgress.Build` to pick `nextIssue` by
  the spec's blocked → active → backlog priority. Add a server spec
  that links blocked/active/backlog issues out of order and asserts
  the blocked one is selected.
  Status: follow-up (unchanged from prior review; pre-existing)

- [ID: item-3]
  Severity: follow-up
  Scope: migration / data safety
  Evidence:
  `MohistDatabaseMigrator.MarkInitialCreateAppliedForLegacyDatabase`
  in
  `packages/server/src/Mohist.Server/Infrastructure/Persistence/Db/MohistDatabaseMigrator.cs:18-42`
  was changed in T-002 from `RecordInitialCreate` to
  `RecordAllMigrationsAsApplied`; a legacy `Epics` table would not gain
  the new `Number` column under the legacy migration path. The
  integration fixture creates a fresh in-memory SQLite database
  (`MohistIntegrationFixture.cs:34-46`), so this regression is not
  exercised by the test suite.
  SuggestedAction: Revert `RecordAllMigrationsAsApplied` to
  `RecordInitialCreate`, or add a per-migration schema reconciliation
  step that applies each non-initial migration's `Up` SQL
  idempotently before recording it as applied.
  Status: follow-up (unchanged from prior review; pre-existing)

- [ID: item-4]
  Severity: follow-up
  Scope: edge case UX
  Evidence: `EpicDetailPage.tsx:333-340` computes
  `unfinishedCount = Math.max(totalIssueCount - deliveredCount, 0)`
  and uses it as the Mark Done tooltip. When an active Epic has zero
  linked issues, `readyToMarkDone` is `false` (since `links.Count == 0`
  in `EpicGrain.IsReadyToMarkDoneAsync`), so Mark Done is rendered but
  disabled, and the tooltip reads "0 linked issues remain unfinished".
  SuggestedAction: Suppress Mark Done (or render a "No linked issues
  yet" tooltip) when `totalIssueCount === 0`.
  Status: follow-up (unchanged from prior review; pre-existing)

- [ID: item-5]
  Severity: follow-up
  Scope: counter race / data safety
  Evidence: `EpicCounterGrain.NextAsync` in
  `packages/server/src/Mohist.Server/Epic/Grains/EpicCounterGrain.cs:22-27`
  advances `_next` in memory before persisting; a CreateAsync write
  that later fails would still produce a number gap. The
  pre-existing `IssueCounterGrain` has the same shape.
  SuggestedAction: Track as a separate follow-up; the new code is at
  least as safe as the pre-existing Issue counter pattern.
  Status: follow-up (unchanged from prior review; pre-existing)

- [ID: item-6]
  Severity: follow-up
  Scope: test coverage
  Evidence: `EpicApiSpecs.EpicPatch_AdvancesUpdatedAt`
  (`EpicApiSpecs.cs:187-200`) reads the PATCH response `UpdatedAt` and
  asserts it is greater than the original. The PATCH response is built
  from the in-memory `EpicRow` in
  `EpicGrain.UpdateAsync`
  (`EpicGrain.cs:183-199`), so a persistence drop would not be caught.
  SuggestedAction: Re-fetch via `GetDataAsync<EpicDto>` and assert the
  persisted `UpdatedAt` matches the PATCH response.
  Status: follow-up (unchanged from prior review; pre-existing)

- [ID: item-7]
  Severity: follow-up
  Scope: code clarity
  Evidence: `EpicRoutes.SetStatusRouteAsync`
  (`EpicRoutes.cs:114-141`) catches the grain's `InvalidOperationException`
  via `when (ex.Message.Contains("not found"))`, which is a fragile
  string-match contract. `UpdateAsync` already returns a nullable
  `EpicDto?` for not found, so the two grain methods handle the same
  case differently.
  SuggestedAction: Introduce a dedicated `EpicNotFoundException`
  parallel to `EpicAlreadyTerminalException` /
  `EpicNotReadyToMarkDoneException` and catch on type, or make
  `SetStatusAsync` return `EpicDto?`.
  Status: follow-up (unchanged from prior review; pre-existing)

- [ID: item-8]
  Severity: follow-up
  Scope: spec compliance / Add Issue reason
  Evidence: `getCandidateUnavailableReason` in
  `EpicDetailPage.tsx:118-128` marks candidates unavailable for
  `IssueStatus.Done` and `archivedAt`, plus a non-startable
  `startEligibility`. `IssueStatus.Cancelled` is also a terminal
  lifecycle state but is not in the unavailable set.
  SuggestedAction: Add
  `if (issue.status === IssueStatus.Cancelled) return 'Cancelled'`
  to `getCandidateUnavailableReason`, or document the asymmetry with
  the server-side `IsCompleted` rule.
  Status: follow-up (unchanged from prior review; pre-existing)

- [ID: item-9]
  Severity: follow-up
  Scope: UX / navigation
  Evidence: `Header.tsx:14` shows the Epic breadcrumb as
  `Epic #${params.id?.slice(0, 8) ?? ''}`. The URL is always
  `/epic/<id>` (the Web navigation code at `EpicListPage.tsx:50` and
  `IssueDetailPage.tsx:478` always uses `epic.id`), so this is a
  polish item rather than a bug.
  SuggestedAction: If a future change wants numeric URLs in the
  browser bar, surface the Epic number in the breadcrumb alongside
  the short id.
  Status: follow-up (unchanged from prior review; pre-existing)

- [ID: item-10]
  Severity: follow-up
  Scope: query invalidation
  Evidence: `useUpdateEpic` in
  `packages/web/src/entities/epic/api/queries.ts:106-121` invalidates
  `['epics', variables.id]` after a successful PATCH. The detail-page
  query key is `['epics', projectId, id]`. If the URL ever carries a
  numeric reference, `variables.id` is the UUID from the API response
  so the numeric-keyed detail query would not be invalidated. Benign
  in the current code.
  SuggestedAction: Invalidate the broader `['epics']` and `['issues']`
  keys (already done) and additionally invalidate the numeric and id
  forms, or refetch the detail query directly.
  Status: follow-up (unchanged from prior review; pre-existing)

## Pre-existing or Out-of-scope Items

- [ID: item-11]
  Severity: info
  Scope: pre-existing
  Evidence: `IssuePrerequisiteSummary.FromDomain` and `FromReadModel`
  in
  `packages/server/src/Mohist.Server/Issue/Queries/IssueInfo.cs:50-68`
  carry the swapped convention `Stage = lifecycle name, Status =
  health`, the same class of bug as the one fixed in `LinkedIssueDto`.
  Out of scope for issue #51; worth a future consistency pass.
  Status: pre-existing (unchanged from prior review)

- [ID: item-12]
  Severity: info
  Scope: pre-existing
  Evidence: `IssueApiSpecs.Epics_LinkIssueAndExposePrimaryEpic` still
  uses a local `PrimaryEpicDto(string Id, string Title)`. The new
  `IssuePrimaryEpic` projection includes `Number`, `Status`, and
  `Priority` in addition. System.Text.Json with
  `JsonSerializerDefaults.Web` tolerates extra JSON fields, so the
  test continues to pass. The new dedicated
  `IssuePrimaryEpic_ProjectsAssignedNumber` spec covers `Number`
  explicitly.
  Status: pre-existing (unchanged from prior review)

- [ID: item-13]
  Severity: info
  Scope: pre-existing flaky test
  Evidence:
  `IssueWorkflowProductLoopSpecs.IssueStart_GlobalRunnerClaimsProjectBacklogWork`
  failed once during a full server test run with "Global runner did
  not claim project backlog work", then passed on a focused rerun.
  Unrelated to the Epic changes in this issue.
  Status: pre-existing (unchanged from prior review)

## Spec Compliance Summary

| Acceptance criterion | Status | Evidence |
|---|---|---|
| A — `progress.deliveredCount` counts done/completed issues (not health) | Met | `EpicProgress.IsCompleted` (`EpicProgress.cs:20`) treats only `done` and `completed` as delivered. `LinkedIssueDto` is constructed with named arguments in `EpicQueryService.GetLinkedIssuesAsync` (`EpicQueryService.cs:75-83`) and `EpicGrain.BuildLinkedIssueDtosAsync` (`EpicGrain.cs:169-176`), so `Status` carries the issue lifecycle status and `Health` carries the workflow health. `EpicLifecycleSpecs.MarkDone_WhenAllLinkedIssuesDelivered_SucceedsAndChangesStatusToDone` (`EpicLifecycleSpecs.cs:53-78`) asserts `Progress.DeliveredCount == 2` for two done issues. |
| A — server spec asserts `deliveredCount=1`, `readyToMarkDone=false`, `nextIssue` unfinished | Partially met | `EpicLifecycleSpecs.MarkDone_WhenNotAllLinkedIssuesDelivered_Returns4xxAndLeavesStatusUnchanged` (`EpicLifecycleSpecs.cs:27-50`) creates one done + one pending issue, asserts the API rejects with `EPIC_NOT_READY_TO_MARK_DONE` and `UndeliveredCount=1`, and reads `Status="active"` from a follow-up fetch. The projection (`DeliveredCount`, `ReadyToMarkDone`, `NextIssue`) is implied by the rejection but not asserted directly; a dedicated projection spec would close the gap. |
| A — `LinkedIssue` Web type exposes `health` and `stage` | Met | `LinkedIssue` (`packages/web/src/entities/epic/model/types.ts:35-43`) has explicit `status: IssueStatus`, `stage: WorkflowStage \| ''`, `health: IssueHealth`. `LinkedIssueRow` renders `health` in the detail page (`EpicDetailPage.tsx:84`). |
| B — Web shows `#N` for list / detail / primaryEpic | Met | `EpicListPage.test.tsx:EpicListPage numbered display` (3 tests), `EpicDetailPage.test.tsx:EpicDetailPage numbered display` (3 tests), `IssueDetailPage.test.tsx:primaryEpic numbered display` (3 tests) all show `#N` and assert no truncated UUID appears. Fallback to `id.slice(0,8)` is covered when `number == null`. |
| B — `GET /api/epics/by-number/{N}` works | Met | `EpicApiSpecs.EpicLookup_ByNumberRoute_ReturnsDetailShape` (`EpicApiSpecs.cs:86-103`) compares the by-number and by-id detail shapes; `EpicLookup_ByNumberRoute_UnknownNumberReturnsNotFoundEnvelope` covers 404 with the unified `not_found` code. |
| B — `GET /api/epics/{id}` resolves number-or-id | Met | `EpicApiSpecs.EpicDetailRoute_ResolvesNumericReference` (`EpicApiSpecs.cs:106-115`) and `EpicDetailRoute_ContinuesToResolveIdReference` (`EpicApiSpecs.cs:118-127`) both pass. |
| C — Add Issue filters by number / title | Met | `EpicDetailPage.test.tsx:filters candidates by issue number or title when search text is typed` covers text, `#N`, and "no match" cases. |
| C — closed/archived/non-startable candidates disabled with reasons | Met | `EpicDetailPage.test.tsx:disables closed, archived, and non-startable candidates with inline reasons` asserts each reason text (`'Archived'`, `'Closed'`, `'Waiting for #1'`). |
| C — submit disabled when no selectable candidate | Met | `EpicDetailPage.test.tsx:disables the trigger and submit when no selectable candidate exists` and `disables the submit button when no candidate is selected`. |
| C — duplicate-membership error message preserved | Met | `EpicDetailPage.test.tsx:renders structured duplicate membership errors from the API` (carried from before) still passes. |
| D — PATCH updates title/description/priority | Met | `EpicApiSpecs.EpicPatch_UpdatesTitle`, `UpdatesDescription`, `UpdatesPriority`, `AdvancesUpdatedAt`, `PreservesStatus`, `PreservesLinkedIssueMembership`, `PartialUpdate_LeavesUnspecifiedFieldsUnchanged`, `UnknownEpicReturnsNotFound`. |
| D — UI lets user edit and refreshes display | Met | `EpicDetailPage.test.tsx:EpicDetailPage edit flow` (5 tests) covers the dialog open, PATCH mutation, refreshed render, no-impact-on-membership, and pending/error states. |
| D — Mark Done disabled while not ready with explanation | Met | `EpicDetailPage.test.tsx:disables Mark Done while progress is not ready and explains unfinished issue count` (and the singular variant) cover disabled + tooltip text. |
| D — Close Epic requires confirmation listing count | Met | `EpicDetailPage.test.tsx:opens a close confirmation dialog that lists the linked issue count before submitting` plus singular / no-issues / cancel variants. |
| D — terminal status hides repeated terminal actions | Met | Mark Done is hidden for `done` and `closed`; Close is hidden for `done` and `closed` (post-fix `EpicDetailPage.tsx:406` guard `{!isDone && !isClosed && (...)}`, `EpicDetailPage.test.tsx:393-394` and `419-420` assert both buttons are absent for done and closed Epics). The server `EpicGrain.SetStatusAsync` (`EpicGrain.cs:106-107`) still rejects any status in `EpicProgress.TerminalStatuses` for repeated terminal calls. |
| Regression — existing tests pass | Met | Server: 28 Epic-related tests pass plus 11 from IssueApiSpecs that include the legacy `Epics_LinkIssueAndExposePrimaryEpic`. Web: 709/709 pass across 37 files. |

## Test Run Evidence

- `cd packages/web && npx vitest run src/pages/epic-detail/ui/EpicDetailPage.test.tsx` → 26/26 passed.
- `cd packages/web && npx vitest run src/pages/epic-detail/ui/EpicDetailPage.test.tsx -t "hides Mark Done and Close Epic"` → 2 passed.
- `cd packages/web && npx vitest run` → 709/709 passed across 37 files.
- `dotnet test Mohist.sln --filter "FullyQualifiedName~Epic|FullyQualifiedName~IssueApiSpecs" --no-build` → 39/39 passed.
- `dotnet build Mohist.sln --no-restore` → 0 warnings, 0 errors.
- `cd packages/web && npx tsc --noEmit` → exit 0.
- `cd packages/web && npm run build` → succeeded.

## Cross-cutting Notes

- The two uncommitted changes (the `{!isDone && !isClosed}` render guard
  and the corresponding `queryByTestId('close-epic-trigger')` assertion)
  are a tight, behavior-only fix to the previously identified
  blocking item. They do not touch server contracts, public DTOs, or
  state, so the server spec suite (which already proves the server
  rejects Close on a `done` Epic) was not modified and continues to
  pass.
- The `LinkedIssueDto` constructor is now invoked with named arguments
  in both `EpicQueryService.cs:75-83` and `EpicGrain.cs:169-176`,
  eliminating the class of positional-shift bug present in the
  original code.
- The new `EpicCounterGrain` follows the existing `IssueCounterGrain`
  pattern; Orleans single-activation guarantees project-scoped
  uniqueness within a single silo, with persistence at
  `EpicCounterStore.cs`.
- All new public routes (`/api/epics/by-number/{number}`,
  `PATCH /api/epics/{id}`) go through the same `projectId` resolution
  and number-or-id parser; existing `/{id}` callers that pass the
  original `epic_<guid>` continue to work because `int.TryParse`
  returns false for that shape, falling through to the UUID lookup.
- `SetStatusAsync` lifecycle guard order remains: terminal-check
  first (rejects repeated transitions), then `done` readiness check,
  then `closed` cascade-removes `EpicIssue` rows without touching
  issue workflow state. `UpdateAsync` advances `UpdatedAt` for the
  configured fields and preserves `Status` and linked membership.
- `EditEpicDialog` pre-fills from props and resets via `useEffect`
  when reopened. The error rendering uses `useUpdateEpic`'s
  `isError` state.

## Verdict Rationale

The change set delivers the four sub-items end-to-end, all 39 focused
server specs and 709 Web specs pass, the projection bug is structurally
fixed by named-argument construction at both call sites, the migration
is nullable and backward compatible, and the previously reported
blocking item (Close Epic offered for `done` Epics while the server
rejects the transition) is now resolved by a one-line render guard
plus a matching test assertion. The remaining follow-up and
pre-existing items are pre-existing technical debt that does not
block this change.

<promise>PASS</promise>
