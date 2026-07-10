# Review Report

## Result: FAIL

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: fixture-cleanup
  Evidence: `DispatcherFixture.DisposeAsync` called `_keeper?.DisposeAsync()` and dropped the returned `ValueTask` — async disposal was fire-and-forget, risking unclosed connections and ODE. The codebase convention (`MohistDbFixture.cs:154`) uses synchronous `_keeper?.Dispose()`. Changed to `_keeper?.Dispose()`.
  Verification: `npm test` — all 1031 tests pass (including dispatcher spec tests).
  Status: resolved

- [ID: item-2]
  Severity: info
  Scope: missing-injection
  Evidence: `DispatcherFixture.ConfigureDispatcherSilo` line 215 registered `siloBuilder.Services.AddSingleton<TimeProvider>(System.TimeProvider.System)` instead of the fixture's own `FakeTimeProvider` field. The `FakeTimeProvider` was declared but never injected, so the silo-level `EventDispatcherService` used wall-clock time. Changed to `siloBuilder.Services.AddSingleton<TimeProvider>(TimeProvider)`.
  Verification: `npm test` — all 1031 tests pass. Spec-level tests can now control time if needed.
  Status: resolved

## Blocking Items

- [ID: item-3]
  Severity: blocking
  Scope: `EventDispatcherService.cs:237-251` (`DeadLetterAsync`)
  Evidence: When `_deadLetters.WriteAsync(row, ct)` throws, the exception is caught and logged at line 248-251, but the caller (`DispatchOneAsync`) still proceeds to set `DispatchedAt` via `MarkDispatchedAsync` (line 186-197). The event row is marked delivered as if the handler had succeeded, but the handler's retries exhausted AND the dead-letter row was never persisted — the poison message is silently lost from both the undelivered queue and the dead-letter queue.
  SuggestedAction: Either re-throw the exception from `DeadLetterAsync` (so the row stays undelivered and is retried on the next tick), or skip the `MarkDispatchedAsync` for this handler (making it per-handler marking, which conflicts with the current one-mark-per-event design). The simplest fix: propagate the dead-letter write failure so `MarkDispatchedAsync` throws too and the row stays undelivered.
  Verification: Add a unit test where `FakeDeadLetterStore.WriteAsync` throws; assert the row remains undelivered (still in `PendingUndelivered`) and no mark occurs.
  Status: open

- [ID: item-4]
  Severity: blocking
  Scope: `openspec/changes/issue-362/specs/event-dispatch/spec.md` line 78-87 (Crash recovery → idempotent absorption) / `EventDispatcherSpecs.cs:217-243` / issue AC #4
  Evidence: Issue AC #4 explicitly requires "投递后标记前崩溃 → 重投 → 幂等吸收". The spec says "the handler SHALL absorb the redelivered duplicate idempotently by event id". The test `DispatchAsync_DeliverBeforeMarkCrash_RowStaysUndelivered_AndIsRedeliveredOnNextTick` only asserts the event is re-delivered (call count goes from 1 to 2), but the handler is a simple counter — it never absorbs or differentiates the duplicate. There is no assertion proving the handler identified the duplicate by `EventId` and idempotently no-oped. The self-review (item-2) flagged this as follow-up but it is a blocking acceptance criterion.
  SuggestedAction: Either (a) implement a handler in the test that records seen `EventId`s and asserts the duplicate is recognized on re-delivery, or (b) add an integration spec where a handler processes twice but produces the result of only one invocation.
  Verification: A test that tracks each `EventId` seen and asserts the same `EventId` appears twice but the handler's side-effect (e.g., counter, state mutation) only occurs once.
  Status: open

- [ID: item-5]
  Severity: blocking
  Scope: `DeadLetterAsync` at `EventDispatcherService.cs:251`
  Evidence: The `ErrorStack` property of `DeadLetterRow` is always set to `null`, despite the `DeadLetterRow` type having a declared `ErrorStack` property and the real exception carrying a stack trace. The `ErrorStack` column is also present in the EF migration, model snapshot, and migration spec tests. Operators inspecting dead-letter rows have no stack trace context for diagnosing poison messages.
  SuggestedAction: Capture `lastError?.ToString()` (which includes the message + stack trace) or `lastError?.StackTrace` in the `ErrorStack` field of the dead-letter row.
  Verification: `DeadLetterStoreSpecs` should include a row with non-null `ErrorStack`. Unit tests that assert exhaustion should verify the dead-letter row has a non-null `ErrorStack`.
  Status: open

## Follow-up Items

- [ID: item-6]
  Severity: follow-up
  Scope: `DispatcherFixture` / `DispatcherGrainSpecs`
  Evidence: The `DispatcherFixture` uses `NoopDeadLetterStore`, and the spec-level tests only exercise `PulseAsync` (immediate tick). There is no silo-integration test that drives the dead-letter flow through the grain — all dead-letter assertions are in pure-DI unit tests (`EventDispatcherSpecs`). This is a coverage gap for the grain → service → dead-letter wiring.
  SuggestedAction: After item-3 is fixed, add a spec-level test that publishes a poison event, runs a tick, and asserts a dead-letter row is queryable through the silo-configured `IDeadLetterStore`.
  Status: follow-up

- [ID: item-7]
  Severity: follow-up
  Scope: `DispatcherGrain.ReceiveReminder` at `DispatcherGrain.cs:60`
  Evidence: `ReceiveReminder` passes `CancellationToken.None` to `_dispatcher.DispatchAsync`, which means the dispatch cycle cannot be cancelled during silo shutdown. While this is intentional (at-least-once delivery should finish the current batch), it means shutdown can be blocked until the batch completes — which could be long with high `BatchLimit` or slow handlers.
  SuggestedAction: Consider using a linked token with a shutdown timeout. Low priority for personal-scale volumes.
  Status: follow-up

- [ID: item-8]
  Severity: follow-up
  Scope: `NoopDeadLetterStore` duplication
  Evidence: Two identical copies exist: `Mohist.Server.UnitTests.Support.NoopDeadLetterStore` and `Mohist.Server.SpecTests.Support.NoopDeadLetterStore`. The production `MohistSiloRegistration` references `NoopDeadLetterStore` for `TryAdd` but only one is in scope per test project.
  SuggestedAction: Move the canonical `NoopDeadLetterStore` to a shared test support project, or accept the duplication as intentional per-project isolation.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-9]
  Severity: info
  Scope: `event-dispatch` spec line 6 ("三张事件真相表") vs implementation (four tables)
  Evidence: The issue body says three tables; the implementation covers all four (including `AgentSessionEvents`). The design doc D3 and Open Question #1 document this divergence and resolve it in favor of four tables. `ListUndeliveredAsync` already UNIONs all four. No code diverges from the spec.
  SuggestedAction: Confirm with the issue author that AgentSession delivery is desired (default is safe per `[Subscription(Type="*")]` contract).
  Status: pre-existing

<promise>FAIL</promise>
