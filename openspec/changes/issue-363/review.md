# Review Report

## Result: FAIL

The post-repair implementation meets the code-level acceptance criteria: `WorkflowGrain` and `RunnerGrain` have no `[Reentrant]` marker; `RunnerGrain` has no write or poll-admission semaphore and exposes the three intended `[AlwaysInterleave]` operations; the relevant handlers propagate setup failures; the hosted sweep and its registration are gone; and the epic recompute chain is renamed with no obsolete references under `packages/server`. No new security, persistence-schema, or external public-contract issue was found. `npm run build` and `npm test` both passed. The verdict is FAIL because required high-risk behavior remains unverified in the test suite.

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: documentation
  Evidence: `WorkflowStageLockReleaseHandler` incorrectly said that the handler awaited the dispatcher invocation. The dispatcher awaits the handler; the handler awaits the target grain.
  Verification: `npm run build`; `npm test`
  Status: resolved

- [ID: item-2]
  Severity: info
  Scope: formatting
  Evidence: `WorkflowGrainConcurrencySpecs.cs` lacked a final newline.
  Verification: `git diff --check`; `npm run build`; `npm test`
  Status: resolved

## Blocking Items

- [ID: item-3]
  Severity: test-gap
  Scope: `packages/server/src/Mohist.Server/Epic/Grains/EpicGrain.cs:358`, `packages/server/tests/Mohist.Server.SpecTests/Specs/Epic/Grain/EpicBatchMembershipSpecs.cs:540`
  Evidence: `LinkIssuesAsync` now invokes the new `RecomputeProgressInternalAsync` branch after a non-wake batch link, but the batch specs only cover the pre-existing wake-from-done tail call. There is no behavior assertion for a startable member batch-linked to a running epic or an all-terminal batch-linked to an idle epic. Those are explicit sweep-replacement scenarios in the acceptance spec.
  SuggestedAction: Add batch-link specs proving that the running case calls `StartWorkAsync` once and that the all-terminal idle case transitions to `done` and releases active memberships.
  Verification: `dotnet test Mohist.sln -p:SkipWebBuild=true --filter "FullyQualifiedName~EpicBatchMembershipSpecs"`
  Status: open

- [ID: item-4]
  Severity: test-gap
  Scope: `packages/server/src/Mohist.Server/Epic/Grains/EpicGrain.cs:705`, `packages/server/tests/Mohist.Server.SpecTests/Specs/Epic/Grain/EpicProgressionSpecs.cs:286`
  Evidence: The direct grain test proves `StartFailureMode.Propagate` throws, but no test drives an `IssueCompleted` or `IssueCancelled` event through `EpicProgressRecomputeDispatcher` and `EventDispatcherService`. The stated contract is retry then dead-letter for this exact terminal-event start failure; the current dispatcher retry test only covers `AgentSubscriptionDispatchHandler`.
  SuggestedAction: Add a dispatcher-level test with a terminal-event epic handler whose selected `IIssueGrain.StartWorkAsync` throws. Assert retry backoff, dead-letter creation, and source-event settlement after exhaustion.
  Verification: `dotnet test Mohist.sln -p:SkipWebBuild=true --filter "FullyQualifiedName~EpicAutoDoneHandlerSpecs|FullyQualifiedName~EventDispatcherSpecs"`
  Status: open

- [ID: item-5]
  Severity: test-gap
  Scope: `packages/server/tests/Mohist.Server.SpecTests/Specs/Runner/Grain/RunnerGrainConcurrencySpecs.cs:335`
  Evidence: The new Runner concurrency cases inspect live state and the definition store, but none proves that the concurrent assignment/lifecycle result survives runner deactivation and reactivation. This leaves the issue's required in-memory-to-persisted-work-ledger agreement unverified after `_worksStateWriteGate` removal.
  SuggestedAction: Extend the concurrent assignment scenario to deactivate and reactivate the runner, then assert the persisted agent-work ledger and runtime projection have exactly the expected accepted work.
  Verification: `dotnet test Mohist.sln -p:SkipWebBuild=true --filter "FullyQualifiedName~RunnerGrainConcurrencySpecs"`
  Status: open

## Follow-up Items

No follow-up items.

## Pre-existing or Out-of-scope Items

- [ID: item-6]
  Severity: info
  Scope: server architecture and spec test suites
  Evidence: The passing full suite reported 12 skipped tests (3 architecture and 9 server specs). None of their files is part of this candidate.
  SuggestedAction: Track and resolve skipped tests in their owning issues.
  Status: pre-existing

<promise>FAIL</promise>
