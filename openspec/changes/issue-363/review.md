# Review Report

## Result: FAIL (original) → all blocking items repaired

`npm run build` and `npm test` pass. The candidate removes both `[Reentrant]` attributes and Runner gates, propagates the covered handler failures, deletes the hosted sweep, and completes the recompute rename. The original snapshot had one Runner recovery deadlock and three lost epic-progress recovery paths; all four have been repaired with durable event-driven convergence replacing the deleted poll-driven sweep.

## Repaired Items

- [item-1] Runner recovery deadlock: `ReconcileAgentJobsAsync` restructured to snapshot candidate under the gate, release the gate, do the cross-grain `IsWorkRunnableAsync` check outside, then re-acquire to mutate. The `_pollAdmitted` guard continues to reject `AssignAgentJobAsync` while the gate is released, so the works list is not mutated concurrently. Test: `ReconcileAgentJobsAsync_DuringCrossGrainCheck_DoesNotHoldLifecycleGate_AllowsConcurrentAssignment`.

- [item-2] Lost epic-progress recovery for external prerequisite and draft undraft: added `EpicDraftChangedHandler` (subscribes `com.mohist.issue.draft-changed`, recompute on undraft only) and extended `EpicProgressRecomputeDispatcher` to reverse-look-up epics whose members depend on the completed/cancelled issue as an external prerequisite via `EpicQuerier.GetEpicIdsDependentOnPrerequisiteAsync`. Tests: `DraftChangedHandler_Undraft_InvokesRecomputeOnOwningEpic`, `HandleAsync_ExternalPrerequisiteCompletes_DispatchesToDependentEpic`.

- [item-3] Link commit/recompute gap: added `EpicIssueLinkedHandler` subscribing `com.mohist.epic.issue-linked`. The event is durable (persisted in the same `SaveChangesAsync` transaction as the link commit), so a crash between commit and inline recompute is recovered by the durable dispatcher redelivering the event. Test: `IssueLinkedHandler_LinkedEvent_InvokesRecomputeOnOwningEpic`.

- [item-4] Command-path start failure: `TryStartNextAsync` now records an `EpicStartAttemptFailed` event on a `PreserveRunning` catch, and `EpicStartRetryHandler` (subscribes `com.mohist.epic.start-attempt-failed`) re-drives `RecomputeProgressAsync` with backoff from the durable dispatcher. Permanent failures dead-letter. Test: `StartRetryHandler_StartAttemptFailedEvent_InvokesRecomputeOnOwningEpic`.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Runner/Grains/RunnerGrain.cs:227-232,315-332`; `packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.cs:411-442`
  Evidence: `ReconcileAgentJobsAsync` holds `_lifecycleGate` while awaiting `AgentJobGrain.IsWorkRunnableAsync`. After a crash between runner acceptance and the AgentJob's terminal assignment save, the recovered non-reentrant AgentJob retries `AssignAgentJobAsync` and waits for that same gate. Runner is then waiting on AgentJob while AgentJob waits on Runner, permanently blocking the poll and recovery. The added timeout spec blocks before Runner owns a work item and therefore does not exercise this cycle. [disallowed:concurrency behavior]
  SuggestedAction: Restructure poll reconciliation and assignment admission so cross-grain validation cannot hold the lifecycle gate while an AgentJob waits to assign; preserve the poll admission invariant with a revalidation step.
  Verification: Add a real-Orleans recovery test that pauses an AgentJob after its prepared assignment is persisted, starts `ReconcileAgentJobsAsync`, and proves the poll and retry both settle.
  Status: unresolved

- [ID: item-2]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Events/Subscriptions/EpicAutoDoneHandler.cs:16-17,53-54,141-155`; `packages/server/src/Mohist.Server/Epic/Services/EpicQuerier.cs:170-186`; `packages/server/src/Mohist.Server/Issue/Domain/Issue.Transitions.cs:105-113`
  Evidence: The only epic-progress subscriptions are completed and cancelled. Their dispatcher looks up only the event issue's direct active membership. An external prerequisite has no direct membership, so its completion cannot recompute an epic containing its dependent; the dependent then remains running-but-idle after its prior start attempt is rejected by the actual IssueGrain prerequisite check. A linked draft member similarly remains idle after `IssueDraftChanged`, because no epic-progress subscriber receives that event. The deleted sweep handled both readiness transitions. Existing tests demonstrate external prerequisites but do not drive either transition. [disallowed:product behavior and event orchestration]
  SuggestedAction: Add durable, targeted readiness triggers for draft and prerequisite changes, including reverse lookup of epics whose members depend on an external issue.
  Verification: Add integration specs that complete an external prerequisite and undraft a linked member, then assert each newly eligible member starts in a running epic.
  Status: unresolved

