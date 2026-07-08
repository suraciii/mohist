# Review Report

## Result: FAIL

## Repaired Items

None.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Infrastructure/Data/Events/DeadLetterStore.cs`
  Evidence: `ListAsync` orders by `DeadLetterId` descending, applies `Take(limit)`, then re-orders ascending before materializing ([DeadLetterStore.cs](/home/szf/.mohist/projects/mohist-local/workspaces/issue-360/packages/server/src/Mohist.Server/Infrastructure/Data/Events/DeadLetterStore.cs:46)). That means the API returns the newest `limit` rows, not the earliest `limit` rows. The port contract and design define the store as a query surface for the poison backlog ordered chronologically by `DeadLetteredAt`/append order, and the current implementation silently drops older backlog entries once the table grows past `limit`. The existing tests only cover two rows and therefore miss this truncation behavior.
  SuggestedAction: Order directly on the intended backlog sequence for the returned slice, then `Take(limit)` without the descending pre-trim. Add a test that writes more than `limit` rows and verifies which slice is returned.
  Verification: `dotnet test packages/server/tests/Mohist.Server.SpecTests/Mohist.Server.SpecTests.csproj --filter "FullyQualifiedName~DeadLetterStoreTests"`
  Status: open

- [ID: item-2]
  Severity: warning
  Scope: verification / acceptance evidence
  Evidence: The issue instructions and repo docs say the server verification command is `npm test -w packages/server`, but this repository only declares npm workspaces for `packages/web` and `packages/runner` ([package.json](/home/szf/.mohist/projects/mohist-local/workspaces/issue-360/package.json:5)). Running the documented command fails with `npm error No workspaces found: --workspace=packages/server`, so the change cannot satisfy the stated acceptance check as written. I verified the relevant server tests with `dotnet test ...` instead, but the documented verification path for this issue is still broken.
  SuggestedAction: Update the issue/task verification instructions or repo docs to use the actual server test entrypoint (`dotnet test Mohist.sln ...` or the specific server test project command).
  Verification: `npm test -w packages/server`
  Status: open

## Follow-up Items

- [ID: item-3]
  Severity: follow-up
  Scope: `packages/server/tests/Mohist.Server.SpecTests/Specs/Events/DeadLetterStoreTests.cs`
  Evidence: The tests cover append-only behavior and round-trip fields, but they do not assert any ordering contract for `ListAsync`, which is how item-1 slipped through.
  SuggestedAction: Add an explicit ordering/limit test for `ListAsync`, ideally with more than 100 inserted records to exercise the default limit too.
  Status: follow-up

## Pre-existing or Out-of-scope Items

None.

<promise>FAIL</promise>
