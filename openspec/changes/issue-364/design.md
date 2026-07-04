## Context

`mo update` (full) ends with a `VerifyRuntime` stage that runs `RuntimeConsistencyValidator` against the freshly restarted stack. The `Runner identity` check in that validator (`packages/cli/Mohist.Cli/Update/RuntimeConsistencyValidator.cs:178`, `CheckRunnerIdentityAsync`) performs a **single** `GET /api/runner/identity`. If the runner process is `active` per systemd but has not yet completed its WebSocket handshake + identity registration with the server, the endpoint returns null/empty and the check emits `[warn] Runner identity: GET /api/runner/identity did not respond` — a false alarm, since `curl` succeeds moments later.

The same CLI already solves this exact registration-lag problem in `RunnerRefreshVerifier.VerifyRunnerRuntimeAsync` (`packages/cli/Mohist.Cli/Update/RunnerRefreshOutcome.cs:110`), which powers `mo update runner` standalone: a 30s-timeout + 500ms-poll loop. The `VerifyRuntime` layer just doesn't reuse that idea. The canonical time-injection pattern for CLI polling loops lives in `ServiceReadinessProbe` (`packages/cli/Mohist.Cli/Update/ServiceReadinessProbe.cs`): injectable `TimeProvider` (default `TimeProvider.System`), `GetUtcNow()` for the deadline, `Task.Delay(interval, timeProvider, token)` for inter-probe waits.

Stakeholders: anyone running `mo update` (every full update currently surfaces this false warn). Constraints (from `design/testing.md`): no real HTTP, no wall-clock in tests; timing must be injectable.

## Goals / Non-Goals

**Goals:**
- Make `CheckRunnerIdentityAsync` tolerate the systemd-active ↔ identity-registered lag by polling `/api/runner/identity` until a non-null identity with a non-empty `buildGitHash` is returned, or a bounded timeout elapses.
- Reuse the existing `TryGetRunnerIdentityAsync` private helper as the per-attempt probe — no duplicated HTTP/deserialization logic.
- Make timeout, poll interval, and `TimeProvider` injectable (constructor params with defaults matching `RunnerRefreshVerifier`: 30s / 500ms / `TimeProvider.System`), following the `ServiceReadinessProbe` convention.
- Preserve all non-timing outcomes: empty `buildGitHash` → Warn (short-circuit, no further polling); mismatched `buildGitHash` → Warn (short-circuit); unresolvable source HEAD → skip Warn (zero probes).
- Add unit specs for the three timing scenarios, driven by fake HTTP + `FakeTimeProvider` (no real network, no wall-clock).

**Non-Goals:**
- Do not change `RunnerRefreshVerifier` — it works.
- Do not change `RestoreRunner`'s systemd-active gate, the `mo update` stage ordering, the server `/api/runner/identity` contract, runner registration, or the WebSocket handshake.
- Do not change `buildGitHash` comparison semantics.
- Do not refactor the other `Check*Async` methods (`CheckServerIdentityAsync`, `CheckRunnerConnectionAsync`, etc.) — they are out of scope even though a similar lag argument could be raised; the bug report is specific to runner identity.

## Decisions

### Decision 1: Polling lives inside `CheckRunnerIdentityAsync`, not behind a new "readiness gate" stage

**Choice:** Add the poll loop directly inside `CheckRunnerIdentityAsync`. Do **not** insert a new `RestoreRunner`↔`VerifyRuntime` "runner identity readiness" stage/machine gate.

**Rationale:** The fix is one method in one validator. Adding a stage changes the `mo update` state machine (explicitly a Non-Goal), widens the diff, and forces every other check in `VerifyRuntime` to wait for runner identity even when they don't need it. The proposal's two options are functionally equivalent for this check; the in-method loop is the lower-blast-radius one.

**Alternatives considered:**
- *New readiness stage between `RestoreRunner` and `VerifyRuntime`:* rejected — touches stage machine, complicates rollback, and the only consumer that needs the wait is this one check.
- *Drive `CheckRunnerIdentityAsync` through `RunnerRefreshVerifier` directly (call `VerifyRunnerRuntimeAsync` and map its outcome):* rejected — `RunnerRefreshVerifier` returns a `RunnerRefreshOutcome` hierarchy with richer semantics (hostname matching, dist manifest fallback, online-status gating) that don't apply here, and the existing `RuntimeCheckResult` Warn messages are the contract the stage machine prints. Reusing the *idea* (bounded poll) without reusing the *type* keeps the two validators decoupled.

### Decision 2: Reuse `TryGetRunnerIdentityAsync` as the per-attempt probe unchanged

**Choice:** The existing private helper (`RuntimeConsistencyValidator.cs:253`) already does the right thing: returns `null` on non-success, on `NotFound`, on deserialization failure; returns the snapshot otherwise. The loop calls it once per attempt and treats `null` as "not ready yet, keep polling". No new HTTP code is written.

**Rationale:** Duplicating the fetch logic would be a direct spec violation ("per-attempt probe MUST reuse the existing identity-fetching helper") and would drift over time. The helper's "swallow exceptions → null" behavior is exactly what a poll probe wants.

### Decision 3: "Ready" = non-null identity AND non-empty `buildGitHash`

**Choice:** The loop continues while the probe returns `null` OR a snapshot whose `BuildGitHash` is null/whitespace. Only when a non-empty `buildGitHash` arrives does the loop exit and proceed to comparison.

