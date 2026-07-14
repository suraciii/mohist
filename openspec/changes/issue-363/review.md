# Review Report

## Result: FAIL

The structural acceptance criteria are satisfied: both authority grains no longer use `[Reentrant]`, the Runner write and poll-admission gates are absent, the narrow Runner interleaving contract is present, the covered handlers propagate setup/router failures, the epic sweep and legacy epic-reconcile identifiers are removed, and the new Orleans concurrency specs run. `npm run build`, `npm test`, and `git diff --check master...HEAD` pass. The candidate still loses recovery signals and can select work blocked by a cancelled prerequisite, so the post-build snapshot fails the epic-progress requirements.

## Repaired Items

None. The open findings change recovery behavior or prerequisite semantics and are not eligible for direct review repair.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Epic/Grains/EpicGrain.cs:554-559,589-595,839-857,1101-1124`
  Evidence: `StartAsync` and `ResumeAsync` commit the `running` transition before attempting child start. A crash after that commit, or a start failure followed by `EpicStartAttemptFailed` append failure, leaves the epic running-but-idle with no durable convergence signal. The recovery event is sent through the post-commit overload, which catches append exceptions; no handler subscribes to the best-effort `EpicStatusChanged` event. Retrying Start/Resume while already running is a no-op. `EpicProgressionSpecs.cs:339-368` explicitly accepts this lost-recovery outcome, contrary to `specs/epic-progress-recompute/spec.md:108-110,172-177`. [disallowed:data safety and recovery-protocol change]
  SuggestedAction: Persist recovery intent with the running transition in the caller DbContext and retain a durable handler that re-drives progress after a crash before child start. Persist `EpicStartAttemptFailed` atomically when the command path catches a child-start failure.
  Verification: With a real `EventStore`, simulate failure after the running transition and a `StartWorkAsync` failure for both Start and Resume. Assert an undelivered recovery event exists and dispatcher delivery converges the epic or dead-letters the permanent failure.
  Status: unresolved

- [ID: item-2]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Epic/Grains/EpicGrain.cs:964-995`; `packages/server/src/Mohist.Server/Issue/Services/IssueInfo.cs:60-76`; `packages/server/src/Mohist.Server/Issue/Grains/IssueGrain.cs:622-631`
  Evidence: Epic candidate construction omits cancelled member and external prerequisites from `undeliveredPrereqNumbers`, so a dependent is marked startable after its prerequisite is cancelled. The authoritative Issue prerequisite summary defines completion as `Status == Done`, so `StartWorkAsync` rejects the same dependent. A cancelled linked prerequisite therefore causes terminal-event recompute to retry and eventually dead-letter while the epic remains blocked. No regression test covers a linked dependent whose prerequisite is cancelled. [disallowed:product behavior and prerequisite-semantics change]
  SuggestedAction: Treat only Done prerequisites as delivered in both EpicGrain prerequisite loops, then add a regression spec for cancellation of a linked prerequisite.
  Verification: Cancel an in-progress linked prerequisite of another linked backlog issue. Assert recompute does not call StartWorkAsync for the dependent, does not dead-letter the cancellation event, and leaves the dependent blocked.
  Status: unresolved

- [ID: item-3]
  Severity: test-gap
  Scope: `packages/server/tests/Mohist.Server.SpecTests/Specs/Events/Epic/EpicAutoDoneHandlerSpecs.cs:583-878`; `packages/server/tests/Mohist.Server.SpecTests/Support/RecordingEventStore.cs:25-33`
  Evidence: New recovery handlers are tested by direct invocation with fake grain factories. `RecordingEventStore.AppendAsync(MohistDbContext, ...)` records immediately in memory and does not stage an event on the supplied DbContext, so these tests cannot prove transaction rollback, assembly-registered subscription dispatch, or eventual recovery from a skipped inline recompute.
  SuggestedAction: Add in-process specs using `EventStore`, registered subscriptions, and `EventDispatcherService` for link convergence and command-path start recovery.
  Verification: Commit a recovery event through the real DbContext, run dispatcher delivery, and assert the persisted epic and linked issue converge.
  Status: open

## Follow-up Items

None.

## Pre-existing or Out-of-scope Items

- [ID: item-4]
  Severity: warning
  Scope: `packages/server/src/Mohist.Server/Infrastructure/Events/EventDispatcherService.cs:17,157-205`
  Evidence: Per-event handler attempts and backoff live only in the process-local `_states` dictionary. Restarting the dispatcher resets a poison event's retry budget, permitting repeated restarts to defer dead-lettering indefinitely. This predates the candidate but is the dispatcher behavior used by the new propagated failures and intended recovery events.
  SuggestedAction: Persist per-event/per-handler delivery state and cover retry-budget exhaustion across a dispatcher restart.
  Status: pre-existing

- [ID: item-5]
  Severity: info
  Scope: server test suites
  Evidence: The completed `npm test` run skipped 12 existing server tests: 3 architecture tests and 9 server specs.
  SuggestedAction: Track the skipped tests with their owning work.
  Status: pre-existing

<promise>FAIL</promise>
