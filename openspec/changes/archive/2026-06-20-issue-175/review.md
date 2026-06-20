# Review Report

## Result: PASS

## Repaired Items

None.

## Blocking Items

None.

## Follow-up Items

None.

## Pre-existing or Out-of-scope Items

- [ID: item-1]
  Severity: info
  Scope: repository diff outside issue-175 deliverables
  Evidence: `git diff --name-only 478febb70d229148f07f664705df2e357f464e97...HEAD` includes many archived OpenSpec changes and unrelated CLI/runner/server/web changes from other issues. Per the candidate boundary, workflow artifacts are review context and not product deliverables for issue 175; unrelated integrated changes were not used to fail this issue except where they directly affect epic inline start contracts.
  SuggestedAction: Keep future review branches scoped or provide an issue-specific base/ref to reduce unrelated review noise.
  Status: out-of-scope

- [ID: item-2]
  Severity: info
  Scope: openspec/changes/archive/2026-06-20-issue-217/progress.txt
  Evidence: `git diff --check 478febb70d229148f07f664705df2e357f464e97...HEAD` reports `openspec/changes/archive/2026-06-20-issue-217/progress.txt:595: new blank line at EOF`. This is unrelated archived workflow evidence, not an issue-175 product deliverable.
  SuggestedAction: Trim the extra blank line when touching that archived artifact for another reason.
  Status: out-of-scope

## Acceptance Criteria Evidence

- Epic list card Start: `packages/web/src/pages/epics/ui/EpicListPage.tsx:70` renders a Start button only when `progress.nextIssue` is present; `packages/web/src/pages/epics/ui/EpicListPage.tsx:80` stops propagation before calling Start, so card navigation is not triggered.
- Epic list blocker/no-start states: `packages/web/src/pages/epics/ui/EpicListPage.tsx:91` shows `nextIssueReason` without Start, and `packages/web/src/pages/epics/ui/EpicListPage.test.tsx:612`/`packages/web/src/pages/epics/ui/EpicListPage.test.tsx:621` cover reason and ready states.
- Epic detail linked issue Start: `packages/web/src/pages/epic-detail/ui/EpicDetailPage.tsx:95` gates row Start via `canInlineStartRow`, and `packages/web/src/entities/epic/model/inline-start.ts:4` rejects non-startable, in-progress, done, cancelled, and blocked rows.
- Epic detail next issue Start: `packages/web/src/pages/epic-detail/ui/EpicDetailPage.tsx:589` preserves the next-issue link and adds Start for `progress.nextIssue`; `packages/web/src/pages/epic-detail/ui/EpicDetailPage.test.tsx:1410` verifies link preservation and mutation invocation.
- Existing start path and cache refresh: `packages/web/src/entities/epic/api/queries.ts:75` reuses `startIssue(number, projectId)`, invalidates `['epics']` and `['issues']`, and reports success/error toasts. `packages/web/src/entities/epic/api/queries.test.tsx:87` and `packages/web/src/entities/epic/api/queries.test.tsx:102` cover success invalidation/toast and failure toast.
- DTO contract: server `LinkedIssueDto.StartBlocker` is populated from `IssueInfo.Blocker` at `packages/server/src/Mohist.Server/Epic/Services/EpicQuerier.cs:99`, and the web `LinkedIssue` type now matches the wire field as `startBlocker` at `packages/web/src/entities/epic/model/types.ts:54`.

## Verification

- `mo issue show 175 --project-id proj_f6c141d63b6243bfbb481737b2243b87` inspected acceptance criteria and current issue context.
- Read `openspec/changes/issue-175/proposal.md`, `design.md`, `tasks.json`, and `specs/epic-inline-start/spec.md`.
- Reviewed changed product files for the issue-175 deliverable: `packages/web/src/entities/epic/api/queries.ts`, `packages/web/src/entities/epic/model/inline-start.ts`, `packages/web/src/entities/epic/model/types.ts`, `packages/web/src/pages/epics/ui/EpicListPage.tsx`, `packages/web/src/pages/epic-detail/ui/EpicDetailPage.tsx`, and related tests.
- Reviewed adjacent DTO/start paths: `packages/server/src/Mohist.Server/Epic/Services/EpicDtos.cs`, `packages/server/src/Mohist.Server/Epic/Services/EpicQuerier.cs`, and `packages/web/src/entities/issue/api/client.ts`.
- `npm run typecheck -w packages/web` passed.
- `npm run test:run -w packages/web -- EpicDetailPage inline-start queries EpicListPage` passed: 7 files, 119 tests.

<promise>PASS</promise>
