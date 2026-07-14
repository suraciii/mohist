# Review Report

## Result: FAIL

The requested removals and rename are present: `WorkflowGrain` and `RunnerGrain` no longer carry `[Reentrant]`; the Runner write and poll-admission gates are gone; `IRunnerGrain` marks `GetRuntimeStateAsync`, `GetSlotsAsync`, and `AssignAgentJobAsync` with `[AlwaysInterleave]`; the hosted epic sweep is deleted; and no legacy epic reconcile identifiers remain under `packages/server/src` or `packages/server/tests`. The covered handlers now propagate setup failures, while Hermes delivery remains best-effort. `npm run build` passed with zero warnings/errors and `npm test` passed (875 CLI, 1,390 server unit, 24 server architecture, 2,896 server spec, 4,596 web, and 1,007 runner tests). The unresolved items below invalidate the post-repair candidate.

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: formatting
  Evidence: `EpicPrerequisiteRemovedHandler` had XML-doc content and a closing `</summary>` without an opening `<summary>` block.
  Verification: `npm run build`; `git diff --check`
  Status: resolved

## Blocking Items

- [ID: item-2]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Epic/Grains/EpicGrain.cs:554-559,593-594,843-865,1087-1132`
  Evidence: Command-path start failures record `EpicStartAttemptFailed` only through the post-commit, exception-swallowing overload. A failed append therefore leaves `StartAsync` or `ResumeAsync` with a committed running-but-idle epic and no durable recovery trigger after the sweep was removed. The running status-event backstop is also appended through that same best-effort path. `EpicProgressionSpecs.cs:339-371` explicitly accepts this lost-event state, despite `specs/epic-progress-recompute/spec.md:108-110,172-176` requiring a durable, atomic recovery trigger. [disallowed:data safety and recovery-protocol change]
  SuggestedAction: Make command-path recovery intent durable before reporting success, using a transactional outbox/state transition or a recovery protocol that cannot lose both the running transition and retry trigger.
  Verification: Fail `StartWorkAsync` and the first recovery-event append for both start and resume, then assert a persisted undelivered event remains and dispatcher delivery converges the epic.
  Status: unresolved

- [ID: item-3]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Events/Subscriptions/EpicAutoDoneHandler.cs:81-82`; `packages/server/src/Mohist.Server/Epic/Grains/EpicGrain.cs:1002`; `packages/server/src/Mohist.Server/Issue/Services/IssueInfo.cs:60-76`
  Evidence: `EpicCancelledHandler` reverse-looks up epics blocked on an external prerequisite. Epic recompute treats a cancelled prerequisite as delivered, selects the dependent, then `IssueGrain.StartWorkAsync` correctly rejects it because the authoritative prerequisite summary considers only `Done` completed. In `Propagate` mode this turns a valid cancellation event into retries and eventual dead-lettering while the dependent remains blocked. The spec requires external-prerequisite recompute for completion, not cancellation.
  SuggestedAction: Do not reverse-look up external dependents on cancellation, or make cancellation satisfy prerequisites consistently across Issue and Epic semantics.
  Verification: Cancel an external prerequisite of a running epic member and assert the member remains blocked without a failed or dead-lettered cancellation delivery.
  Status: unresolved

