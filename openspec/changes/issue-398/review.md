# Review Report

## Result: FAIL

## Repaired Items

- None.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/web` verification surface
  Evidence: The candidate does not satisfy the required green web test suite. `npm run test:run -w packages/web` currently fails with 12 tests, including `src/shared/ui/components/badge-button.test.tsx`, `src/shared/ui/components/alert-dialog.test.tsx`, `src/pages/epic-detail/ui/LinkedIssueRow.removeConfirm.test.tsx`, `src/pages/settings/ui/LabelCatalogSection.test.tsx`, and `src/pages/settings/ui/AgentSettingsSection.test.tsx`. The implementation intentionally switched semantic text from `*-foreground` to `text-*` in `packages/web/src/shared/status-presentation/index.ts`, `packages/web/src/shared/ui/components/button.tsx`, `packages/web/src/shared/ui/components/badge.tsx`, and `packages/web/src/shared/ui/components/field-error.tsx`, but the shipped tests still assert the old `*-foreground` contract. That leaves the post-build candidate red and violates the issue/tasks acceptance criteria that require `npm run test:run -w packages/web` to pass.
  SuggestedAction: Update the stale tests and any remaining callers that still encode the old `*-foreground` expectation so the verified contract matches the implemented token treatment, then rerun `npm run test:run -w packages/web`.
  Verification: `npm run test:run -w packages/web`
  Status: open

- [ID: item-2]
  Severity: blocking
  Scope: `widgets/issue-event-timeline/*`
  Evidence: The issue acceptance criteria and specs explicitly include the activity surface and require consistent status rendering on activity pages. The candidate only rewired the timeline row marker override in `packages/web/src/widgets/issue-event-timeline/ui/EventTimelineRow.tsx`, but the category style registry still hardcodes the old failure palette in `packages/web/src/widgets/issue-event-timeline/model/types.ts`: `bg-red-50`, `text-red-700`, `border-red-200`, and `bg-red-500`. Those values are still consumed by activity UI such as `packages/web/src/widgets/issue-event-timeline/ui/CategoryFilter.tsx`, so the activity surface still has a parallel status color system and still ships light-only status combinations on a covered page. Repair was considered, but replacing the category styling is a visible product-surface behavior change across the activity UI and is disallowed by the repair policy.
  SuggestedAction: Route `CATEGORY_STYLES` and its consumers through the shared status-presentation/theme-token layer, including failure/attention category chips and related activity badges, and add coverage that exercises the rendered activity surface rather than only the marker dot.
  Verification: Inspect the rendered activity filter/timeline classes and rerun `npm run test:run -w packages/web`.
  Status: open

## Follow-up Items

- [ID: item-3]
  Severity: follow-up
  Scope: `packages/web/src/shared/status-presentation/cross-surface.equivalence.spec.tsx`
  Evidence: The new equivalence spec deliberately allows stale/offline divergence in runner summary at `packages/web/src/shared/status-presentation/cross-surface.equivalence.spec.tsx`: offline in `RunnerList` may still render differently from the aggregated stale/offline summary state. That may be intentional UX, but it weakens the stated invariant that the same domain state renders identically across covered surfaces.
  SuggestedAction: Either narrow the product/spec wording to allow aggregated runner-summary semantics, or strengthen the implementation/spec so offline is rendered consistently cross-surface.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-4]
  Severity: info
  Scope: uncovered non-milestone surfaces outside the issue's covered pages
  Evidence: There are still many raw palette classes elsewhere in the web app, for example in session transcript, settings, inbox, and other pages. I did not treat those as blockers because issue #398 and the associated specs scope this milestone to dashboard, board, issue detail, activity, session, runner, and the covered shared primitives/surfaces.
  SuggestedAction: Continue the token/status cleanup in later issues once the current milestone passes on its covered surfaces.
  Status: out-of-scope

<promise>FAIL</promise>
