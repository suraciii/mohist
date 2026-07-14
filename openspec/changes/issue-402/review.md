# Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: `packages/web/src/pages/session/data/useIssueSessionDataSource.tsx:155`
  Evidence: The legacy session-id forced-refetch set its one-shot guard in `.finally()`, so a transient refetch failure (network/server error) would still arm the guard, leaving `resolvedSessionName` permanently undefined and the user stranded on a loading state with no metadata/transcript request and no error surface. Changed to `.then()` so the guard is armed only on a successful refetch; a failed refetch leaves the guard unset, allowing the effect to retry on the next render.
  Verification: `npx vitest run tests/CoderSessionEvidence.spec.tsx` — 28 passed.
  Status: resolved

- [ID: item-2]
  Severity: info
  Scope: `packages/web/src/entities/coder-session/model/useCoderSessions.ts:40-44`
  Evidence: The merge-on-refetch built a `Map` it never read by value (only by key membership) to filter out survivors already present in the refreshed list. Simplified to a `Set<string>` of refreshed ids so the intent (preserve hub-arrived sessions not yet in the server list, append them after the server-ordered set) reads clearly. Order semantics are unchanged and deliberate: the server list is the authoritative ordering; live-arrived sessions the server has not yet listed are newer and belong after it.
  Verification: `npx vitest run src/entities/coder-session/model/useCoderSessions.test.tsx tests/CoderSessionEvidence.spec.tsx` — passed.
  Status: resolved

- [ID: item-3]
  Severity: info
  Scope: `packages/server/tests/Mohist.Server.SpecTests/Specs/Api/ProjectEventsApiSpecs.cs`
  Evidence: The stored `TimeSortKey` computed column (`EventReadKeys.TimeSortKeySql`) had no spec proving it orders sub-second timestamps for the real Microsoft.Data.Sqlite `DateTimeOffset` storage format (roundtrip `.ffffffff+00:00`). Added `GetProjectEvents_SubSecondTimes_AreOrderedByFractionalPrecision`, which seeds three issue events 100/500/900 ms apart and asserts the descending order. The test exercises the actual stored-column expression end-to-end through the endpoint, so it also guards against regressions in the `substr` fractional extraction.
  Verification: `dotnet test --filter "FullyQualifiedName~SubSecond|FullyQualifiedName~ProjectEvents"` — 19 passed.
  Status: resolved

## Blocking Items

None.

## Follow-up Items

- [ID: item-4]
  Severity: follow-up
  Scope: `packages/server/src/Mohist.Server/Infrastructure/Data/Events/EventReadKeys.cs`
  Evidence: `TimeSortKeySql` normalizes to a `Z` suffix and assumes position-20 string surgery over the stored `DateTimeOffset` text. All current event producers write UTC (`TimeProvider.GetUtcNow()` / `DateTimeOffset.UtcNow` / injected `now`), so every stored row carries a `+00:00` offset and the expression is correct and proven by the new sub-second spec. If a future producer stamps a non-zero offset, the expression would not normalize it to UTC before deriving the key. This is not a present defect (no such producer exists), but the expression is defensive only for the UTC case.
  SuggestedAction: If a non-UTC event producer is ever introduced, switch `TimeSortKey` to a write-time UTC-normalized column or a SQL expression that converts via `datetime()` before formatting.
  Status: follow-up

- [ID: item-5]
  Severity: follow-up
  Scope: `packages/server/src/Mohist.Server/AgentOps/Services/ProjectEventFeedAssembler.cs`
  Evidence: The attention/server-side failure filter bounds the candidate set in SQL only for the `coder_session_status_changed` and `session.closed` paths. Workflow and issue failures rely on in-memory `AttentionOnly` pruning after the bounded `Take(limit)`. The guarantee "an older failure is found beyond a window of newer routine events" therefore holds for session failures (proven by specs) and for workflow/issue failures when the failure count itself is under the limit (the routine events are pruned in memory, leaving room). It would only break if a single project accumulated more than `limit` failure events newer than the target failure — a volume this evidence view does not approach today.
  SuggestedAction: Push a failure predicate into the workflow/issue SQL candidate selection if a project ever concentrates enough failure events to exceed the requested window.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-6]
  Severity: info
  Scope: `packages/web` Vitest worker heap pressure
  Evidence: Running the full `packages/web` Vitest suite concurrently occasionally triggers a V8 "Ineffective mark-compacts near heap limit" OOM in a single worker, surfacing as a non-test "Worker exited unexpectedly" error while all 4647 test assertions still pass. This is an environment-level memory pressure issue under parallel jsdom execution, not a defect in the changed code, and reproduces independent of this change.
  SuggestedAction: Consider lowering the default Vitest fork/thread count or raising the worker heap limit in a separate change if the suite grows further.
  Status: pre-existing

<promise>PASS</promise>
