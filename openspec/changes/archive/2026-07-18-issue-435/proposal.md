## Why

Runner registers its set of opencode coder models with the server exactly once at startup; from then on the server holds a stale snapshot. When the user edits `~/.config/opencode/opencode.json`, flips auth, or upgrades the opencode binary, the new models never appear in `/api/projects/{id}/opencode/models` until someone manually restarts the runner process — so users wire a stage to a model the runner already has but cannot find in the picker, or see a list that silently disagrees with `opencode models --verbose` on the runner host. The runner must keep the server-registered set in sync with what opencode actually exposes across the runner's lifetime, not just at process start.

## What Changes

- **Runner gains a periodic rediscovery timer.** A single 30-minute interval timer in `RunnerHost.run()` (alongside heartbeat / self-check / convergence / cleanup) re-runs the existing `discoverOpencodeModels` flow. First fire is 30 minutes after runner start — startup discovery already happens in `connectRunner`.
- **Change is the only trigger for an uplink.** After each rediscovery, the runner compares the new `coderModels` and `coderModelVariants` against the last-reported set using **order-insensitive, content-based** comparison (sort-then-compare or set semantics). When unchanged, no extra heartbeat is sent. When changed, the runner updates its local state and sends one immediate heartbeat through the existing channel.
- **BREAKING (internal): single trigger model.** The TTL cache guard inside `packages/runner/src/runtime/opencode-models.ts` (the 30-minute `now - cached.fetchedAt` check that currently gates on `Date.now()`) is removed. `discoverOpencodeModels` becomes a pure always-executes call; the only pacing for rediscovery is the periodic timer in the host. No external behavior breaks; this is a code-structure change documented for maintainers.
- **Failure does not clear state.** A rediscovery that throws (or returns empty) keeps the previously reported `coderModels` / `coderModelVariants` intact on both runner and server. The existing "do not cache empty result" rule in `opencode-models.ts` is preserved. The next tick retries.
- **Timer callback is exception-safe.** Errors in the timer fire are caught and logged; they do not bubble to an unhandled rejection and do not suppress the next tick.
- **Time is injectable.** Both the new timer's interval accounting and any remaining time judgment in the discovery module go through an injected clock (the host already runs under `vi.useFakeTimers()` in tests). No `Date.now()` in the new code path.
- **`coderModelVariants` refresh in lockstep.** When a provider's variant list changes (add/remove/rename), the server sees it on the next change-triggered heartbeat, not just the model id list.

## Capabilities

- `runner-model-discovery`: The runner-process behavior of (a) periodically rediscovering the set of coder models and variants opencode currently exposes, (b) comparing that set against the last-reported one order-insensitively, (c) pushing an immediate heartbeat only when the set changed, (d) leaving server-side state untouched on discovery failure, and (e) keeping the discovery module and the periodic timer both driven by an injectable clock so spec tests can advance time without a wall-clock sleep. Covers the invariants "runner-registered model set ≡ opencode-exposed model set within one TTL period" and "the periodic timer is the single trigger for rediscovery after startup".

## Impact

- **`packages/runner/src/runtime/host.ts`** — primary change surface:
  - Add a `rediscoveryTimer` (`setInterval`) registered in `run()` next to the existing timers (`host.ts:325-328`); cleared in the `finally` block (`host.ts:332-335`).
  - New private method (e.g. `runModelRediscoveryOnce(signal)`) that calls `discoverOpencodeModels`, does the set comparison against `this.coderModels` / `this.coderModelVariants` (`host.ts:96-97`), updates state and fires an immediate heartbeat (`sendImmediateHeartbeat`, `host.ts:397`) on change.
  - Timer interval comes from `RunnerOptions` (new field, default 30 min) so it is configurable and overridable in tests.
  - Time reads go through an injected clock rather than `Date.now()`.
- **`packages/runner/src/runtime/opencode-models.ts`** — remove the `CACHE_TTL_MS` / `cached.fetchedAt` TTL guard (`opencode-models.ts:9, 11, 22-25, 38-40`); `discoverOpencodeModels` always executes the underlying command and still skips caching on empty results. The `Date.now()` call at `opencode-models.ts:22` is deleted with the guard. `clearOpencodeModelsCacheForTesting` can stay as a no-op or be removed.
- **`packages/runner/src/core/types.ts`** — `RunnerOptions` gains the rediscovery interval (and an optional clock) field.
- **Tests** —
  - `packages/runner/tests/opencode-models.spec.ts`: drop the `cachesSuccessfulResultsForSubsequentCalls` TTL assertion (`opencode-models.spec.ts:137-153`); each call now executes the underlying runner.
  - New spec (or extension of `runner-host-lifecycle.spec.ts`) using `vi.useFakeTimers()` to assert: timer fires at the configured interval, rediscovery with no change does not call `connection.heartbeat` beyond baseline, rediscovery with a change triggers exactly one immediate heartbeat, rediscovery failure keeps prior state and does not throw out of the timer.
  - Existing runner-host spec wiring (`runner-host-lifecycle.spec.ts`, `runner-host-reporting.spec.ts`, etc.) injects a fake `discoverOpencodeModels` and the new interval; no real `opencode` invocation.
- **Server (`packages/server/`)** — no change. `RunnerInfo` / registration / `/api/projects/{id}/opencode/models` shape and behavior unchanged; the runner simply sends heartbeats on the existing channel when the model set changes.
- **CLI (`packages/cli/`)** — no change.
- **Web (`packages/web/`)** — no change.
- **Docs** — optional one-line note in the runner ops doc that the manual-restart workaround for stale models is no longer required (out of scope to update unless adjacent).
- **Risk** — low: change is localized to runner-side timing and conditional heartbeat; discovery parsing and server contract are untouched.
