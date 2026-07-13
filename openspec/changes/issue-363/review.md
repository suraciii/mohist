# Review Report

## Result: FAIL

Acceptance evidence: `WorkflowGrain` and `RunnerGrain` no longer carry `[Reentrant]` (`packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.cs:21`, `packages/server/src/Mohist.Server/Runner/Grains/RunnerGrain.cs:32`); the Runner write and poll-admission gates are absent while the interface declares the required `[AlwaysInterleave]` methods (`IRunnerGrain.cs:20-49`). The hosted sweep is deleted, legacy epic reconcile names have zero source/test matches, handler failures propagate, and recovery/readiness subscriptions plus atomic link events are present (`EpicGrain.cs:140-145`, `EpicAutoDoneHandler.cs:85-215`).

Verification passed: `npm run build` (0 warnings, 0 errors); `npm test` (2,891 server specs, 1,390 server unit tests, 24 architecture tests, 875 CLI tests, 4,596 web tests, and 1,007 runner tests); and `dotnet test --no-build packages/server/tests/Mohist.Server.SpecTests/Mohist.Server.SpecTests.csproj --filter "FullyQualifiedName~RunnerGrainConcurrencySpecs"` (9 passed).

## Repaired Items

No repairs were made.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Epic/Grains/EpicGrain.cs:564-575,824-842`
  Evidence: `ResumeAsync` commits the paused-to-running transition at line 569 before calling `RecomputeProgressInternalAsync`. If `StartWorkAsync` then fails, the `PreserveRunning` catch writes `EpicStartAttemptFailed` only in the later save at lines 840-841. An append/save failure at that point leaves a committed running-but-idle epic without the required recovery event. Retrying `ResumeAsync` does not help: it sees an already-running epic and skips recompute (`wasAlreadyRunning`, lines 565 and 572). This violates the required durable command-path retry signal for `ResumeAsync`. [disallowed:data safety and recovery behavior]
  SuggestedAction: Make the resume transition and its recovery signal atomic, or persist an independent durable recovery record before the failed start can strand the already-committed transition.
  Verification: Inject an event-store failure after `StartWorkAsync` fails during resume; assert either the resume transition rolls back or a retriable recovery event remains durable, then verify redelivery advances the issue.
  Status: unresolved

- [ID: item-2]
  Severity: test-gap
  Scope: `packages/server/tests/Mohist.Server.SpecTests/Specs/Runner/Grain/RunnerGrainConcurrencySpecs.cs:262-330`; `packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.cs:434-442`; `packages/server/src/Mohist.Server/Runner/Grains/RunnerGrain.cs:644-691`
  Evidence: The required reciprocal deadlock scenario is not exercised. The test blocks at `AssignmentPreparedAsync` before `TryAssignToRunnerAsync` calls `RunnerGrain.AssignAgentJobAsync`; that ordering is explicit in `AgentJobGrain.cs:434-442`. Runner therefore owns no work when timeout calls `CloseoutLostAsync`, so its agent-job loop has nothing to report. The test permits an independent `runner-unavailable` failure and asserts `runner-lost` ledger closeout only conditionally, leaving the actual Runner-to-AgentJob callback cycle untested. [disallowed:requires non-local deterministic concurrency orchestration]
  SuggestedAction: Establish a runner ledger entry, pause the same agent job on a duplicate/retry assignment immediately before `AssignAgentJobAsync`, trigger timeout, and assert that the interleaved rejection unblocks `ReportResultAsync`, records `runner-lost`, and settles both grains.
  Verification: Run the real-Orleans spec using awaitable test signals and FakeTimeProvider, with assertions for the closeout report, assignment rejection, final runner ledger, and terminal job state.
  Status: unresolved

## Follow-up Items

No follow-up items.

## Pre-existing or Out-of-scope Items

- [ID: item-3]
  Severity: warning
  Scope: `packages/server/src/Mohist.Server/Infrastructure/Events/EventDispatcherService.cs:17,157-205`
  Evidence: Per-handler attempts and backoff are stored only in the process-local `_states` dictionary. Restarting the dispatcher resets a failing event to attempt one, so repeated restarts can indefinitely defer dead-lettering. This file is unchanged from `master`; `git blame` attributes the state to `fd84960679`.
  SuggestedAction: Persist per-handler delivery state and add a dispatcher-restart spec. The new epic retry events inherit this existing limitation.
  Status: pre-existing

- [ID: item-4]
  Severity: info
  Scope: server architecture and spec suites
  Evidence: The passing full suite skipped 12 unrelated tests: 3 architecture tests and 9 server specs.
  SuggestedAction: Track skipped tests with their owning work.
  Status: pre-existing

<promise>FAIL</promise>
