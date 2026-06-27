# Review Report

## Result: FAIL

## Repaired Items

- [ID: item-0]
  Severity: info
  Scope: review
  Evidence: No safe local repair was applied. The open findings require migration strategy or product behavior changes, which are disallowed during review repair.
  Verification: Not applicable.
  Status: resolved

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: packages/server/src/Mohist.Server/Infrastructure/Data/Migrations/20260626200645_AddEpicListDerivedColumns.cs:13
  Evidence: The migration adds four `stored: true` computed columns to an existing `Issues` table via `migrationBuilder.AddColumn`. SQLite rejects adding a STORED generated column to a non-empty existing table. A direct reproduction with `CREATE TABLE Issues (...); INSERT ...; ALTER TABLE Issues ADD COLUMN Title TEXT GENERATED ALWAYS AS (...) STORED;` fails with `cannot add a STORED column`. The new tests only apply all migrations to a fresh in-memory database (`IssueDerivedColumnsSpecs.cs:24-25`), so they miss upgrade-on-existing-data. [disallowed: migration/data-safety strategy change]
  SuggestedAction: Change the migration strategy so upgrading existing SQLite databases succeeds, for example by rebuilding the `Issues` table with the new generated columns or by using a compatible computed-column strategy after explicitly accepting the performance tradeoff. Add a test that migrates a database containing pre-existing `Issues` rows from the prior migration state.
  Verification: Run a migration test that creates an `Issues` row before `AddEpicListDerivedColumns`, applies the new migration, and reads `Title`, `Priority`, `IsDraft`, and `PrerequisiteNumbersJson`; then run `dotnet test Mohist.sln -p:SkipWebBuild=true`.
  Status: open

- [ID: item-2]
  Severity: warning
  Scope: packages/server/src/Mohist.Server/Epic/Services/EpicQuerier.cs:60
  Evidence: `allIssuesByNumber` is built only from issues returned by the `Epics` -> `EpicIssues` -> `Issues` aggregate rows (`lines 60-67`). `ComputeStartBlocker` then treats a prerequisite as delivered only if its number is absent from `allUndeliveredNumbers` (`lines 204-212`). The detail/full-enrichment path resolves missing prerequisite issues from the project-wide `Issues` table (`IssueQuerier.cs:734-746`) before computing `CanStart` (`IssueQuerier.cs:758-762`). As a result, a linked issue whose prerequisite exists in the same project and is done, but is not linked to any epic in the list result set, is incorrectly reported as blocked on the list endpoint. This violates the issue/spec requirement that `nextIssue` and `CanStart` remain exact and match the detail path. [disallowed: product behavior/query contract change]
  SuggestedAction: Resolve prerequisite statuses from the full project issue set without reintroducing N+1, or define and document a narrower accepted semantic. At minimum, add a regression test where issue #2 is linked to an epic, depends on done issue #1, and #1 is not linked to any epic; list and detail should agree that #2 can start.
  Verification: Add the regression test above and run `dotnet test Mohist.sln -p:SkipWebBuild=true --filter FullyQualifiedName~EpicQuerierListAsyncSpecs` plus the full server suite.
  Status: open

- [ID: item-3]
  Severity: minor
  Scope: packages/server/src/Mohist.Server/Epic/Services/EpicQuerier.cs:53
  Evidence: The old list path ordered epics with `OrderByPriorityThenUpdatedAt`, using `PriorityRank` so unknown priority values sort after `p4`. The new raw SQL orders by the raw string `e."Priority"`, then `UpdatedAt` (`line 53`). `EpicPriority.From` accepts any non-empty priority string, so custom or malformed values such as `a` now sort before `p0` instead of last. The added test (`EpicQuerierListAsyncSpecs.cs:235-250`) covers only normal `p0`/`p2` values and does not catch this behavior drift. [disallowed: product behavior/query semantics change]
  SuggestedAction: Preserve the previous rank ordering in SQL with a `CASE` expression, or explicitly validate/normalize epic priorities so unknown values cannot be stored. Add a test covering an unknown priority value.
  Verification: Run `dotnet test Mohist.sln -p:SkipWebBuild=true --filter FullyQualifiedName~EpicQuerierListAsyncSpecs` and verify unknown priorities sort after `p4` or are rejected by validation.
  Status: open

- [ID: item-4]
  Severity: test-gap
  Scope: packages/server/tests/Mohist.Server.Tests/Specs/Epic/Domain/EpicQuerierListAsyncSpecs.cs:177
  Evidence: The new prerequisite tests cover only prerequisites that are also linked to the same epic (`lines 181-184`). There is no list-vs-detail comparison test and no test for prerequisites outside the aggregate row set, which is the exact edge that regresses in item-2.
  SuggestedAction: Add focused tests comparing list and detail progress for external project prerequisites, including done, backlog, and missing prerequisite numbers.
  Verification: Run the focused `EpicQuerierListAsyncSpecs` filter and the full server suite.
  Status: open

## Follow-up Items

- [ID: item-5]
  Severity: follow-up
  Scope: packages/server/src/Mohist.Server/Epic/Services/EpicQuerier.cs:50
  Evidence: The raw joins rely on `EpicId`/`IssueId` alone (`lines 50-51`) even though `EpicIssues` carries `ProjectId` and the previous detail path filters issues through `IssueQuerier.ListAsync(epic.ProjectId, all: true)`. Normal writes likely keep IDs globally consistent, but adding `li."ProjectId" = e."ProjectId"` and `i."ProjectId" = e."ProjectId"` would make the raw SQL mirror the project boundary more explicitly.
  SuggestedAction: Consider tightening the join predicates if it does not affect the query plan.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-6]
  Severity: info
  Scope: openspec/changes/issue-274
  Evidence: Workflow artifacts (`proposal.md`, `design.md`, `tasks.json`, `self-review.md`, specs, and this review) are expected review context for this issue and are not product deliverables by themselves.
  SuggestedAction: None.
  Status: out-of-scope

## Verification

- Read issue details with `mo issue show 274 --project-id proj_f6c141d63b6243bfbb481737b2243b87`.
- Reviewed candidate diff against `master...HEAD`, all issue artifacts, changed implementation files, migration files, and added tests.
- Ran `npm test`: runner/workspace tests passed, summary `47 passed | 3 skipped`, `652 passed | 23 skipped`.
- Ran `dotnet test Mohist.sln -p:SkipWebBuild=true --no-build`: passed, `2841 passed`, `14 skipped`.
- Ran focused server tests: `IssueDerivedColumnsSpecs` passed `2/2`; `EpicQuerierListAsyncSpecs` passed `11/11`.
- Ran a direct SQLite compatibility check for adding a STORED generated column to a populated table; it failed with `cannot add a STORED column`.

<promise>FAIL</promise>
