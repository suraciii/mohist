# Review Report

## Result: FAIL

## Repaired Items

_None. I did not apply repairs because the findings below affect product behavior and test strategy, not local formatting or typo-level issues._

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Issue/Domain/Issue.Transitions.cs`, issue close/cancel lifecycle
  Evidence: `Close(...)` now assigns `_completedAt = completedAt` every time it is called, but the method only rejects `Done` and archived issues; it does not reject or no-op when `_status == IssueStatus.Cancelled` (`Issue.Transitions.cs:203`). The public close route reaches this through `IssueGrain.CancelAsync`, which calls `_issue!.Close("user-cancelled")` without a terminal-state guard (`IssueGrain.cs:293`, `IssueGrain.cs:309`; route at `IssueRoutes.Lifecycle.cs:91`). A second close of an already-cancelled issue is not an "entering terminal state" transition, but it overwrites the persisted completion time and bumps `UpdatedAt`, so an old cancelled issue can acquire a fresh terminal timestamp without reopen/re-complete. This violates the acceptance criteria that completion time records terminal-entry time and only updates after reopen then completion. [disallowed:behavior-change]
  SuggestedAction: Make `Close` explicitly handle `Cancelled` before stamping `CompletedAt`, either as an idempotent no-op or as a conflict, and add a domain/API regression test that closing an already-cancelled issue does not change `CompletedAt` or re-count as newly completed.
  Verification: `npm test` passed overall, but no current test exercises repeated close on a cancelled issue; added coverage should fail before the fix and pass after it.
  Status: open

- [ID: item-2]
  Severity: warning
  Scope: `packages/web/src/entities/issue/lib/recent-digest.ts`, dashboard recent completed list
  Evidence: The completed digest bucket still includes every unarchived `status === 'done'` issue (`recent-digest.ts:36`) and only sorts missing `completedAt` values to the bottom with `parseTimestamp(a.completedAt ?? '')` (`recent-digest.ts:45`). When there are fewer than `DIGEST_TOP_N` valid completed rows, a done issue with no `completedAt` is still displayed in the "Completed" section, and `DashboardDigestWidget` then displays its timestamp from `issue.completedAt ?? issue.updatedAt` (`DashboardDigestWidget.tsx:62`). That means the recently-completed list can still surface an issue using `updatedAt` despite the requirement that completed rows be driven by persisted completion time. The migration design even documents terminal rows without completion events should drop out of recently completed until repaired; this implementation keeps them visible with an updatedAt display fallback. [disallowed:behavior-change]
  SuggestedAction: Filter completed digest candidates to rows with a parseable `completedAt`, or otherwise define an explicit product fallback that does not present `updatedAt` as a completed timestamp. Add tests for a done issue with missing `completedAt`, especially when the completed section has fewer than five valid rows.
  Verification: `npm run test:run -w packages/web` passed, but the existing tests do not cover missing `completedAt` in the digest; some existing widget tests still create completed rows without `completedAt` and expect the section to render (`DashboardDigestWidget.test.tsx:70`, `DashboardDigestWidget.test.tsx:154`).
  Status: open

- [ID: item-3]
  Severity: test-gap
  Scope: `packages/server/tests/Mohist.Server.Tests/Specs/Issue/Domain/BackfillIssueCompletedAtMigrationSpecs.cs`, migration verification
  Evidence: Most migration tests call the private `RunMigrationUpAsync` helper (`BackfillIssueCompletedAtMigrationSpecs.cs:186`) that duplicates the SQL text from the migration instead of executing `20260629120000_BackfillIssueCompletedAt` through EF. The only `MigrateAsync` test verifies the migration ID is applied (`BackfillIssueCompletedAtMigrationSpecs.cs:172`), but it does not seed terminal issues or verify the migration's actual `Up` SQL changes data. If the migration file diverges from the copied helper SQL, the backfill tests can still pass while production backfill is wrong. [disallowed:test-strategy]
  SuggestedAction: Replace the copied-SQL helper with EF migration execution against seeded SQLite data, or add at least one seeded `MigrateAsync` test that verifies done/cancelled data is backfilled by the actual migration class.
  Verification: `npm test` passed (`Mohist.Server.Tests`: 3078 passed, 13 skipped; runner/web workspace tests also passed), but this gap remains because the production migration SQL is not the code under test for the backfill assertions.
  Status: open

## Follow-up Items

- [ID: item-4]
  Severity: follow-up
  Scope: issue completion timestamp precision and traceability
  Evidence: Live writes stamp `CompletedAt` from transition `DateTime.UtcNow` (`Issue.Transitions.cs:177`, `Issue.Transitions.cs:207`), while the durable completion event envelope time is assigned later during publish (`IssueGrain.cs:620`). The approved design accepts this small skew, but the spec/proposal wording repeatedly says the field is sourced from completion-event time. This does not create the dashboard bug by itself, but it leaves traceability ambiguity between snapshot `completedAt` and event-log `Time`.
  SuggestedAction: Either align the spec wording to "terminal transition time" or add a future event timestamp payload/envelope-time handoff if byte-level parity between live writes and backfill becomes important.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-5]
  Severity: info
  Scope: repository test output
  Evidence: `npm test` prints expected negative-path stderr from runner tests, such as simulated SignalR failures, model discovery failures, and cleanup failures. The command still completed successfully: server `dotnet test` reported 3078 passed / 13 skipped; web workspace tests reported 2874 passed / 1 skipped; runner workspace tests reported 781 passed / 23 skipped.
  SuggestedAction: No action for this issue unless the project wants quieter test logs.
  Status: out-of-scope

## Verification

- `mo issue show 299 --project-id proj_f6c141d63b6243bfbb481737b2243b87` read before review.
- `npm test` passed.
- `npm run typecheck -w packages/web` passed.
- `npm run test:run -w packages/web` passed: 194 files passed, 2874 passed, 1 skipped.

<promise>FAIL</promise>
