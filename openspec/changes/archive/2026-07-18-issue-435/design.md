## Context

Today the runner discovers opencode coder models exactly once — inside `connectRunner` (`packages/runner/src/runtime/host.ts:804-822`) — and caches the result via a 30-minute TTL guard inside `opencode-models.ts` (`packages/runner/src/runtime/opencode-models.ts:9-42`). Nothing in the runner's event loop ever invokes `discoverOpencodeModels` again after that initial call: the heartbeat timer (`host.ts:325`) just uplinks the cached snapshot, so any opencode-side change (new provider, auth refresh, opencode upgrade) is invisible to the server until the runner is manually restarted. The TTL guard at `opencode-models.ts:22-25` exists but its "expire and re-run" branch is unreachable in practice — no caller paces on that TTL.

The fix is runner-local: add a periodic rediscovery timer, drop the unreachable TTL guard, push an uplink only when the discovered set actually changed. Server contract, parsing logic, and the model-"use" path are untouched. See `proposal.md` for motivation and `specs/runner-model-discovery/spec.md` for normative requirements.

Relevant existing surfaces:
- `packages/runner/src/runtime/host.ts:84-165` — `RunnerHost` constructor; already stores per-host state including `coderModels` / `coderModelVariants` (`host.ts:96-97`).
- `packages/runner/src/runtime/host.ts:304-342` — `run()`; the four existing interval timers (`heartbeat`, `selfCheck`, `convergence`, `cleanup`) registered at `host.ts:325-328` and cleared at `host.ts:332-335`.
- `packages/runner/src/runtime/host.ts:397-405` — `sendImmediateHeartbeat()`; already the channel used on SignalR reconnect; reused for change-triggered uplinks.
- `packages/runner/src/runtime/host.ts:794-802` — `registrationState()`; produces the body sent on every heartbeat, already includes `coderModels` / `coderModelVariants`.
- `packages/runner/src/runtime/opencode-models.ts` — discovery + parser; lines 9-42 hold the TTL guard to remove.
- `packages/runner/src/core/types.ts:267` — `RunnerOptions`; already follows the "optional `*IntervalMs` field with internal default" pattern (`cleanupConvergenceIntervalMs`, `cleanupLoopIntervalMs`, `taskLogFlushIntervalMs`).
- `packages/runner/tests/runner-host-lifecycle.spec.ts` — already uses `vi.useFakeTimers()` (`:115`) and hoists `discoverOpencodeModels` as a mock (`:90-92`); the pattern for the new spec scenarios.

## Goals / Non-Goals

**Goals:**
- Make the runner's server-registered model set converge with opencode's currently exposed set within one rediscovery interval, with no manual restart.
- Single, explicit trigger for rediscovery after startup (one periodic timer), replacing the unreachable internal TTL guard.
- Push at most one heartbeat per change; never push on unchanged rediscovery; never push on failure.
- Preserve the existing "don't cache empty result" semantics so transient opencode hiccups never wipe the server-registered set.
- Keep the rediscovery path free of wall-clock reads so spec tests drive it via `vi.useFakeTimers()`.

**Non-Goals:**
- Do not change `opencode models --verbose` parsing.
- Do not change server-side `RunnerInfo`, registration, heartbeat endpoint, or `/api/projects/{id}/opencode/models` shape.
- Do not introduce a new IPC/RPC/push channel — uplinks ride the existing heartbeat.
- Do not change the runner's model-"use" path (model selection happens via Action Input, not the registered list).
- Do not tighten `connectRunner`'s initial-empty-result overwrite (host.ts:807-809) — out of scope; the bug is about post-startup staleness.
- Do not add sub-minute real-time push; 30-minute convergence is acceptable.

## Decisions

### D1. Single periodic `setInterval` in `run()`, alongside the existing timers

Add `rediscoveryTimer = setInterval(() => void this.runModelRediscoveryOnce(signal).catch(...), this.modelRediscoveryIntervalMs)` next to `host.ts:325-328`, and `clearInterval(rediscoveryTimer)` in the `finally` block at `host.ts:332-335`.

**Rationale**: matches the existing pattern (heartbeat / selfCheck / convergence / cleanup are all bare `setInterval` with `void … .catch(log)` wrappers), keeps teardown in the same `try/finally`, and is what the spec scenario "Timer is cleared when the run loop ends" asserts.

**Alternatives considered**:
- *Piggyback on the heartbeat timer.* Rejected: spec mandates "the periodic timer SHALL be the sole trigger" and the heartbeat cadence (5 s) is far too fast to spawn `opencode models --verbose` on.
- *A dedicated `setTimeout` recursion loop.* Rejected: adds drift bookkeeping and re-registration complexity for no gain over `setInterval`.

### D2. First fire at `+interval`, not at `0`

