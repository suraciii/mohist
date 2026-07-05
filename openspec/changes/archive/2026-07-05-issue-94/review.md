# Review Report

## Result: FAIL

## Repaired Items

- _None._

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: packages/server/src/Mohist.Server/Epic/Grains/EpicGrain.cs
  Evidence: `ReopenAsync` commits the terminal-to-idle `EpicRow` update at `packages/server/src/Mohist.Server/Epic/Grains/EpicGrain.cs:513-514`, then re-establishes `EpicActiveIssues` in later per-issue saves at `packages/server/src/Mohist.Server/Epic/Grains/EpicGrain.cs:525-540`. If the process crashes or a transient database error occurs after the first save, the epic is now non-terminal but its linked issues may have no active memberships. Retrying `POST /reopen` then fails with `EPIC_NOT_TERMINAL`, so the recovery path cannot complete the acceptance criterion that reopen re-establish active memberships for linked issues. [disallowed:product-behavior-and-data-safety]
  SuggestedAction: Persist the status change and active-membership re-claim atomically, or make the re-claim idempotently retryable for already-idle epics that were just reopened and have missing active memberships.
  Verification: Add a regression test that injects a failure between the first reopen save and the active-membership inserts, then proves retry or recovery restores `EpicActiveIssues` without violating the uniqueness invariant.
  Status: open

- [ID: item-2]
  Severity: warning
  Scope: packages/server/src/Mohist.Server/Api/EpicRoutes.cs
  Evidence: Batch unlink maps unresolved identifiers through `MergeBatchOutcomes`, which emits `BatchMembershipOutcome.NotFound(identifier)` at `packages/server/src/Mohist.Server/Api/EpicRoutes.cs:418-428`. The batch unlink spec defines only `unlinked` and `was-not-a-member` outcomes for unlink responses (`openspec/changes/issue-94/specs/epic-batch-membership/spec.md:88-92`), while `not-found` is defined for batch link. This creates a public response status that is outside the unlink contract. [disallowed:public-contract-change]
  SuggestedAction: Decide the unlink contract for unresolved issue identifiers, then either return `was-not-a-member` for unknown unlink identifiers or update the spec, DTO docs, client typing, and tests to explicitly allow `not-found` on unlink.
  Verification: Add/adjust API tests for `POST /issues:batch-unlink` with an unknown identifier and assert the documented status set.
  Status: open

- [ID: item-3]
  Severity: warning
  Scope: packages/server/src/Mohist.Server/Api/EpicRoutes.cs
  Evidence: The batch routes drop duplicate requested identifiers before calling the grain at `packages/server/src/Mohist.Server/Api/EpicRoutes.cs:309-313` and `packages/server/src/Mohist.Server/Api/EpicRoutes.cs:364-368`, and `MergeBatchOutcomes` drops duplicates again at `packages/server/src/Mohist.Server/Api/EpicRoutes.cs:412-417`. That conflicts with the task acceptance criterion that both batch endpoints return one outcome entry per requested identifier (`openspec/changes/issue-94/tasks.json:67`) and leaves clients unable to correlate responses to the original array length. The current duplicate test only asserts `>= 1` result at `packages/server/tests/Mohist.Server.Tests/Specs/Epic/Api/EpicBatchMembershipApiSpecs.cs:177-180`, so this contract drift is not caught. [disallowed:public-contract-change]
  SuggestedAction: Preserve response cardinality for every non-blank requested token, marking duplicates as a non-error outcome, or update the OpenSpec/task contract to say responses are per unique identifier and align tests/docs/client expectations.
  Verification: Add API tests for duplicate identifiers on both batch link and batch unlink that assert the exact response length and per-entry identifiers.
  Status: open

- [ID: item-4]
  Severity: minor
  Scope: packages/server/src/Mohist.Server/Epic/Services/EpicQuerier.cs
  Evidence: Title search uses `LIKE '%' || @search || '%'` at `packages/server/src/Mohist.Server/Epic/Services/EpicQuerier.cs:62` and `NormalizeSearch` returns the trimmed input unchanged at `packages/server/src/Mohist.Server/Epic/Services/EpicQuerier.cs:142-147`. The parameterization is injection-safe, but `%` and `_` are still SQL wildcard syntax, so a search for a literal `%` or `_` can match unrelated/all titles instead of titles containing that literal term. The issue/spec require title substring search, not pattern search (`openspec/changes/issue-94/specs/epic-list-query/spec.md:1-8`). [disallowed:product-behavior-change]
  SuggestedAction: Escape SQL LIKE wildcards in the search term and use an `ESCAPE` clause, or document that the endpoint intentionally accepts LIKE patterns and update tests/specs accordingly.
  Verification: Add querier/API tests for titles containing literal `%` and `_`, proving those searches do not match unrelated titles.
  Status: open

- [ID: item-5]
  Severity: minor
  Scope: packages/web/src/pages/epics/ui/EpicListPage.tsx
  Evidence: The list page sends active search/sort params through `useEpics` at `packages/web/src/pages/epics/ui/EpicListPage.tsx:380-386`, but any empty result renders the project-empty message and CTA at `packages/web/src/pages/epics/ui/EpicListPage.tsx:510-517` (`No epics yet` / `Create your first Epic`). A no-match search therefore tells users the project has no epics, which is incorrect for the filtered-list workflow added by this issue. [disallowed:product-behavior-change]
  SuggestedAction: Track whether a filter/sort query is active and render a no-results state for empty filtered results, while keeping the create-first state for an actually unfiltered empty project.
  Verification: Add a web test where `searchInput` is non-empty and `useEpics` returns `[]`, asserting no-match copy rather than the create-first empty state.
  Status: open

## Follow-up Items

- [ID: item-6]
  Severity: follow-up
  Scope: packages/web/src/entities/epic/api/queries.ts and packages/web/src/entities/epic/index.ts
  Evidence: Batch membership has low-level client functions in `packages/web/src/entities/epic/api/client.ts:56-82`, but no public entity hook/export or shared invalidation path alongside the existing single issue add/remove hooks. This is not a direct acceptance failure because the issue does not require a batch-membership UI, but it makes future web callers deep-import client functions and duplicate invalidation/toast behavior.
  SuggestedAction: Add `useBatchAddEpicIssues` and `useBatchRemoveEpicIssues` when a UI starts using the batch endpoints.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-7]
  Severity: warning
  Scope: packages/runner/tests/runner-host-task-log.spec.ts
  Evidence: `npm test` completed the server suite successfully (`3897` passed, `13` skipped) and then failed in the runner workspace: `RunnerHost task-log best-effort flush (T-003) > RoutesConcurrentIncrementalUploadsToEachWorkItemCollector` timed out after 5000ms. Issue 94 changed no runner product files, so this failure is outside the reviewed candidate surface, but it means the monorepo `npm test` command did not finish green in this run.
  SuggestedAction: Re-run or investigate the runner test independently of issue 94.
  Status: out-of-scope

## Verification

- Read issue details with `mo issue show 94 --project-id proj_f6c141d63b6243bfbb481737b2243b87`.
- Reviewed `openspec/changes/issue-94/proposal.md`, `design.md`, `tasks.json`, `self-review.md`, and all issue-94 delta specs.
- Reviewed changed server, web, migration, and test files from `git diff master...HEAD`.
- Ran `npm run typecheck -w packages/web`: passed.
- Ran `npm run test:run -w packages/web`: passed (`271` files, `4340` tests passed, `1` skipped).
- Ran `npm test`: server phase passed (`3897` passed, `13` skipped); runner workspace failed on the out-of-scope timeout recorded above.

<promise>FAIL</promise>
