# Review Report

## Result: PASS

## Repaired Items

None.

## Blocking Items

None.

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: `packages/server/src/Mohist.Server/Infrastructure/Data/Events/DeadLetterStore.cs`
  Evidence: `ListAsync` currently orders by `DeadLetterId` (append order). The design document specifies a `(DeadLetteredAt)` index for chronological query and defines the backlog as a chronological inspection surface. Because `DeadLetterId` is auto-increment and `DeadLetteredAt` is caller-supplied, the two usually coincide, but they can diverge if a caller backdates or predates records. Ordering by `DeadLetteredAt` would make the chronological contract explicit and use the index created for that purpose.
  SuggestedAction: Consider ordering `ListAsync` by `DeadLetteredAt` (then `DeadLetterId` as a tiebreaker) to align with the intended chronological contract and the existing index. Update or add a test that writes records with non-monotonic `DeadLetteredAt` to verify the ordering behavior.
  Status: follow-up

- [ID: item-2]
  Severity: follow-up
  Scope: `packages/server/tests/Mohist.Server.SpecTests/Specs/Events/DeadLetterStoreTests.cs` and `EventStoreDeliveryProgressSpecs.cs`
  Evidence: The `dead-letter-store` spec scenario "A dead-lettered message leaves the live delivery path" asserts that a dead-letter record must not be returned by the unified undelivered query. It is structurally impossible to violate today because `ListUndeliveredAsync` only UNIONs the three live event tables and `DeadLetters` is a separate physical table. However, there is no explicit cross-check test for this invariant, so a future refactor that accidentally includes `DeadLetters` in the undelivered query would not be caught by current tests.
  SuggestedAction: Add a one-line assertion in `DeadLetterStoreTests` or `EventStoreDeliveryProgressSpecs` that writes a dead-letter record and then asserts `ListUndeliveredAsync` returns zero rows.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: `packages/server/src/Mohist.Server/Infrastructure/Data/Events/EventStore.cs` and `DeadLetterStore.cs`
  Evidence: Both stores contain an identical private `ParseOrigin` method that maps the string values `"WorkflowRun"`, `"Issue"`, and `"Epic"` to the `EventOrigin` enum. The raw SQL in `EventStore.ListUndeliveredAsync` and the string persistence in `DeadLetterStore` already share the same set of origin literals, so a drift in the enum names would break both stores. A shared helper would remove the duplication and make the origin-string mapping a single point of truth.
  SuggestedAction: Extract a shared `EventOrigin.Parse` (or internal static helper) and use it in both `EventStore` and `DeadLetterStore`.
  Status: follow-up

## Pre-existing or Out-of-scope Items

None.

## Verification Summary

- `dotnet test Mohist.sln -p:SkipWebBuild=true`: passed (5004 tests: 865 CLI + 24 Arch + 4115 Spec)
- `npm run test:ci --workspaces --if-present`: passed (runner: 1031 tests)
- `npm run test:ci -w packages/web`: passed (4404 tests, 1 skipped)
- `npm run typecheck -w packages/web`: passed
- `npm run typecheck -w packages/runner`: passed
- `dotnet test packages/server/tests/Mohist.Server.SpecTests/Mohist.Server.SpecTests.csproj --filter "FullyQualifiedName~EventStoreDeliveryProgressSpecs|FullyQualifiedName~DeadLetterStoreTests|FullyQualifiedName~EventDeliveryProgressMigrationSpecs|FullyQualifiedName~EventStoreSpecs"`: passed (26 tests)

## Acceptance Criteria Coverage

- Delivery column + partial undelivered index on all three event tables: implemented in `MohistDbContext.cs` and migration `20260708015352_AddEventDeliveryProgressAndDeadLetters.cs`; verified by `EventDeliveryProgressMigrationSpecs` and `EventStoreDeliveryProgressSpecs`.
- DeadLetters table with snapshot, failing handler, error, and attempt count: implemented in `DeadLetterRow.cs`, `IDeadLetterStore.cs`, `DeadLetterStore.cs`; verified by `DeadLetterStoreTests`.
- Mark-delivered / list-undelivered / write-dead-letter ports covering all three tables: implemented in `IEventStore.cs`, `EventStore.cs`, `IDeadLetterStore.cs`, `DeadLetterStore.cs`; verified by `EventStoreDeliveryProgressSpecs` and `DeadLetterStoreTests`.
- Existing event write/read behavior unchanged; all tests green: existing `EventStoreSpecs` still pass, `List*Async` tests include delivered and undelivered rows, and the full test suite is green.

<promise>PASS</promise>