`setInterval`'s semantics already give us this: the first fire happens one interval after registration. We do not call `runModelRediscoveryOnce` from `run()` before entering the timer.

**Rationale**: `connectRunner` already runs discovery once at startup (`host.ts:807`); firing again at T=0 wastes one `opencode models --verbose` spawn. Spec scenario "Timer first fires one interval after runner start" codifies this.

### D3. Interval is a new optional `RunnerOptions.modelRediscoveryIntervalMs`, default 30 min

Add `modelRediscoveryIntervalMs?: number` to `RunnerOptions` (`packages/runner/src/core/types.ts:267`); the host clamps to `Math.max(60_000, options.modelRediscoveryIntervalMs ?? 30 * 60_000)` in the constructor, alongside the existing `cleanupConvergenceIntervalMs` / `cleanupLoopIntervalMs` clamps (`host.ts:123-124`).

**Rationale**: 30 min matches the previous TTL constant; the optional-field pattern matches the existing cadence overrides and lets tests drive the timer deterministically. The `Math.max(60_000, …)` floor prevents accidental hot-looping in production while still allowing tests to use small values.

**Alternatives considered**:
- *Hard-coded `const MODEL_REDISCOVERY_INTERVAL_MS = 30 * 60_000`.* Rejected: forces tests to either real-wall-clock sleep or to monkey-patch a constant; the optional-field pattern is already established and cleaner.
- *Env var override only (no `RunnerOptions` field).* Rejected: diverges from the established `RunnerOptions` pattern.

### D4. Remove the internal TTL guard in `opencode-models.ts`; `discoverOpencodeModels` always executes

Delete `CACHE_TTL_MS`, the `cached` variable, the `cached.fetchedAt` accounting, the `Date.now()` call, and the `if (cached !== null && now - cached.fetchedAt < CACHE_TTL_MS) return cached.result` short-circuit (`opencode-models.ts:9, 11, 22-25, 38-40`). Keep the `result.models.length > 0` check semantics by repurposing it: the host (the only caller that cares about caching semantics) decides what to do with an empty result. The discovery function becomes "always spawn → parse → return"; on parse/spawn error, return `{ models: [], variants: {} }` as today. Drop `clearOpencodeModelsCacheForTesting` (no longer applicable). Migrate the existing test that asserts TTL caching (`packages/runner/tests/opencode-models.spec.ts:137-153`) to assert that every call spawns the command.

