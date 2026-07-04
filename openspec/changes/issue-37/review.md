# Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: small test expectation update
  Evidence: `npm test` initially failed in `packages/server/tests/Mohist.Server.Tests/Specs/Epic/Domain/EpicQuerierExternalPrerequisitesSpecs.cs:48`. The reviewed change split prerequisite summary semantics so `IssuePrerequisiteSummary.Status` now carries the workflow status (`in_progress`) while `Health` carries health (`active`). The epic external-prerequisite test still expected the old health value in `Status`, so I updated the assertion from `active` to `in_progress`.
  Verification: `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter BuildExternalPrerequisites_ResolvesExternalPrereqToSummary` passed; `npm run typecheck -w packages/web` passed; `npm run test:run -w packages/web` passed; `npm test` passed with server 3832 passed / 13 skipped, web 4344 passed / 1 skipped, runner 908 passed.
  Status: resolved

## Blocking Items

- None.

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: `packages/web/src/features/prerequisite-picker/ui/IssuePrerequisitePicker.tsx`
  Evidence: The picker intentionally filters the full project issue list client-side (`useIssues({ projectId, all: true })`, then in-memory filtering by number/title/status). This satisfies the current issue and matches the design risk, but very large projects may need debouncing or result capping.
  SuggestedAction: Add debouncing and a result cap if real project sizes make the picker sluggish.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-3]
  Severity: info
  Scope: `packages/server/src/Mohist.Server/Api/IssueRoutes.Prerequisites.cs`
  Evidence: The single-add prerequisite route maps every `IssuePrerequisiteResult` failure to `404 NotFound`, including self/circular validation failures. This route behavior existed before the picker replacement and the issue explicitly freezes the single-add/remove HTTP contract; the new picker surfaces the returned error text to the user.
  SuggestedAction: If API status-code semantics matter later, split not-found from validation/cycle errors in a separate contract change.
  Status: pre-existing

<promise>PASS</promise>
