# Review Report

## Result: FAIL

The required removals and rename are present: `WorkflowGrain` and `RunnerGrain` have no `[Reentrant]`, the Runner write and poll-admission gates are gone, the required `IRunnerGrain` methods are `[AlwaysInterleave]`, the hosted epic sweep is deleted, and no legacy epic reconcile identifiers remain under `packages/server/src` or `packages/server/tests`. Handler propagation and the new feature specs are also present. `npm run build` passed with 0 warnings and 0 errors; `npm test` passed (875 CLI, 1,390 server unit, 24 architecture, 2,892 server spec, 4,596 web, and 1,007 runner tests). The unresolved items below invalidate the candidate.

## Repaired Items

No repairs were made.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Epic/Grains/EpicGrain.cs:535-540,572-577`
  Evidence: `StartAsync` and `ResumeAsync` now invoke `IIssueGrain.StartWorkAsync` before committing the epic transition. `IssueGrain.StartWorkAsync` durably starts the workflow and saves the issue before it returns (`packages/server/src/Mohist.Server/Issue/Grains/IssueGrain.cs:216-249`). A subsequent epic `SaveChangesAsync` failure rolls back the epic status and recovery event while leaving the child issue and workflow active. The next issue-start event does not repair this parent state, so an `idle` or `paused` epic can have active child work. [disallowed:data safety and cross-aggregate behavior]
  SuggestedAction: Preserve the committed parent transition before starting a child, or introduce a durable recovery/compensation protocol that converges both aggregates when the parent commit fails.
  Verification: Inject an epic `SaveChangesAsync` failure after a successful real `StartWorkAsync` in both start and resume paths; assert there is neither orphaned child work nor an unrecoverable parent state.
  Status: unresolved

- [ID: item-2]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Runner/Grains/RunnerGrain.cs:158-176`; `packages/server/src/Mohist.Server/Runner/Services/DispatchService.cs:64-85`
  Evidence: Removing `_pollAdmissionGate` lets `UnregisterAsync` clear the runner while an admitted poll retains its earlier non-null `RunnerInfo`. That poll can then use the stale `info.ProjectId` to assign and claim new workflow work after closeout has scanned current assignments. There is no online/generation recheck before `AddAssignablePendingDispatchesAsync`; the newly claimed work is left assigned to an unregistered runner with no closeout path. `RunnerDefinitionStateSpecs` tests only direct unregister during admission, not this full poll interleaving. [disallowed:product behavior and data safety]
  SuggestedAction: Establish an atomic poll validity boundary through workflow claim, or revalidate runner presence immediately before every new workflow assignment and reject claims after unregister.
  Verification: Pause `PollAsync` after `GetInfoAsync`, unregister the runner and complete closeout, then resume the poll. Assert no workflow is assigned, claimed, or returned for that runner.
  Status: unresolved

- [ID: item-3]
  Severity: warning
  Scope: `packages/server/src/Mohist.Server/Events/Subscriptions/EpicAutoDoneHandler.cs:81`; `packages/server/src/Mohist.Server/Epic/Grains/EpicGrain.cs:979-982`
  Evidence: `EpicCancelledHandler` reverse-looks up external-prerequisite dependents, but epic-side readiness treats a cancelled prerequisite as delivered. The authoritative issue start path treats only `Done` as completed (`packages/server/src/Mohist.Server/Issue/Services/IssueInfo.cs:60-69`, `packages/server/src/Mohist.Server/Issue/Grains/IssueGrain.cs:622-631`). Recompute therefore selects the still-blocked dependent, `StartWorkAsync` rejects it, and the valid cancellation event is retried then dead-lettered under `Propagate` mode.
  SuggestedAction: Limit external-prerequisite lookup to completion events, or make cancellation satisfy prerequisites consistently in the Issue and Epic domains.
  Verification: Create a running epic whose member depends on an external issue, cancel that external issue, and assert the member remains blocked without a failed/ded-lettered terminal-event delivery.
  Status: unresolved

- [ID: item-4]
  Severity: test-gap
  Scope: `packages/server/tests/Mohist.Server.SpecTests/Specs/Runner/Grain/RunnerGrainConcurrencySpecs.cs:434-502`
  Evidence: `ReconcileAgentJobsAsync_DuringCrossGrainCheck_DoesNotHoldLifecycleGate_AllowsConcurrentAssignment` does not create the reciprocal wait described in its comment. The inspected `AgentJobGrain.IsWorkRunnableAsync` is a synchronous `Task.FromResult` read (`packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.cs:141-147`), and the test's second assignment is sent directly to Runner rather than from an AgentJob turn awaiting `AssignAgentJobAsync`. It would pass if `ReconcileAgentJobsAsync` still held `_lifecycleGate` across `IsWorkRunnableAsync`. The suite has no configured per-test timeout (`packages/server/tests/Mohist.Server.SpecTests/xunit.runner.json:1-4`), so a genuine deadlock would hang instead of failing deterministically.
  SuggestedAction: Block a real AgentJob retry at `RunnerGrain.AssignAgentJobAsync`, then start reconciliation of that same job's existing runner work. Assert the interleaved rejection frees the AgentJob and lets reconciliation settle.
  Verification: Run that real-Orleans spec with awaitable test signals, asserting both calls settle and the runner work ledger remains consistent.
  Status: unresolved

- [ID: item-5]
  Severity: test-gap
  Scope: epic recovery event persistence and subscriptions
  Evidence: The new `EpicIssueLinkedHandler`, `EpicStartRetryHandler`, draft, prerequisite-removal, and external-prerequisite cases are tested by direct handler invocation with fake grains (`packages/server/tests/Mohist.Server.SpecTests/Specs/Events/Epic/EpicAutoDoneHandlerSpecs.cs:583-773`). The atomic append tests use `RecordingEventStore`, whose `AppendAsync(MohistDbContext, ...)` only appends to an in-memory list rather than the supplied DbContext (`packages/server/tests/Mohist.Server.SpecTests/Support/RecordingEventStore.cs:25-33`). No test proves that a real event-store row is atomically committed, discovered by the registered durable dispatcher, and converges the epic after inline recompute is skipped.
  SuggestedAction: Add in-process specs using real `EventStore`, registered subscriptions, and the dispatcher for link convergence and command-path start-retry recovery.
  Verification: Commit a link or start-failure recovery event, simulate the missing inline recompute, run dispatcher delivery, and assert the persisted epic and issue reach the expected final state.
  Status: unresolved

## Follow-up Items

No follow-up items.

## Pre-existing or Out-of-scope Items

- [ID: item-6]
  Severity: warning
  Scope: `packages/server/src/Mohist.Server/Infrastructure/Events/EventDispatcherService.cs:17,157-205`
  Evidence: Per-handler attempts and backoff are process-local in `_states`. A dispatcher restart resets a failing recovery event to attempt one, so repeated restarts can indefinitely defer the required dead-letter outcome. This file is unchanged from `master`; blame attributes the state to `fd84960679`.
  SuggestedAction: Persist delivery state per event and handler, then add a dispatcher-restart exhaustion spec.
  Status: pre-existing

- [ID: item-7]
  Severity: info
  Scope: server test suites
  Evidence: The passing full suite skipped 12 unrelated server tests: 3 architecture tests and 9 server specs.
  SuggestedAction: Track skipped tests with their owning work.
  Status: pre-existing

<promise>FAIL</promise>
