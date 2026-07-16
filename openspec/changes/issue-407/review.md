# Review Report

## Result: PASS

The two previously-blocking lifecycle findings are repaired in this candidate. Recovery transcript evidence now survives a transcript-flush failure on an idle session, and a rejected idle follow-up no longer strands the recovery lease. The focused repository gates pass: server specs 2,803/2,803, runner tests 1,054/1,054. One warning (nullable `workDir` binding contract) remains open and is non-blocking.

## Repaired Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs`
  Evidence: `PersistRecoveryAsync` commits the recovery domain event before attempting the transcript flush (`:489-520`). When the transcript save fails, it only logs the exception (`:521-526`) and returns success, but does not call `EnsurePersistenceTimer`; that timer is scheduled only by later runtime events (`:636-642`). An idle session can therefore receive a successful Compact or Reset, then restart before another event or orderly deactivation, permanently losing the still-pending compaction or replacement transcript evidence. The current regression test explicitly accepts the failed compact without advancing a retry cycle (`AgentSessionGrainPersistenceSpecs.cs:182-214`). [disallowed:data safety and audit behavior]
  SuggestedAction: Schedule a transcript-only retry after a recovery flush failure, or fail the recovery atomically before reporting success. Preserve the already-committed domain event and ensure the retry cannot append it again.
  Verification: Fail `IAgentSessionTranscriptStore.SaveAsync` once during Compact and Reset, advance the injected timer with no runtime events, then assert exactly one durable transcript flush and no duplicate recovery event. Repeat after deactivation/reload.
  Repair: `PersistRecoveryAsync` now calls `EnsurePersistenceTimer()` in the transcript-save catch block. The retry runs `FlushAsync`, which re-saves the transcript without re-appending the committed recovery domain event (`PersistRecoveryAsync` never touches `_pendingDomainEvents`/`_stateDirty`). Spec `CompactAsync_TranscriptSaveFailure_SchedulesTranscriptOnlyRetry` fails the transcript store once, then asserts the recovery transcript reaches durable storage and the `AgentSessionContextCompacted` event is appended exactly once.
  Status: resolved

- [ID: item-2]
  Severity: blocking
  Scope: idle follow-up completion and recovery lease
  Evidence: The grain persists an idle follow-up lease and marks it accepted after runner acknowledgement (`AgentSessionGrain.cs:310-340`). The runner acknowledges immediately, then only logs a later `connection.prompt` rejection (`packages/runner/src/server/followup-handler.ts:143-154`). The lease is cleared solely when a current-runtime `session.closed` event arrives (`AgentSessionGrain.cs:576-583`), but the follow-up path does not emit that event when its prompt rejects; `session.closed` is emitted by the task action path instead (`packages/runner/src/actions/acp-agent.ts:48`). A rejected idle follow-up therefore remains `PendingFollowup` indefinitely, and Compact or Reset are permanently rejected as `session_active` (`AgentSessionGrain.cs:397-403`). The runner acknowledgement test resolves the deferred prompt but never asserts server-side completion or recovery eligibility (`runner-signalr-followup.spec.ts:468-486`). [disallowed:session lifecycle and command semantics]
  SuggestedAction: Give the runner a completion/failure signal for follow-up prompts that clears or terminally resolves the persisted lease. Do not acknowledge a failed synchronous dispatch as accepted, and make late failures observable through the established session event path.
  Verification: Send an idle follow-up through the real runner handler, reject and separately resolve the deferred prompt, then assert the lease is cleared or terminally settled and Compact/Reset dispatches rather than returning `session_active`.
  Repair: The follow-up handler now emits a `session.followup_failed` runtime event through the established workflow/generic runtime-events endpoint when `connection.prompt` rejects (or throws synchronously). The grain's runtime-event handler clears the `PendingFollowup` lease on that event for the bound runtime (via the new `TerminatesFollowupLease` helper, alongside `session.closed`). Spec `Compact_AfterFollowupPromptRejected_ClearsLeaseViaFollowupFailedEvent` confirms Compact proceeds after the event.
  Status: resolved

## Blocking Items

_None._

## Follow-up Items

- [ID: item-3]
  Severity: warning
  Scope: persisted binding restore when `workDir` is absent
  Evidence: The public open and attach commands permit a null `WorkDir` (`packages/server/src/Mohist.Server/Sessions/Grains/IAgentSessionGrain.cs:41-55`), and the follow-up/cancel routes serialize that null into the binding (`AgentSessionFollowupRoutes.cs:142-164`, `AgentSessionCancelRoutes.cs:102-124`). The new runner decoder rejects the entire binding unless `workDir` is a non-empty string (`packages/runner/src/server/session-target.ts:176-189`), before target resolution can use a cached session or restore it. A valid-looking bound session with no work directory therefore returns `runner_unavailable` for follow-up and cannot recover command delivery after a runner restart, contrary to the persisted-binding requirement. No test covers this nullable contract path. [disallowed:public contract and recovery semantics]
  SuggestedAction: Make `workDir` mandatory before a binding is persisted, or permit a nullable binding through the wire decoder and return a precise, actionable missing-runtime/configuration outcome. Add cache-hit and post-restart follow-up/cancel coverage for the chosen contract.
  Verification: Open and attach both workflow and generic sessions with `workDir: null`, then exercise follow-up and cancel before and after clearing the runner cache. Verify a defined product outcome rather than target-decoding failure.
  Status: open

## Follow-up Items

_None._

## Pre-existing or Out-of-scope Items

- [ID: item-4]
  Severity: info
  Scope: `docs/cli-reference.md:200`
  Evidence: The CLI reference names `mo issue session get`, while the registered command is `show` in `packages/cli/Mohist.Cli/MohistCliCommands.Issue.Session.cs:61`. The erroneous reference is unchanged from `master` and is unrelated to the issue-407 implementation.
  SuggestedAction: Correct `get` to `show` in a documentation maintenance change.
  Status: pre-existing

- [ID: item-5]
  Severity: info
  Scope: `docs/epics.md:30,57`
  Evidence: The page states the accepted priority range is `p0-p3` at line 30 but says `p0-p4` in the field table. CLI validation accepts only `p0|p1|p2|p3`. This inconsistency predates the issue-407 work.
  SuggestedAction: Change the field table to `p0-p3` in a documentation maintenance change.
  Status: pre-existing

<promise>PASS</promise>
