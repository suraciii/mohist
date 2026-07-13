# Review Report

## Result: FAIL

`npm run build` and `npm test` pass, and the acceptance surfaces for the attribute removal, handler propagation, epic sweep deletion, rename, link-time recompute, and new characteristic specs are present. The post-repair Runner implementation still permits unsafe interleavings after the write gate was removed.

## Repaired Items

No repairs were made during this review.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Runner/Grains/RunnerGrain.cs:216-272,346-369,681-684`
  Evidence: `AssignAgentJobAsync` is now `[AlwaysInterleave]` and persists while holding `_lifecycleGate` (lines 225-271). `ReportAgentJobResultAsync` awaits `IAgentJobGrain.ReportResultAsync` and then removes work and calls `PersistAsync` without that gate (lines 353-361). An assignment can therefore interleave with an in-flight report and issue a concurrent or stale `WriteStateAsync` after `_worksStateWriteGate` was removed (line 683). This can lose an accepted work item from persistent state or make reactivation disagree with the runner-work ledger. The new concurrency spec only races assignments with assignments; it does not cover report plus assignment. [disallowed:data safety and concurrency behavior]
  SuggestedAction: Serialize report-side ledger mutation with interleavable assignment writes without holding the gate across the AgentJob call; re-check tracked state after acquiring the mutation guard. Add a real-Orleans report/assignment race that verifies persistent state and the runner-work ledger after reactivation.
  Verification: `dotnet test Mohist.sln -p:SkipWebBuild=true --filter "FullyQualifiedName~RunnerGrainConcurrencySpecs|FullyQualifiedName~RunnerWorkLedgerSpecs"`
  Status: unresolved

- [ID: item-2]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Runner/Grains/RunnerGrain.cs:216-282`
  Evidence: `AssignAgentJobAsync` checks `_pollAdmitted` before awaiting `_lifecycleGate` (lines 222-225), while `TryBeginPollAsync` and `EndPollAsync` set and clear that same flag without the gate (lines 275-290). Because assignment is `[AlwaysInterleave]`, a poll can be admitted after the initial false read but before assignment validates capacity and persists the new work. The assignment then succeeds during the active reconciliation round, defeating the required `runner-reconciling` rejection and allowing a poll claim plus agent assignment to oversubscribe capacity. Existing coverage only starts the poll before assignment (`AgentJobOwnerKindSpecs.cs:162-180`) and cannot exercise this race. [disallowed:concurrency behavior]
  SuggestedAction: Coordinate poll admission and the assignment admission decision through the same short-lived synchronization boundary, including a post-acquisition `_pollAdmitted` check; do not reintroduce a gate held across the full poll sequence. Add a deterministic characteristic test that admits a poll after assignment has started but before it commits, then verifies rejection and no persisted work.
  Verification: `dotnet test Mohist.sln -p:SkipWebBuild=true --filter "FullyQualifiedName~RunnerGrainConcurrencySpecs|FullyQualifiedName~AgentJobOwnerKindSpecs"`
  Status: unresolved

## Follow-up Items

No follow-up items.

## Pre-existing or Out-of-scope Items

- [ID: item-3]
  Severity: info
  Scope: server architecture and spec suites
  Evidence: The passing `npm test` run skipped 12 tests: 3 architecture tests and 9 server specs. None belongs to a file changed by this candidate.
  SuggestedAction: Track skipped tests with their owning work.
  Status: pre-existing

<promise>FAIL</promise>
