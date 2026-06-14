# Review Report

## Result: PASS

## Acceptance Criteria Verification

All nine acceptance criteria from the issue body are satisfied by the change. Concrete evidence:

- **`AppendRuntimeEventsAsync` returns without DB writes** — `packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:145-188` performs no `_stateStore.SaveAsync` / `_transcriptStore.SaveAsync` calls. Confirmed by `AgentSessionGrainPersistSuccessSpecs.PersistCallback_Success_SavesStateAndTranscriptAndDisposesTimer` (test passes).
- **`TranscriptAccumulator.Accept()` returns void** — `packages/server/src/Mohist.Server/Sessions/Services/TranscriptAccumulator.cs:36` returns void. Six `TranscriptAccumulatorSpecs` tests pass.
- **`BuildFlush()` + `CommitFlush()` two-phase interface** — `packages/server/src/Mohist.Server/Sessions/Services/TranscriptAccumulator.cs:71-91`. `BuildFlush_DoesNotClearAccumulatedPartsOrInputTracking` and `CommitFlush_ClearsPendingPartsAndInputTracking` tests pass.
- **One-shot Orleans timer (200ms)** — `AgentSessionGrain.cs:14` (`PersistTimerDueTime = 200ms`), `:255-258` (registration with `Timeout.InfiniteTimeSpan`).
- **Try-catch with structured `LogError`** — `AgentSessionGrain.cs:280-285, 296-301` for `PersistCallback`; `:72-75, 88-91` for `OnDeactivateAsync`. LogError includes `SessionId` and part counts. Tests assert the message contains both.
- **`OnDeactivateAsync` flushes synchronously with error logging** — `AgentSessionGrain.cs:51-100` performs the same persistence sequence inline and logs failures. Four deactivation tests pass.
- **`session.input` captures prompt for next flush** — `TranscriptAccumulator.cs:51-55, 153-163` and `:184-196`. `Accept_SessionInput_CapturesPromptForNextFlush` test passes.
- **Session detail page shows complete transcript** — `AgentSessionSpecs.DeferredPersistence_SessionDetailTranscriptContainsAllTextAndToolParts` integration test passes; end-to-end validation asserts text + reasoning + tool parts survive the 200ms deferral.
- **`SyncLabelsAsync` logs warning on null labels** — `AgentSessionStore.cs:91-97` emits `LogWarning` with `sessionId`. `AgentSessionStoreSpecs.SaveAsync_NullLabels_LogsWarningAndRemovesExistingLabels` test passes.

Test summary: all 40 grain/accumulator/store/integration tests in the change scope pass; 2 are pre-existing skips unrelated to this change. The build is clean for the changed files.

## Repaired Items

None.

## Blocking Items

None.

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: `packages/server/src/Mohist.Server/Sessions/Services/TranscriptAccumulator.cs:134-138` and `:165-182`
  Evidence: `CreatePartDelta` accepts a `RuntimeEventEnvelope row` parameter that is never read by the method body. `FlushPendingIntoAccumulated` constructs a new envelope solely to pass `lastSeenAt` even though that timestamp is already supplied via a separate `lastSeenAt` parameter. The dead argument and the throw-away `RuntimeEventEnvelope { CreatedAt = lastSeenAt }` allocation are a code smell that confuses the contract of `CreatePartDelta`.
  SuggestedAction: Remove the `row` parameter from `CreatePartDelta` and update the two call sites. Not a correctness issue — `lastSeenAt` is passed correctly — so safe to defer.
  Status: follow-up

