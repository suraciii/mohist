# Review Report

## Result: FAIL

## Repaired Items

_None._

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Sessions/Services/AgentSessionQuerier.cs`
  Evidence: `GetGenericSessionSummaryAsync` now projects `loaded.Parts` directly at lines 507-514, and `BuildSessionMetadataDtoAsync` does the same at lines 569-576. The previous path used `LoadTranscriptAsync`, whose transcript parts are ordered by `Sequence` and then `Id` at lines 841-844. `TranscriptEventSummaryProjector.Summarize` is order-sensitive: later model and closure events overwrite earlier values at `TranscriptEventSummaryProjector.cs:14-25`. Without reapplying `OrderBy(p => p.Sequence).ThenBy(p => p.Id)`, these two endpoints can choose the wrong resolved model or failure category whenever materialized row order differs from transcript sequence. [disallowed:product-behavior-change]
  SuggestedAction: Reapply the previous `Sequence, Id` ordering before projecting transcript events in both call sites, or make the shared loader expose an ordered projection helper that preserves each caller's previous ordering contract.
  Verification: Add a regression that inserts transcript parts out of sequence order and asserts generic-session summary and issue-session metadata use the highest sequence/ID event; rerun `npm test`.
  Status: open

- [ID: item-2]
  Severity: warning
  Scope: `packages/server/src/Mohist.Server/Sessions/Services/AgentSessionQuerier.cs`, `packages/server/src/Mohist.Server/Sessions/Services/TranscriptPartLoader.cs`
  Evidence: `LoadTranscriptAsync` queries turns at lines 835-839, then calls `TranscriptPartLoader.LoadAsync` at line 840. The loader independently queries turns again at `TranscriptPartLoader.cs:40-42` before loading parts at lines 48-53. That makes the returned `AgentSessionTranscriptData` potentially internally inconsistent under concurrent transcript writes: `Turns` can come from the first query while `Parts` can include parts for turns only seen by the second query. The design expected the single-session transcript loader to be a thin wrapper over one shared load, but the current implementation duplicates the turn query and changes the snapshot boundary. [disallowed:product-behavior-change]
  SuggestedAction: Return the loaded turns from `TranscriptPartLoaderResult` and have `LoadTranscriptAsync` order and return that single loaded turn set with its matching parts, or otherwise load parts from the exact first turn set without re-querying turns.
  Verification: Add a focused test or fake loader coverage for transcript detail consistency, then rerun the Session specs and `npm test`.
  Status: open

- [ID: item-3]
  Severity: test-gap
  Scope: `packages/server/tests/Mohist.Server.Tests/Specs/Sessions/GenericAgentSessionSummarySpecs.cs`, `packages/server/tests/Mohist.Server.Tests/Specs/Sessions/AgentSessionSpecs.cs`
  Evidence: The new helper specs cover that `TranscriptPartLoader` does not impose ordering (`TranscriptPartLoaderSpecs.cs:146-163`), but the endpoint/spec coverage seeds transcript parts in normal insertion order. There is no regression proving former `Sequence, Id` ordering survives in `GetGenericSessionSummaryAsync` or `BuildSessionMetadataDtoAsync`, which is why item-1 can pass the current suite.
  SuggestedAction: Add tests that insert model/closure parts with row insertion order different from transcript sequence, then assert summaries and metadata use the sequence-last values.
  Verification: Run the new focused tests plus `npm test`.
  Status: open

- [ID: item-4]
  Severity: cleanup
  Scope: `packages/server/src/Mohist.Server/Sessions/Services/AgentActivityFeedAssembler.cs`
  Evidence: The constructor takes `AgentSessionQuerier coreQuerier` and assigns `_coreQuerier` at lines 45 and 50-59, but the field is never read. The assembler calls static/internal helpers on `AgentSessionQuerier` instead. This leaves an unnecessary runtime dependency and makes the service graph look more coupled than it is.
  SuggestedAction: Remove the unused field and constructor parameter, or make the dependency intentional by moving the shared primitives behind instance methods.
  Verification: Rerun `npm test` and `MigratedServicesRegistrationSpecs` after the constructor change.
  Status: open

## Follow-up Items

- [ID: item-5]
  Severity: follow-up
  Scope: `packages/server/tests/Mohist.Server.Tests/Specs/Sessions/TranscriptPartLoaderSpecs.cs`
  Evidence: The new loader specs use `DateTime.UtcNow` as seed data at lines 43, 63, 104, and 128. These assertions are not currently time-sensitive, but the project testing guidance asks tests to avoid wall-clock time.
  SuggestedAction: Replace those seeds with a fixed `DateTime` constant so the tests stay aligned with the no-real-time convention.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-6]
  Severity: info
  Scope: `packages/web/src/entities/session/model/view.ts`
  Evidence: The web session view still accepts both `session.closed` and `session_closed` at lines 209-210, and adjacent web tests still mention `session_closed`. The OpenSpec task scoped the required literal search to `packages/server/src/Mohist.Server/Sessions/`, so this is not counted as a candidate failure, but it is a remaining product-level legacy spelling path if the intended cleanup is later expanded beyond server read models.
  SuggestedAction: Decide whether web transcript normalization should also drop the underscore alias in a separate web-facing change.
  Status: out-of-scope

- [ID: item-7]
  Severity: info
  Scope: verification
  Evidence: `npm test` passed on rerun with a 600s timeout. The first run with a 120s timeout expired while the .NET suite was still running; the longer rerun completed successfully, including server `dotnet test` and workspace Vitest suites.
  SuggestedAction: None.
  Status: out-of-scope

<promise>FAIL</promise>
