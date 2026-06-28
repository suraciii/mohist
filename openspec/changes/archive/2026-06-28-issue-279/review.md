# Review Report

## Result: PASS

## Repaired Items

_None._

## Blocking Items

_None._

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: server/list read-model contract
  Evidence: The reviewed implementation intentionally classifies `Ready to start` from `progress.nextIssue != null` using only the list read model (`packages/web/src/pages/epics/ui/groupActiveEpics.ts:20`). That matches the issue non-goal of avoiding backend/API changes and the design decision that `LinkedIssue.canStart` is only available on the detail path (`packages/web/src/entities/epic/model/types.ts:43`, `packages/web/src/entities/epic/model/types.ts:72`). The remaining assumption is that the server never emits a non-null `nextIssue` for a non-startable issue.
  SuggestedAction: If next-issue selection rules change later, add a server-side contract test that non-null list `nextIssue` means startable; keep it out of this frontend-only change.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-2]
  Severity: info
  Scope: packages/web Vitest config
  Evidence: `npm run test:run -w packages/web` passes, but Vitest prints a deprecation warning: `test.poolOptions` was removed in Vitest 4. This is outside the reviewed Epic list change and does not affect the candidate behavior.
  SuggestedAction: Migrate the web Vitest config away from `test.poolOptions` in a separate cleanup.
  Status: pre-existing

## Acceptance Criteria Evidence

- Running epics are independent and prioritized: `groupActiveEpics` assigns `progress.activeIssues.length > 0` to `running` before checking next issue or reason (`packages/web/src/pages/epics/ui/groupActiveEpics.ts:18`), and `activeSections` renders `Running`, `Ready to start`, `Waiting / Blocked`, `Idle / Empty` in that order (`packages/web/src/pages/epics/ui/EpicListPage.tsx:364`). Covered by tests in `packages/web/src/pages/epics/ui/EpicListPage.test.tsx:222` and `packages/web/src/pages/epics/ui/groupActiveEpics.test.ts:178`.
- Ready-to-start, waiting/blocked, and idle-empty are distinguishable: the selector cascade separates `nextIssue`, `nextIssueReason`, and fallback idle/empty buckets (`packages/web/src/pages/epics/ui/groupActiveEpics.ts:20`), and card bodies render next issue, waiting reason, `Ready to mark done`, or `No linked issues` per group (`packages/web/src/pages/epics/ui/EpicListPage.tsx:161`, `packages/web/src/pages/epics/ui/EpicListPage.tsx:184`, `packages/web/src/pages/epics/ui/EpicListPage.tsx:188`). Covered by `EpicListPage.test.tsx:362`, `EpicListPage.test.tsx:390`, `EpicListPage.test.tsx:398`, and `EpicListPage.test.tsx:407`.
- Start action semantics are clarified: the per-card control is labelled `Start next issue` (`packages/web/src/pages/epics/ui/EpicListPage.tsx:117`), is only passed into ready-to-start active cards (`packages/web/src/pages/epics/ui/EpicListPage.tsx:280`), and invokes `useStartIssue` rather than an epic lifecycle action (`packages/web/src/pages/epics/ui/EpicListPage.tsx:371`; hook mutation calls issue `startIssue` in `packages/web/src/entities/epic/api/queries.ts:75`). Covered by `EpicListPage.test.tsx:371`, `EpicListPage.test.tsx:457`, and `EpicListPage.test.tsx:515`.
- Mobile readability and no horizontal overflow are covered in product code and browser tests: card title/current/next/reason text uses wrapping classes (`packages/web/src/pages/epics/ui/EpicListPage.tsx:149`, `packages/web/src/pages/epics/ui/EpicListPage.tsx:167`, `packages/web/src/pages/epics/ui/EpicListPage.tsx:249`, `packages/web/src/pages/epics/ui/EpicListPage.tsx:276`), and the Playwright spec checks 320/390/430 px real browser overflow plus state text bounds (`packages/web/tests/e2e/epic-list-mobile-overflow.spec.ts:160`). Targeted E2E passed.
- Done/Closed folded behavior is preserved: Done and Closed sections pass `defaultExpanded={false}` (`packages/web/src/pages/epics/ui/EpicListPage.tsx:446`, `packages/web/src/pages/epics/ui/EpicListPage.tsx:458`). Covered by `EpicListPage.test.tsx:276`.
- Regression coverage is meaningful: selector unit tests cover all buckets, precedence, partitioning, order, and non-mutation (`packages/web/src/pages/epics/ui/groupActiveEpics.test.ts:119`); page tests cover grouping, card content, action semantics, folded terminal sections, paused adjacency, numbering, and jsdom mobile invariants (`packages/web/src/pages/epics/ui/EpicListPage.test.tsx:203`, `packages/web/src/pages/epics/ui/EpicListPage.test.tsx:771`); Playwright covers real mobile overflow (`packages/web/tests/e2e/epic-list-mobile-overflow.spec.ts:160`).

## Verification

- `npm run typecheck -w packages/web` passed.
- `npm run test:run -w packages/web` passed: 175 files, 2594 passed, 1 skipped.
- `npm run test:e2e -w packages/web -- tests/e2e/epic-list-mobile-overflow.spec.ts` passed: 3 Chromium tests.

<promise>PASS</promise>