- [ID: item-4]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Runner/Services/DispatchService.cs:64-85,145-155`; `packages/server/src/Mohist.Server/Runner/Grains/RunnerGrain.cs:158-176`
  Evidence: An admitted poll snapshots non-null `RunnerInfo`, then `UnregisterAsync` can clear registration and finish closeout before that poll reaches `AddAssignablePendingDispatchesAsync`. The stale poll still uses its captured `info.ProjectId` to assign and claim fresh workflow work. The new work is assigned to an unregistered runner after its only closeout has already run, leaving it stranded. [disallowed:data safety and cross-aggregate scheduling behavior]
  SuggestedAction: Carry an atomic availability/generation token through the poll claim window, or revalidate live runner registration immediately before every new workflow assignment.
  Verification: Pause a poll after `GetInfoAsync`, unregister and await closeout, then resume it. Assert no workflow is assigned, claimed, or returned for that runner.
  Status: unresolved

- [ID: item-5]
  Severity: warning
  Scope: `packages/server/src/Mohist.Server/Runner/Services/DispatchService.cs:42-48,77-85`; `packages/server/src/Mohist.Server/Runner/Grains/RunnerGrain.cs:583-599`
  Evidence: `TryBeginPollAsync` snapshots slots. A concurrent `UpdateAsync(1)` can complete after a poll was admitted with two slots, while that poll still claims two workflows. This violates the documented invariant that slots bound all concurrently executing work (`design/workflow/scheduling.md:112-115`). The new test only verifies the next admission sees the new capacity (`RunnerDefinitionStateSpecs.cs:171-193`).
  SuggestedAction: Either linearize an admitted poll through its claims or re-read/revalidate capacity before each claim; document the chosen linearization semantics.
  Verification: Pause a two-slot poll, reduce capacity to one, resume with two ready workflows, and assert no more than one is claimed.
  Status: unresolved

- [ID: item-6]
  Severity: test-gap
  Scope: `packages/server/tests/Mohist.Server.SpecTests/Specs/Runner/Grain/RunnerGrainConcurrencySpecs.cs:434-502`; `packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.cs:141-147`
  Evidence: The claimed reconciliation reciprocal-deadlock spec invokes a direct Runner assignment while `IsWorkRunnableAsync` is a synchronous read. It never creates an AgentJob turn blocked at `AssignAgentJobAsync`, so it would pass even if reconciliation held `_lifecycleGate` across the cross-grain call. The suite also has no test timeout configured (`xunit.runner.json:1-4`), so a real deadlock would hang instead of failing deterministically.
  SuggestedAction: Block a real AgentJob retry at `AssignAgentJobAsync`, then reconcile that job's existing work using awaitable test signals and assert both calls settle.
  Verification: Run the real-Orleans spec with deterministic signals, asserting the interleaved rejection frees the AgentJob and preserves the Runner ledger.
  Status: unresolved

- [ID: item-7]
  Severity: test-gap
  Scope: `packages/server/tests/Mohist.Server.SpecTests/Specs/Events/Epic/EpicAutoDoneHandlerSpecs.cs:583-845`; `packages/server/tests/Mohist.Server.SpecTests/Support/RecordingEventStore.cs:25-33`
  Evidence: New recovery handlers are tested only by direct invocation with fake grains. The fake's scoped `AppendAsync` merely records in memory instead of adding to the supplied DbContext, so the tests do not prove a real atomic event row is committed, discovered through assembly-registered subscriptions, and dispatched to converge a skipped inline recompute.
  SuggestedAction: Add in-process specs with real `EventStore`, subscriptions, and `EventDispatcherService` for link convergence and command-path start recovery.
  Verification: Commit the recovery event through the real DbContext, omit the inline recompute, run dispatcher delivery, and assert the persisted epic and issue converge.
  Status: unresolved

- [ID: item-8]
  Severity: test-gap
  Scope: `packages/server/tests/Mohist.Server.SpecTests/Specs/Workflow/Grain/WorkflowGrainConcurrencySpecs.cs:92-126,167-205`; `packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.cs:135-149`
  Evidence: A serialized `StopAsync` is valid after either Pending or Paused and must leave the run Stopped, yet `ConcurrentPauseAndStop_FromPending_SettlesToStopped` accepts Paused and catches all operation failures. The independent-workflow case discards every operation result. These tests can pass when stop never succeeds, so they do not establish the required characteristic behavior.
  SuggestedAction: Require one successful stop and a final Stopped state while allowing only order-valid pause rejections.
  Verification: Break or reject `StopAsync` in the test double path and confirm the spec fails; run the corrected in-process concurrency spec repeatedly.
  Status: unresolved

- [ID: item-9]
  Severity: minor
  Scope: `packages/server/tests/Mohist.Server.SpecTests/Specs/Workflow/Grain/WorkflowGrainConcurrencySpecs.cs:178`; `packages/server/tests/Mohist.Server.SpecTests/Specs/Workflow/WorkflowGrainSpecs.cs:126-139`
  Evidence: The new characteristic specs call `TestInput`, which stamps workflow metadata with `DateTimeOffset.UtcNow`. The issue specification requires fake/injectable time and no wall-clock use in these concurrency specs.
  SuggestedAction: Build the concurrency-test input from the fixture's `FakeTimeProvider`.
  Verification: Search the concurrency spec's setup path for `UtcNow`; run its focused spec file with the fixture clock fixed.
  Status: unresolved

- [ID: item-10]
  Severity: warning
  Scope: `design/workflow/scheduling.md:114`; `packages/server/src/Mohist.Server/Runner/Grains/RunnerGrain.cs:283-310,583-599`
  Evidence: The scheduling design still says the poll-admission gate spans reconciliation and `UpdateAsync` waits for it. The candidate removes that gate and permits updates during an admitted poll. This leaves the architecture specification inconsistent with the implemented capacity semantics and obscures the race in item-5. [disallowed:architecture specification and scheduling-semantics decision]
  SuggestedAction: Update the scheduling specification with the intended poll/update linearization and its required safety boundary once the behavior is corrected.
  Verification: Review the revised specification against an interleaving spec covering unregister and capacity reduction during an admitted poll.
  Status: unresolved

## Follow-up Items

No follow-up items.

## Pre-existing or Out-of-scope Items

- [ID: item-11]
  Severity: warning
  Scope: `packages/server/src/Mohist.Server/Infrastructure/Events/EventDispatcherService.cs:17,157-205`
  Evidence: Handler attempts and backoff are held only in the process-local `_states` dictionary. Restarting the dispatcher resets a poison event to its first attempt, so repeated restarts can defer dead-lettering indefinitely. This implementation predates the candidate, but issue 363 depends on it for the newly propagated handler and epic recovery failures.
  SuggestedAction: Persist per-event/per-handler delivery state and add a dispatcher-restart exhaustion spec.
  Status: pre-existing

- [ID: item-12]
  Severity: info
  Scope: server test suites
  Evidence: The passing full test run skipped 12 unrelated server tests: 3 architecture tests and 9 server specs.
  SuggestedAction: Track skipped tests with their owning work.
  Status: pre-existing

<promise>FAIL</promise>
