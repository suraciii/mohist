# Review — issue-435

Reviewed the change committed in `T-001` (`b0d04548b`) and `T-002` (`c7acf433f`) against issue #435's acceptance criteria and the plan artifacts in this directory (`proposal.md`, `design.md`, `tasks.json`, `specs/runner-model-discovery/spec.md`). The plan artifacts themselves are this workflow's own output and are not judged as deliverables; they are used here as the spec to check the code against.

## Change under review

- `packages/runner/src/runtime/opencode-models.ts` — TTL cache guard removed; `discoverOpencodeModels` now always spawns `opencode models --verbose`; `opencodeModelSetsEqual` helper added.
- `packages/runner/src/runtime/host.ts` — new `rediscoveryTimer` `setInterval` registered alongside the existing four timers in `run()`; cleared in the same `finally` block; new private `runModelRediscoveryOnce(signal)` calls discovery, compares via `opencodeModelSetsEqual`, and only on change updates state + calls `sendImmediateHeartbeat`.
- `packages/runner/src/core/types.ts` — `RunnerOptions.modelRediscoveryIntervalMs?: number` added.
- `packages/runner/tests/opencode-models.spec.ts` — TTL caching test migrated to "executes command for every call"; new `opencodeModelSetsEqual` unit block covers 7 cases (same/different order, missing/new model id, variant reorder/add/remove).
- `packages/runner/tests/runner-host-model-rediscovery.spec.ts` — new spec, 6 scenarios using `vi.useFakeTimers()` covering pre-interval no-fire, periodic fire, unchanged→no-heartbeat, changed→one-heartbeat, empty→state-preserved, thrown→logged+contained, abort→timer-cleared.

## Verification performed

- `npm run typecheck -w packages/runner` → clean (no diagnostics).
- `npm test -w packages/runner` → 100 files / 1184 tests pass, including the new `runner-host-model-rediscovery.spec.ts` (6 tests) and migrated `opencode-models.spec.ts` (18 tests).

## Acceptance-criteria trace

| Issue criterion | Status | Where |
| --- | --- | --- |
| runner refreshes discovered set ≥once 30 min after startup via a single periodic timer; TTL guard removed or non-conflicting | met | `host.ts:331` registers `rediscoveryTimer`; `host.ts:339` clears it; `opencode-models.ts` has no `CACHE_TTL_MS`/`cached`/`Date.now()`; no manual call from `run()` so first fire is at `+interval` (test `TimerDoesNotFireBeforeInterval_AndFiresOnceAtInterval`) |
| after `opencode.json` edit, new entries appear in `/api/projects/.../opencode/models` within one interval | met | periodic rediscovery + change-gated `sendImmediateHeartbeat` carrying the updated `registrationState()` (test `ChangedRediscovery_TriggersOneImmediateHeartbeatWithUpdatedState`) |
| unchanged rediscovery does not trigger extra heartbeat; comparison is order-insensitive on `coderModels` and `coderModelVariants` | met | `opencode-models.ts:29-44` (sort-then-compare for models, key-set for variant map, sort-then-compare per key); `host.ts:360` returns early on equal; test `UnchangedRediscovery_DoesNotTriggerExtraHeartbeat` |
| discovery failure preserves prior snapshot on runner and server; next tick retries | met | `host.ts:359` returns early when `models.length === 0`; the timer's `.catch()` (`host.ts:331`) prevents the run loop from being disturbed; tests `EmptyRediscovery_LeavesLocalStateUnchanged_AndDoesNotTriggerHeartbeat` and `ThrownDiscoveryError_IsLogged_AndNextIntervalStillFires` |
| timer callback catches + logs errors, no unhandled rejection, next tick still fires | met | `host.ts:331` `setInterval(() => void …runModelRediscoveryOnce(signal).catch((error) => console.error("model rediscovery fire failed", error)), …)`; tests `ThrownDiscoveryError_…` and `AbortingRunner_…` |
| time judgment in the rediscovery path is injectable; spec tests use fake timers | met (see F-001 for nuance) | no `Date.now()` in the path; interval override via `RunnerOptions.modelRediscoveryIntervalMs`; spec uses `vi.useFakeTimers()` + `vi.advanceTimersByTimeAsync` exclusively |
| `coderModelVariants` refreshed in lockstep with `coderModels` | met | `host.ts:361-362` assigns both; `opencodeModelSetsEqual` compares both (variant-only change case in spec.md:88-93 is satisfied because the model-id check passes but the variant check fails) |

## Findings

### F-001 (non-blocking). Spec paragraph wording for "Time is injectable" is stricter than the implementation; the contradiction flagged as B-001 in `self-review.md` was not reconciled before building.

`specs/runner-model-discovery/spec.md:133` requirement paragraph reads, in part:

> Every time-driven decision in the rediscovery path … SHALL read from an injected clock, not from `Date.now()` or any other wall-clock source. **The runner SHALL accept this clock via its construction/option surface** so that tests can drive it.

