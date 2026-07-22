# Review

No findings.

The rebase task payload now contains only `baseBranch` and `remote`, matching the `mohist/rebase` manifest. The route still rejects missing repository context and resolves an omitted base branch from the run-owned snapshot. Conflict recovery construction is unchanged, and the updated unit test asserts both the exact payload keys and the absence of `repository`.

Verification: `dotnet test packages/server/tests/Mohist.Server.UnitTests/Mohist.Server.UnitTests.csproj --no-restore --filter FullyQualifiedName~IssueRebaseRecoveryTests` passed with 2 tests.

<promise>PASS</promise>
