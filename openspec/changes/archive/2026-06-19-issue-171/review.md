# Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: formatting
  Evidence: `packages/web/src/pages/epic-detail/ui/EpicDetailPage.test.tsx` contained trailing blank lines at EOF and extra blank separators around the module mocks in the candidate diff. Removed the unnecessary blank lines without changing test behavior.
  Verification: `npm test -- --run src/pages/epic-detail/ui/EpicDetailPage.test.tsx` from `packages/web` -> 1 file passed, 32 tests passed; `git diff --check` -> no output.
  Status: resolved

## Blocking Items

(none)

## Follow-up Items

(none)

## Pre-existing or Out-of-scope Items

- [ID: item-2]
  Severity: warning
  Scope: dependency audit
  Evidence: The focused server validation invokes the repository build path, which runs the web build and reports `npm audit` findings: 9 vulnerabilities (3 moderate, 3 high, 3 critical). This is not introduced by the reviewed epic-board changes and did not fail the build/test command.
  SuggestedAction: Triage dependency audit findings separately with `npm audit` / package upgrades.
  Status: pre-existing

- [ID: item-3]
  Severity: info
  Scope: build output
  Evidence: The web production build emitted Rollup warnings for `@microsoft/signalr/dist/esm/Utils.js` PURE annotations. These originate in `node_modules`, did not fail the build, and are unrelated to the reviewed change.
  SuggestedAction: Track upstream dependency/build-tool compatibility if the warning becomes noisy or blocks CI.
  Status: pre-existing

## Acceptance Criteria Evidence

- AC1 priority and updatedAt ordering: `packages/server/src/Mohist.Server/Epic/Services/EpicQuerier.cs` orders list rows by `PriorityRank(e.Priority)` then `UpdatedAt` descending, covered by `EpicList_OrdersByPriorityWithinStatusGroup`, `EpicList_OrdersByRecentUpdatedAtWhenPrioritiesMatch`, and `EpicList_ReturnsOrderedArraySoConsumerCanRenderInServerSuppliedOrder` in `packages/server/tests/Mohist.Server.Tests/Specs/Epic/Api/EpicApiSpecs.cs`.
- AC2 Done/Closed collapse: `packages/web/src/pages/epics/ui/EpicListPage.tsx` renders Active with `defaultExpanded={true}` and Done/Closed with `defaultExpanded={false}`, covered by `EpicListPage group collapse` tests.
- AC3 current activity real issue listing: `packages/server/src/Mohist.Server/Epic/Services/EpicProgress.cs` derives active/blocked sets from `Health`; `packages/web/src/pages/epic-detail/ui/EpicDetailPage.tsx` lists concrete entries with health and links. Covered by `ActiveAndBlockedIssues_AreDerivedFromHealthNotStatus` and `EpicDetailPage current activity listing` tests.
- AC4 startable `nextIssue` and reason fallback: `EpicProgress.SelectStartableNext` filters by `CanStart && StartBlocker is null` and priority rank; `BuildNextIssueReason` reports blocker-derived text. Covered by `NextIssue_PrefersHighestPriorityStartableIssue`, `NextIssue_IgnoresNonStartableIssuesEvenWhenInsertedFirst`, and `NextIssue_IsNullAndReasonPopulated_WhenNoIssueStartable`.
- AC5 card in-progress plus next display: `EpicListPage.tsx` renders `epic-card-in-progress` and `epic-card-next` independently for active cards, covered by `EpicListPage in-progress and next display` tests.
- AC6 Markdown description: `EpicDetailPage.tsx` uses `MarkdownReader` for epic descriptions, covered by `EpicDetailPage markdown description` tests.
- AC7 status-conditional card text: `EpicListPage.tsx` branches Done to `Completed`, Closed to `Closed`, and only Active to `Ready to mark done`, covered by `EpicListPage status-conditional card text` tests.
- AC8 mark-done no regression: `EpicProgress.Build` keeps `ReadyToMarkDone` as delivered-count-only, covered by `ReadyToMarkDone_*` and `GrainPath_ReadyToMarkDone_*` tests plus integration lifecycle tests.

## Verification

- `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter "FullyQualifiedName~Epic"` -> passed, 42 tests.
- `npm test -- --run src/pages/epics/ui/EpicListPage.test.tsx src/pages/epic-detail/ui/EpicDetailPage.test.tsx` from `packages/web` -> passed, 2 files, 48 tests.
- `npm test -- --run src/pages/epic-detail/ui/EpicDetailPage.test.tsx` from `packages/web` after repair -> passed, 1 file, 32 tests.
- `git diff --check` -> no output.

<promise>PASS</promise>
