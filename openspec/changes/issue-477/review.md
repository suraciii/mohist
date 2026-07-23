# Review: Issue 477

## Findings

### [P1] Keep recoverable Failed runs as deletion blockers

`WorkflowRunStatus.IsTerminal` explicitly defines only `Stopped` and `Completed` as terminal; `Failed` is recoverable and can be retried (`packages/server/src/Mohist.Server/Workflow/Domain/Run/WorkflowRun.cs:18-26`). However, `WorkflowProfileDeletionBlockerQuery.ListActiveRunsAsync` includes `failed` in `TerminalRunStatuses` (`packages/server/src/Mohist.Server/Workflow/Services/WorkflowProfileDeletionBlockerQuery.cs:69-94`). A failed run therefore disappears from the diagnostic blocker list even though `WorkflowRunStore` retains its custom `WorkflowProfileIdKey` (`packages/server/src/Mohist.Server/Infrastructure/Data/Workflow/WorkflowRunStore.cs:155-159`). Deletion then hits the restrictive FK, re-queries the same incomplete blocker set, and reports `ProfileUnknown`/not-found instead of the required reference relationship. Use the domain terminal statuses for this query and add a failed/retryable run deletion spec.

### [P1] Restrict FK-race translation to an actual Profile FK violation

`IssueStore.TranslateProfileForeignKeyViolation` returns `WorkflowProfileNotFoundException` for every `DbUpdateException` whenever the Issue has a custom profile (`packages/server/src/Mohist.Server/Infrastructure/Data/Issue/IssueStore.cs:97-113`); it never inspects the exception or inner SQLite/PostgreSQL constraint. Any unrelated persistence failure during Issue create/edit, such as a duplicate key, another FK failure, or a database error, is therefore misreported as the retryable `workflow-profile-not-found` conflict. Match the database constraint/provider error for `WorkflowProfileIdKey` before translating, and rethrow unrelated `DbUpdateException`s unchanged.

### [P2] Bring the new collection spec under the enforced size ratchet

The current verification fails `Mohist.Server.ArchTests.ArchitectureRules.CSharpTestFiles_MustStayWithinSizeRatchet`: `Specs/Workflow/WorkflowProfileCollectionSpecs.cs` is 27,448 bytes while its recorded allowance is 26,398 bytes. The file grew in the current change with additional migration and active-run coverage, so the repository cannot pass its architecture gate as committed. Split the spec by subject or reduce/extract shared setup so the resulting test files satisfy the existing size rule, while retaining the added coverage.

<promise>FAIL</promise>
