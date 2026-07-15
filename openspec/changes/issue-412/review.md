# Review Report

## Result: PASS

The initial workflow event now has a versioned issue-lineage creation handoff. `IssueGrain` supplies the current lineage version with the run metadata; `WorkflowRunStore` conditionally locks that version in the same transaction that persists the first run event. If membership commits first, the stale create is rejected and the issue reloads before retrying. If creation commits first, the later link is ordered after the initial event. The workflow store continues to stamp only the metadata snapshot it owns.

The batch membership public retry budget now permits exactly three total persistence attempts. The recovery path remains durable: a failed affiliation delivery stays pending for dispatcher redelivery rather than being acknowledged.

## Verification

- `dotnet test packages/server/tests/Mohist.Server.SpecTests/Mohist.Server.SpecTests.csproj --no-restore -p:SkipWebBuild=true --filter 'FullyQualifiedName~WorkflowRunStoreSpecs|FullyQualifiedName~EpicAffiliationLineageSpecs|FullyQualifiedName~EpicBatchMembershipSpecs|FullyQualifiedName~EpicRecoverySpecs|FullyQualifiedName~IssueTransactionalEventAppendSpecs'` passed: 46 tests.
- `npm test` passed: 865 CLI, 1,408 server unit, 2,786 server spec, 22 architecture, 4,653 web, and 1,014 runner tests.
- `git diff --check` passed.

<promise>PASS</promise>
