# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: consistency
  Evidence: `proposal.md` §Impact stated "the **two** `new RuntimeConsistencyValidator(...)` construction sites", but the codebase has **three** production construction sites — `packages/cli/Mohist.Cli/MohistCliCommands.cs:141`, `packages/cli/Mohist.Cli/MohistCliCommands.Update.cs:166`, and `packages/cli/Mohist.Cli/Program.cs:21` (verified via grep). `tasks.json` already listed all three correctly, so the proposal was the outlier. Updated `proposal.md` line 21 to read "the three `new RuntimeConsistencyValidator(...)` construction sites" and to enumerate `MohistCliCommands.cs` / `MohistCliCommands.Update.cs` / `Program.cs`.
  Verification: re-read the edited section; the count and file list now match `tasks.json` acceptance criterion 2 and the grep result (`Found 5 matches` total, 3 production + 2 test).
  Status: resolved

- [ID: item-2]
  Severity: info
  Scope: completeness
  Evidence: `proposal.md` line 23 and `tasks.json` acceptance criterion 8 both claimed "existing identity specs remain green since their handlers answer on the first call." This is **false** for `CheckRunnerIdentityAsync_EndpointUnreachable_ReportsWarn` (`packages/cli/tests/Mohist.Cli.Tests/RuntimeConsistencyValidatorSpecs.cs:319`): its `RecordingHttpHandler` always returns `503 ServiceUnavailable`, so `TryGetRunnerIdentityAsync` returns `null` on every call. Under the proposed poll loop with default `TimeProvider.System` + 30s timeout, that existing test would block for ~30 seconds (violating the `design/testing.md` "unit < 50ms" constraint and the issue's "不依赖真实时间" acceptance criterion). The other four existing runner-identity specs (matching hash, differing hash, missing/null hash, source-HEAD-unavailable) genuinely answer on the first call or skip HTTP entirely and do remain green unchanged. Edited `proposal.md` line 23 and `tasks.json` acceptance criterion 8 to call out the `EndpointUnreachable` exception and require retrofitting it with a `FakeTimeProvider` + short timeout (or folding it into the new "never available within window" spec). The fix is a plan-accuracy clarification only — no product/architecture change.
  Verification: confirmed the canonical fake-time poll-loop template already exists in-repo at `ServiceReadinessProbeSpecs.cs:75` (`WaitForServerReadyAsync_TimeoutWithNeverReady_ReportsNotReady` uses `FakeTimeProvider` + `time.Advance(...)`), so the retrofit is mechanical and uses existing test infrastructure; re-read both edited strings to confirm wording accuracy.
  Status: resolved

## Blocking Items

_None. All `alignment`, `completeness`, `consistency`, `feasibility`, and `dependency_completeness` checks pass after the two repairs above._

## Follow-up Items

- [ID: item-3]
  Severity: follow-up
  Scope: feasibility
  Evidence: `design.md` Decision 5 mentions an optional "tail probe" after the deadline (mirroring `ServiceReadinessProbe.TryCaptureFinalFailureAsync`) to remove an off-by-one at the timeout boundary. The spec does not mandate or forbid this; it is an implementation detail. The "Identity never registers within the bounded window" scenario is satisfied either way (terminal Warn).
  SuggestedAction: Implementer may include the tail probe for parity with `ServiceReadinessProbe`, but it is not required by spec. If included, ensure the `FakeTimeProvider`-driven "never available" spec still asserts the Warn outcome without consuming real time.
  Status: follow-up

- [ID: item-4]
  Severity: follow-up
  Scope: consistency
  Evidence: The spec's "Identity never registers within the bounded window" scenario says `/api/runner/identity` "keeps returning a null/empty payload". Read alone, "empty payload" is slightly ambiguous (could mean a null snapshot vs. a non-null snapshot with an empty `buildGitHash`). `design.md` Decision 3 disambiguates: `null` snapshot → poll again; non-null snapshot (regardless of hash) → exit loop and evaluate. The "empty buildGitHash → Warn without further polling" scenario confirms non-null-with-empty-hash must NOT keep polling. The scenarios are consistent when read together.
  SuggestedAction: Optional wording tweak in `spec.md` to replace "null/empty payload" with "null snapshot (the identity-fetching helper returns null)" for single-read clarity. Not required — current text is internally consistent.
  Status: follow-up

## Review Summary

- **alignment**: Proposal addresses the actual issue (false "did not respond" warn on full `mo update`). Every "What Changes" entry traces to an issue acceptance criterion (no-false-warn, injectable timing, genuine-failure-still-Warn, three timing unit specs, non-goals honored). No requirements missing or misinterpreted.
- **completeness**: All four issue acceptance criteria covered by spec scenarios and by T-001's acceptance criteria. Edge cases considered: immediate availability, polled availability, timeout, empty-hash short-circuit, mismatch short-circuit, unresolvable source HEAD (zero probes). The one coverage gap (the existing `EndpointUnreachable` test) was repaired in item-2.
- **consistency**: Capability `runner-identity-verification`, spec dir `specs/runner-identity-verification/`, and T-001 `spec` anchor `#runner-identity-verification-polls-for-registration-readiness` all align. `design.md` Decisions 1–5 align with the spec requirements (in-method poll loop, reuse `TryGetRunnerIdentityAsync`, null=poll-again/non-null=exit-loop, `TimeProvider` injection mirroring `ServiceReadinessProbe`, deadline-guard loop). The construction-site count mismatch was repaired in item-1.
- **feasibility**: All dependencies exist in-repo — `TimeProvider`, `FakeTimeProvider` (already used in `ServiceReadinessProbeSpecs.cs`), `RecordingHttpHandler`, `TryGetRunnerIdentityAsync` (line 253), the `ServiceReadinessProbe` time-injection template, and the three production construction sites. T-001 is a single cohesive feature slice (implementation + construction-site verification + tests) — not too fine, with tests inlined rather than split out, matching the feasibility guidance.
- **dependency_completeness**: Single task (T-001), `dependsOn: []`, priority 1. No cycles possible. No other tasks reference missing IDs.

<promise>PASS</promise>