- [ID: item-2]
  Severity: follow-up
  Scope: `packages/server/src/Mohist.Server/Sessions/Services/TranscriptAccumulator.cs:184-196` (`BuildTurn`)
  Evidence: `BuildTurn` computes `Sequence: _inputCreatedAt.HasValue ? 1 : 0`. The consumer `AgentSessionTranscriptStore.SaveAsync` always overrides this value (line 56-61): when `StartNewTurn` is true or `Sequence <= 0`, the store uses `(max + 1)`; when not, it uses `transcript.Turn.Sequence`. Because `StartNewTurn = _promptText is not null` and the accumulator never emits `Sequence = 0` while `StartNewTurn` is true, the calculated sequence in the accumulator is effectively dead — the store always recomputes it. The Sequence field is also a public record field on `AgentSessionTranscriptTurnUpsert`, so this is not a privacy concern, just redundant computation.
  SuggestedAction: Either pass `0` unconditionally and let the store compute, or document the relationship. Not a correctness issue today because the store's logic is idempotent. Safe to defer.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: `packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:67, 277` vs. `OpenAsync:113`, `AttachPhysicalSessionAsync:136`
  Evidence: The deferred-persistence change covers `AppendRuntimeEventsAsync` only. `OpenAsync` (line 102-115) and `AttachPhysicalSessionAsync` (line 128-143) still perform inline `_stateStore.SaveAsync` on the runner's HTTP request thread. This was explicitly out-of-scope for issue 100 (the issue body and proposal limit the change to `AppendRuntimeEventsAsync`), but the runner still blocks on a DB write during the first `OpenAsync` and on `AttachPhysicalSessionAsync`. This is a pre-existing characteristic, not a regression introduced by the change.
  SuggestedAction: Track in a follow-up issue to route these through the same deferred-persistence path. No code change is required for the current issue.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-4]
  Severity: info
  Scope: `packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:166, 169`
  Evidence: `RuntimeEventEnvelope.Id` is assigned `-(_realtimeSequence + 1)` (negative placeholders) and the same field is then used as the `Id` of the realtime `TranscriptEnvelope` published to subscribers. Real-time subscribers therefore observe negative numeric IDs. This is pre-existing and not introduced by issue 100.
  SuggestedAction: When the realtime `Id` is reified (e.g., persisted on `AgentSessionTranscriptPart`), revisit the placeholder scheme. No action required for this issue.
  Status: pre-existing

- [ID: item-5]
  Severity: info
  Scope: `packages/server/tests/Mohist.Server.Tests/Specs/Api/IssueCliProjectRefAndOutputSpecs.cs`
  Evidence: One CLI test (`IssueSessions_Help_ListsProjectProjectIdAndOutputOptions`) fails with `No service for type 'Mohist.Cli.IServiceInstaller' has been registered` when run through the Server.Tests assembly. The file is not touched by the issue 100 commit chain (`git log 2c4889bc..743938df -- …` returns empty for this path). The test also fails on the pre-change base commit and is therefore pre-existing.
  SuggestedAction: Fix the missing `IServiceInstaller` registration in the test fixture or mark the test with `[Trait(Skip=…)]` to keep the suite green. Not a blocker for issue 100.
  Status: pre-existing

- [ID: item-6]
  Severity: info
  Scope: `packages/server/src/Mohist.Server/Issue/Grains/IssueGrain.cs:84` and `Workflow/Services/Sessions/AgentSessionQuerier.cs:621-626`
  Evidence: `dotnet format --verify-no-changes` reports `WHITESPACE` errors in two files that are untouched by the issue 100 commit chain (`git log 2c4889bc..743938df -- …` returns empty). The formatting issues are pre-existing.
  SuggestedAction: Run `dotnet format` to clean up whitespace in a follow-up commit. The files in scope for this issue (`AgentSessionGrain.cs`, `TranscriptAccumulator.cs`, `AgentSessionStore.cs`, `AgentSessionTranscriptStore.cs`) have no formatting issues.
  Status: pre-existing

## Verdict Summary

- All nine acceptance criteria are met and have passing tests as evidence.
- The two-phase flush, retry semantics, structured logging, and synchronous deactivation flush are correctly implemented.
- The non-reentrant Orleans grain scheduler serializes timer callbacks with `AppendRuntimeEventsAsync`, so the concerns around a fresh event arriving between `BuildFlush` and `CommitFlush` do not produce data loss in the current configuration (the queued event runs after the current `PersistCallback` completes, by which point `_persistTimer` is null and a fresh `EnsurePersistenceTimer` call from the queued event registers a new timer).
- Multi-turn behavior is intentionally out of scope; a second `session.input` overwrites the captured prompt, which matches the explicit non-goal.
- The 3 follow-up items are code-quality / non-functional improvements; none block correctness.

<promise>PASS</promise>
