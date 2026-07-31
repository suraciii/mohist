# Review: Issue 531

## Findings

### H1. A running snapshot with the wrong work id is trusted and becomes permanently unreportable

The new Running-snapshot guard verifies only that `dispatchSnapshot.workId` is nonblank, not that it equals the legacy state's `workId` (`packages/server/src/Mohist.Server/Infrastructure/Data/Migrations/20260729000000_AgentJobOwnerLedger.cs:273-283`). A row whose state work id is `work-a` and whose otherwise valid snapshot has `work-b` consequently retains `work-a` in the indexed ledger while the runner receives `work-b`. Redelivery reconciliation keys the former from the row (`packages/server/src/Mohist.Server/Runner/Services/DispatchService.cs:133-142`), but direct result reporting validates the latter against `State.WorkId` and rejects it (`packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.cs:300-307`). The runner keeps retrying its report while the server keeps treating the row as missing and redelivering it. Require snapshot `workId` to equal the row's state work id before retaining it, otherwise rebuild from complete input or reject the migration, and add a mismatched-work-id regression.

### H2. Assigned Pending rows still persist malformed snapshots without reconstruction or migration failure

The repair adds snapshot validation only to the Running update. The assigned Pending/Unknown update still copies every non-null `dispatchSnapshot` verbatim (`AgentJobOwnerLedger.cs:327-363`), while migration validation never checks its JSON or dispatch identity (`:204-226`). An otherwise valid Pending row with `dispatchSnapshot: "not-json"` commits successfully; its first poll claims the row and only then fails deserialization in `AgentJobGrain.ClaimNextAsync` (`packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.cs:243-248`). That is not an atomically rejected legacy migration, as required for a nonterminal row that cannot reconstruct its dispatch ledger, and it changes valid upgrade behavior into a later `invalid-dispatch` terminal failure. Apply the same snapshot validation/fallback policy to the Pending path (or reject the migration when no complete reconstruction is possible) and cover malformed assigned Pending and Unknown rows.

## Verification

- Compared `origin/master...HEAD` with the Issue 531 plan artifacts and acceptance criteria.
- Rechecked the repair commit's report gate, Running snapshot guard, and remaining Pending migration path against the owner-ledger requirements.
- Did not rerun the full Server suite in this review-only pass.

<promise>FAIL</promise>