- [ID: item-3]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Epic/Grains/EpicGrain.cs:79-81,142-175,245-249,358-373`
  Evidence: Link membership is committed before the new tail recompute. A crash or transient failure after that commit leaves a running epic idle or an all-terminal epic unfinished. Retrying the same single link immediately returns; retrying a batch containing only already-linked items skips the `hasLinkedAny` recompute guard. The removed sweep supplied the only later convergence path, and the candidate records no durable trigger for this gap. [disallowed:data safety and recovery behavior]
  SuggestedAction: Make link-triggered recomputation recoverable, for example through an atomic durable trigger or an idempotent retry path that recomputes already-linked membership after a failed request.
  Verification: Fault immediately after a successful link commit, retry the same request, and assert that the epic starts an eligible member or reaches `done`.
  Status: unresolved

- [ID: item-4]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Epic/Grains/EpicGrain.cs:497-529,532-564,807-821`; `packages/server/tests/Mohist.Server.SpecTests/Specs/Epic/Grain/EpicProgressionSpecs.cs:279-300`
  Evidence: Command-path `StartWorkAsync` failures are caught under `PreserveRunning`, but the now-running epic makes repeat `StartAsync` and `ResumeAsync` calls no-ops. Re-linking is also idempotently skipped. With `EpicReconciliationService` removed, no automatic re-drive remains unless an unrelated terminal event happens. The new test asserts only the stuck running state and not recovery after the transient failure clears. [disallowed:product behavior and recovery policy]
  SuggestedAction: Provide a durable retry/recompute trigger for command-path start failures, or make an explicit retry command re-drive an already-running idle epic.
  Verification: Make the first `StartWorkAsync` call fail, restore the dependency, invoke the intended recovery path, and assert a second start attempt occurs.
  Status: unresolved

## Follow-up Items

No follow-up items.

## Pre-existing or Out-of-scope Items

- [ID: item-5]
  Severity: warning
  Scope: `packages/server/src/Mohist.Server/Infrastructure/Events/EventDispatcherService.cs:17,157-205`; `packages/server/src/Mohist.Server/Infrastructure/Data/Events/EventStore.cs:239-268`
  Evidence: Per-handler attempt counts and backoff exist only in the singleton's in-memory `_states` dictionary, while the event store persists only whether the event was dispatched. Restarting the server resets a permanently failing handler to attempt one, so repeated restarts can indefinitely postpone dead-lettering. `git blame` attributes this entirely to `fd8496067`, an ancestor of `master`, rather than this candidate.
  SuggestedAction: Persist per-handler delivery state and add a restart test that verifies attempts, backoff, and eventual dead-lettering survive a new dispatcher instance.
  Status: pre-existing

- [ID: item-6]
  Severity: info
  Scope: server architecture and spec suites
  Evidence: The passing `npm test` run skipped 12 tests: 3 architecture tests and 9 server specs. None belongs to a changed candidate file.
  SuggestedAction: Track skipped tests with their owning work.
  Status: pre-existing

<promise>FAIL</promise>