**Rationale**: spec requirement "Discovery module always executes the underlying command". The TTL guard was the root cause — it pretended to provide rediscovery but no caller ever drove the expiration branch. With a single periodic timer as the trigger, an in-module TTL is both redundant and confusing (two clocks that don't agree).

**Alternatives considered**:
- *Keep the TTL guard and have the timer call discovery knowing it'll hit cache most of the time.* Rejected: forces both clocks to agree, and the bug we're fixing is precisely that they don't.
- *Keep the TTL guard but add a "force refresh" parameter.* Rejected: every call site would pass `force: true`; the parameter would be dead weight.

### D5. Order-insensitive comparison as a pure helper in `opencode-models.ts`

Export `opencodeModelSetsEqual(a: DiscoveredOpencodeModels, b: DiscoveredOpencodeModels): boolean` from `opencode-models.ts`. Implementation: compare sorted `models` arrays; for `variants`, compare key sets and, per shared key, compare sorted variant arrays. The host calls it inside `runModelRediscoveryOnce` against the current `coderModels` / `coderModelVariants`.

**Rationale**: comparison operates on the discovered-models shape, so it belongs next to `DiscoveredOpencodeModels`. Pure function → trivially unit-testable in isolation.

**Alternatives considered**:
- *Inline in `host.ts`.* Rejected: pushes a non-trivial bit of logic into the already-large host class and removes the easy unit-test surface.
- *Use a generic `deepEqual` after sorting in place.* Rejected: spec mandates a specific order-insensitive semantic; an explicit helper makes the intent unambiguous.

### D6. Change-gated uplink reuses `sendImmediateHeartbeat`

In `runModelRediscoveryOnce`:
1. `const discovered = await discoverOpencodeModels(signal)`.
2. If `discovered.models.length === 0`, return without touching state or sending a heartbeat (failure / empty → preserve prior state per spec "Discovery failure preserves previously registered state").
3. If `opencodeModelSetsEqual(discovered, { models: this.coderModels, variants: this.coderModelVariants })`, return without sending.
4. Otherwise set `this.coderModels = discovered.models` and `this.coderModelVariants = discovered.variants`, then `await this.sendImmediateHeartbeat()`.

**Rationale**: `sendImmediateHeartbeat` (`host.ts:397-405`) is already the channel used on SignalR reconnect and already reads `registrationState()` (which includes `coderModels` / `coderModelVariants`). Reusing it means no new transport code, satisfies "Server contract is unchanged", and gives us the established `try/catch + log` error handling for free.

**Alternatives considered**:
- *Call `connection.heartbeat(...)` directly.* Rejected: bypasses the abort-signal guard at `host.ts:398-399`.
- *Trigger the next heartbeat-timer fire early instead of sending immediately.* Rejected: couples two timers and adds latency variance; the spec wants "exactly one immediate heartbeat".

### D7. Timer callback wraps the fire in `try/catch`; no clock abstraction object

Wrap as `setInterval(() => void this.runModelRediscoveryOnce(signal).catch((error) => console.error("model rediscovery fire failed", error)), this.modelRediscoveryIntervalMs)`. `runModelRediscoveryOnce` itself is `async` and never rethrows to the timer.

**No new clock abstraction**: the only time source in the new path is `setInterval` itself, which `vi.useFakeTimers()` (already active in `runner-host-lifecycle.spec.ts:115`) intercepts natively. The one `Date.now()` in the old path lived inside the TTL guard and is removed by D4, leaving zero wall-clock reads.

**Rationale**: the rest of `host.ts` (lines 325-328) already relies on `vi.useFakeTimers()` to drive `setInterval`/`clearInterval`; introducing a clock object just for this feature would diverge from the established pattern and add a constructor parameter for no behavioral benefit. Spec requirement "Time is injectable" is satisfied by the absence of `Date.now()` plus fake-timer interception.

**Alternatives considered**:
- *Thread a `TimeProvider` through `RunnerHost`.* Rejected: none of the other timers use one; would be inconsistent and over-engineered for a path whose only time read is `setInterval`.
- *Use `setTimeout` recursion with a manual `lastFireAt`.* Rejected: reintroduces a `Date.now()` read for no gain.

## Risks / Trade-offs

- **[30-min convergence window feels long to users editing config]** → Mitigation: matches the existing TTL constant the code already documented; the issue explicitly accepts "30 分钟级 TTL 对本场景已经够用". Configurable via `RunnerOptions.modelRediscoveryIntervalMs` if an operator wants shorter.
- **[Transient opencode failure keeps stale server state for one interval]** → Mitigation: failure is logged; next tick retries. The alternative (wipe server-side set on failure) is worse — it breaks every running workflow's model picker.
- **[First fire at +30 min means a config edit at T=+1s waits ~30 min]** → Mitigation: acceptable per issue; the prior behavior required a full process restart (effectively infinite convergence time).
- **[Two test mocks (`discoverOpencodeModels` and `connection.heartbeat`) must be coordinated in the new spec]** → Mitigation: existing pattern (`runner-host-lifecycle.spec.ts:90-92` and `:114-138`) already does this; new scenarios reuse the same setup.
- **[Removing `clearOpencodeModelsCacheForTesting` is a breaking change to the test API]** → Mitigation: only consumer is the test being migrated in the same change; no external callers.
- **[`connectRunner` still overwrites local state with an empty initial result — host.ts:807-809]** → Mitigation: explicitly non-goal (D6 / Goals). Preserved as pre-existing behavior; the bug this change fixes is about post-startup staleness, not initial-empty.

## Migration Plan

This is a pure runner-side change. No server, CLI, or web changes; no schema migration; no config-file migration.

**Deploy**:
1. Build the new runner binary (`npm run build`).
2. Restart the runner process once (`systemctl --user restart mohist-runner`). This is the same workaround users were already applying manually — after this deploy, it is the last time a restart is needed for an opencode config change.
3. After restart, verify: edit `~/.config/opencode/opencode.json` (add a provider), wait one rediscovery interval, `curl /api/projects/{id}/opencode/models` and confirm the new model appears.

**Rollback**:
1. Revert the runner binary to the previous build and restart the runner process.
2. Old "snapshot at startup" behavior returns. No server-side cleanup needed — server state is whatever the last heartbeat left it at, which is correct under the old behavior.
3. No data migration, no orphans.

## Open Questions

- **Should we surface `modelRediscoveryIntervalMs` as a CLI flag / env var for operators?** Not required for this fix; the optional `RunnerOptions` field is enough for tests and for any future operator override. Defer until an operator actually asks.
- **Should `connectRunner` (host.ts:807-809) skip the initial state overwrite when discovery returns empty, matching the new post-startup semantics?** Explicitly out of scope per the issue (initial-empty is a different bug). Worth a follow-up issue if observed in practice.
- **Should the new spec live in `runner-host-lifecycle.spec.ts` or a dedicated `runner-host-model-rediscovery.spec.ts`?** Default to a dedicated file — the scenarios are scoped and the file is already 741 lines; a new file keeps both readable. Final call happens at implementation time.