**Rationale:** The spec's "Identity becomes available after several polls" scenario explicitly treats null/empty payloads as "still reconnecting". But once a *non-empty, non-matching* hash arrives, that's a real signal about the deployed artifact — there's no point polling further, so the mismatch Warn short-circuits out of the loop. This preserves the present-but-unusable semantics from the spec ("Identity buildGitHash differs from source HEAD → Warn, without continuing to poll further").

**Subtle point:** an empty `buildGitHash` on a *non-null* snapshot is ambiguous — could be a transient mid-registration state or a genuinely broken runner. Per the spec's "Identity reports an empty buildGitHash → Warn, without continuing to poll further", a non-null snapshot with empty hash must **not** poll. Resolution: the loop treats `null` snapshot as "poll again", but a non-null snapshot (regardless of hash content) exits the loop and is then evaluated by the existing comparison block. The comparison block already produces the correct Warn for empty/mismatched hashes. This is the smallest change that satisfies both the "poll for readiness" requirement and the "don't poll past a present-but-unusable identity" requirement.

### Decision 4: Time injection mirrors `ServiceReadinessProbe`, not `RunnerRefreshVerifier`

**Choice:** Add constructor params `TimeProvider? timeProvider = null`, `TimeSpan? runnerIdentityTimeout = null`, `TimeSpan? runnerIdentityPollInterval = null`. Drive the deadline via `_timeProvider.GetUtcNow() + timeout` and the inter-probe delay via `Task.Delay(interval, _timeProvider, token)`.

**Rationale:** `RunnerRefreshVerifier` predates the testing-policy tightening — it uses `CancellationTokenSource(timeout)` + `Task.Delay(interval, cts.Token)` with no `TimeProvider`, which makes its timing non-injectable and would require fake timers to actually elapse. The project's current convention (`ServiceReadinessProbe`, `design/testing.md` "禁止真实时间") mandates `TimeProvider`. We follow the newer, policy-compliant pattern. Default values (30s / 500ms) match `RunnerRefreshVerifier`'s so production behavior is consistent across the two paths.

**Alternatives considered:**
- *Use `CancellationTokenSource(timeout)` like `RunnerRefreshVerifier`:* rejected — untestable without real time, violates the testing constraint.
- *Make timing config a top-level `UpdateOptions` field:* rejected — over-engineered; no caller needs to tune this, only tests do. Constructor params with defaults suffice.

### Decision 5: Deadline loop guard, not a timer-fired cancel

**Choice:** `while (_timeProvider.GetUtcNow() < deadline)` with a `Task.Delay(interval, _timeProvider, token)` between probes, plus a final "tail probe" after the loop in case the deadline expired mid-delay (mirroring `ServiceReadinessProbe.WaitForServerReadyAsync`'s `TryCaptureFinalFailureAsync` shape — optional but cheap and removes an off-by-one edge case at the boundary).

**Rationale:** Matches the in-repo canonical shape, avoids `CreateTimer` ceremony, and the `GetUtcNow()` reads are deterministic under `FakeTimeProvider`. The `token` passed by the caller still cancels mid-delay for cooperative shutdown; the deadline is a secondary bound independent of the caller's token.

## Risks / Trade-offs

- **[Up to 30s added to `VerifyRuntime` on a genuinely broken runner]** → Mitigation: the timeout is bounded (not infinite), defaults match `RunnerRefreshVerifier` so users already accept this wait in `mo update runner`, and the outcome is still Warn (real failures are not masked). Acceptable per the issue's acceptance criteria.
- **[Two polls on the happy path if registration lands just after the first probe]** → Mitigation: first probe happens immediately (no leading delay), so an already-registered runner returns Pass with zero delays, matching the spec's "Identity already registered on the first probe" scenario.
- **[Poll loop semantics for non-null-with-empty-hash could mask a transient empty state]** → Mitigation: spec explicitly forbids further polling once a non-null identity is seen; the empty-hash Warn is the documented behavior. A genuinely broken runner will surface within one probe rather than being hidden behind a long poll.
- **[Constructor signature change touches three production construction sites + one test builder]** → Mitigation: all new params are optional with defaults, so existing call sites compile unchanged; the test `BuildValidator` helper grows one optional param. Low blast radius.
- **[Possible divergence over time between this loop and `RunnerRefreshVerifier`'s loop]** → Accepted: the two have different DTOs, different comparison semantics, and different outcome types; a shared abstraction would be forced. The proposal explicitly scopes this out.

## Migration Plan

No persistence, no API contract, no runner/server changes — purely a CLI build artifact. Deployment is:

1. Merge the change; rebuild CLI (`npm run build`).
2. The fixed `mo` ships to users on their next `mo update`. The first run that exercises the fix is the very update that installs it, so users self-heal on the same invocation.
3. Validate post-deploy: run `mo update` end-to-end and confirm the `Runner identity` line reads `[ok]` instead of `[warn] did not respond` when the runner is healthy.

Rollback: revert the commit and rebuild — no data migration, no schema concerns. The pre-fix behavior (single-shot GET) is restored.

## Open Questions

- None blocking. The defaults (30s / 500ms) are inherited from `RunnerRefreshVerifier` and accepted there; if telemetry later shows the 30s is too long for the VerifyRuntime path specifically, the constructor param makes it tunable without a follow-up code change.