`design.md` Decision D7 deliberately chooses the opposite — no clock object is threaded through `RunnerHost`; the only time source in the new path is `setInterval` itself, which `vi.useFakeTimers()` intercepts natively. The implementation followed D7: `RunnerHost` accepts `modelRediscoveryIntervalMs` (the interval) but not a clock object.

Self-review B-001 flagged this paragraph-vs-design contradiction as blocking and asked that one of the two be reconciled before building. Neither was: the spec paragraph still says "SHALL accept this clock via its construction/option surface" and the implementation still doesn't.

Why this is non-blocking for the change:

- The issue body's own criterion is permissive — "`vi.useFakeTimers()` 推进时间来验证 … 不依赖真实墙钟 sleep" — and is satisfied.
- The spec's two concrete scenarios under the requirement ("Tests drive rediscovery via fake timers", "No Date.now in the rediscovery path") are both satisfied by the implementation. No scenario in the spec actually tests for a clock object on the construction surface.
- The behavior works: tests can drive the timer deterministically.

Cleanup either way before this becomes a pattern: soften the spec paragraph to say what its scenarios actually test (interval is overridable; no `Date.now()` in the path; tests drive ticks via `vi.useFakeTimers()`), or thread a `TimeProvider` through `RunnerHost` to literally satisfy the paragraph. Recommend the former — the rest of `host.ts:325-330` already relies on fake-timer interception of `setInterval`, so adding a clock object only here would diverge from the established pattern.

### F-002 (non-blocking). No spec test covers the "Thrown heartbeat error is logged and contained" scenario in the rediscovery context.

`specs/runner-model-discovery/spec.md:124-129` scenario requires that when the change-triggered immediate heartbeat throws, the error is logged, no unhandled rejection escapes, and the next periodic fire still occurs.

The behavior is satisfied indirectly by `host.ts:414-417` (`sendImmediateHeartbeat` already wraps `connection.heartbeat(...)` in its own `try/catch` logging "immediate post-reconnect heartbeat failed"), so a heartbeat throw never reaches the timer's `.catch()` and never disrupts the next tick. But the new spec only injects a throw into `discoverOpencodeModels` (`ThrownDiscoveryError_IsLogged_AndNextIntervalStillFires`); it never makes the heartbeat mock reject. A future refactor that removes the inner `try/catch` from `sendImmediateHeartbeat` would silently regress this spec scenario without any test failing.

Suggested follow-up: in `runner-host-model-rediscovery.spec.ts`, add a case where `heartbeat.mockRejectedValueOnce(...)` after a changed rediscovery, then assert (a) the error is logged, (b) `discover.mock.calls.length` continues to grow on subsequent intervals, (c) the run loop is still alive.

### F-003 (non-blocking, carried over from self-review). The "all providers removed" corner case never converges to empty.

`host.ts:359` returns early when `discovered.models.length === 0`, so if the user removes every provider from `opencode.json`, the runner keeps reporting the last non-empty set forever (never converging to empty on the server). The spec scenario "Removed provider disappears within one interval" (`spec.md:172-175`) is worded without qualifying "all vs. some" and is technically violated in this corner. Explicitly accepted by the issue body and by design D6 step 2; flagged here only because the spec scenario wording still doesn't carry the carve-out. Partial removal works correctly because the remaining set is non-empty and `opencodeModelSetsEqual` detects the change.

### F-004 (non-blocking, carried over from self-review). T-001 shipped alone is a transient regression in `connectRunner` retry cost; the two tasks must ship together.

`host.ts:820` calls `discoverOpencodeModels` inside `connectRunner`'s `while (!signal.aborted)` retry loop. With the in-module TTL guard removed, every retry now spawns `opencode models --verbose` instead of hitting the 30-min cache. Design D4 explicitly accepts this cost on the assumption that T-002 (which adds the periodic timer that justifies removing the TTL) ships together with T-001. The commits `b0d04548b` (T-001) and `c7acf433f` (T-002) are consecutive in this branch, so the combined change is fine — but anyone cherry-picking only T-001 would regress the connection-retry path.

### F-005 (non-blocking, style). The new spec reads private fields via a TypeScript cast.

`runner-host-model-rediscovery.spec.ts:135-138` defines `readState(host)` as `host as unknown as { coderModels: string[]; coderModelVariants: Record<string, string[]> }` to read the private fields `coderModels` / `coderModelVariants` from outside the class. This is a common test pattern and is also used in `runner-host-reporting.spec.ts`, so it is consistent with the existing test conventions — flagged only because it bypasses TypeScript's private-field protection, which means a rename of those fields would not be caught at compile time and would surface only as a failing test.

## Verdict

The implementation matches the design (D1–D7) and satisfies all seven of the issue's acceptance criteria as well as all testable scenarios in `specs/runner-model-discovery/spec.md`. Typecheck is clean and the full runner test suite (100 files / 1184 tests, including the new 6-scenario spec and the migrated 18-test `opencode-models.spec.ts`) passes. The findings above are documentation/coverage cleanups, not behavioral defects — the runner behavior the issue asked for is in place.

<promise>PASS</promise>
