# Review Report

## Result: PASS

Reviewed the current issue #412 candidate after the lineage repair. The producer snapshots now preserve the active affiliation across workflow creation, membership changes, workflow persistence conflicts, and cutover migration without querying another aggregate during event emission.

## Repaired Items

- [ID: item-1]
  Scope: workflow creation concurrent with an epic link
  Resolution: after the Issue transaction binds the new workflow run, `IssueGrain` synchronizes the run's snapshot from the authoritative Issue scalar. The synchronization retries a competing workflow write, closing the start/link handoff without changing historic envelopes.

- [ID: item-2]
  Scope: workflow persistence concurrent with membership updates
  Resolution: membership staging advances the WorkflowRun `ETag` with its scalar affiliation change. A stale workflow append cannot commit an event with the old lineage, and membership operations retry boundedly from persisted state when that version race occurs.

- [ID: item-3]
  Scope: legacy active-workflow migration backfill
  Resolution: the snapshot migration prefers an existing workflow annotation, then derives a legacy workflow's affiliation from its associated Issue scalar. The regression spec covers a linked workflow with no legacy `epicId` annotation.

- [ID: item-4]
  Scope: terminal target in batch link
  Resolution: the membership transaction stages the target only when it creates the active membership. For a terminal-only target it stages the resolver's retained active owner. The regression spec verifies that a done-epic link preserves the existing active snapshot.

## Verification

- `dotnet test packages/server/tests/Mohist.Server.SpecTests/Mohist.Server.SpecTests.csproj --no-restore -p:SkipWebBuild=true --filter 'FullyQualifiedName~EpicAffiliationLineageSpecs|FullyQualifiedName~EpicBatchMembershipSpecs|FullyQualifiedName~EpicRecoverySpecs|FullyQualifiedName~WorkflowRunStoreSpecs|FullyQualifiedName~BackfillIssueEpicAffiliationMigrationSpecs'` passed: 36 tests.
- `npm test` passed.
- `git diff --check` passed.

<promise>PASS</promise>
