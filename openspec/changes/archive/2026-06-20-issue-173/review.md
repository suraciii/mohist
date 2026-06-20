# Review Report

## Result: PASS

## Repaired Items

_None._

## Blocking Items

_None._

## Follow-up Items

_None._

## Pre-existing or Out-of-scope Items

- [ID: item-1]
  Severity: info
  Scope: packages/web/src/entities/epic/api/queries.ts:111
  Evidence: The previous review found that Pause/Resume invalidated `['epics', id]` instead of the actual detail key `['epics', projectId, id]`. The current snapshot now invalidates `['epics', projectId, variables.id]` for Pause and `['epics', projectId, id]` for Resume, and `packages/web/src/entities/epic/api/queries.test.tsx` verifies both paths do not use the old malformed key.
  SuggestedAction: None for this issue.
  Status: out-of-scope

- [ID: item-2]
  Severity: warning
  Scope: packages/web/src/entities/epic/api/queries.ts:45
  Evidence: The broader pre-existing detail-key mismatch still exists for add/remove issue, mark done, close, and update mutations (`['epics', id]` rather than `['epics', projectId, id]`). This predates and is broader than the Paused acceptance criteria; the current change fixed the newly added Pause/Resume mutations and test coverage for them.
  SuggestedAction: Consider a separate cleanup to introduce shared Epic query-key helpers and align all Epic detail invalidations.
  Status: pre-existing

- [ID: item-3]
  Severity: info
  Scope: validation
  Evidence: Focused server Epic verification passed: `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter "FullyQualifiedName~Epic"` -> 76 passed. The command also ran the web production build successfully and reported existing npm audit output: 9 vulnerabilities (3 moderate, 3 high, 3 critical).
  SuggestedAction: Track dependency audit remediation separately unless dependency security cleanup is assigned.
  Status: out-of-scope

- [ID: item-4]
  Severity: info
  Scope: validation
  Evidence: Focused web verification passed: `npm --prefix packages/web test -- --run src/entities/epic/api/queries.test.tsx src/pages/epic-detail/ui/EpicDetailPage.test.tsx src/pages/epics/ui/EpicListPage.test.tsx src/widgets/app-shell/ui/Header.test.tsx` -> 4 files, 62 tests passed. This covers Pause/Resume cache invalidation, detail lifecycle UI, Paused list grouping, and Epic header title resolution.
  SuggestedAction: None.
  Status: out-of-scope

<promise>PASS</promise>
