## Context

`mo update`'s `VerifyRuntime` stage (`MohistCliCommands.Update.Stages.cs:193`) runs a fixed sequence of runtime consistency checks to confirm the freshly built/restarted components are actually live. Each check resolves to `Pass`/`Warn`/`Fail` and is printed on its own line as `[ok]`/`[warn]`/`[fail]`.

The runner connection check today (`RuntimeConsistencyValidator.cs:152`, `CheckRunnerConnectionAsync`) only asserts that `/api/system/info` reports `services.runner == "active"`. It never compares the runner's deployed `buildGitHash` to the source HEAD it was built from. As a result a runner can ship a stale binary (e.g. an earlier `build-info.json` with an old git hash) and still report `[ok] Runner connection: Runner service is active`, masking the drift until workflows misbehave.

The data to catch this already exists end-to-end:

- The runner reports `buildGitHash` via SignalR handshake + heartbeat (`RunnerHub.cs:28`), persisted on the `RunnerGrain` (`RunnerGrain.cs:347`).
- The server exposes it at `GET /api/runner/identity` (`RunnerIdentityRoutes.cs:12`, returns `RunnerIdentityView.BuildGitHash`).
- `mo update` already reads this exact endpoint elsewhere in the pipeline (`RunnerRefreshOutcome.cs:228`, `TryReadRunnerIdentityAsync`) to confirm runner restart.

The only missing piece is an identity check symmetric to the existing server check (`CheckServerIdentityAsync`, `RuntimeConsistencyValidator.cs:76`), which already compares `data.running.gitHash` from `/api/system/info` against `git rev-parse HEAD`.

**Stakeholders:** developers running `mo update` on a managed single-host deployment (server + runner co-located).

**Constraints:**

- Server API contract and runner reporting mechanism are out of scope (see Non-Goals).
- The change is confined to the CLI (`packages/cli`).
- `RuntimeConsistencyValidator` is `internal` and unit-tested directly — no DI/facade changes needed beyond wiring the new check into the sequence.

## Goals / Non-Goals

**Goals:**

- Add a `CheckRunnerIdentityAsync` check to `VerifyRuntime`, behaviorally symmetric to `CheckServerIdentityAsync`, comparing the runner's reported `buildGitHash` to source HEAD.
- Slot it into the fixed check sequence after the runner connection check, so identity is layered on top of (not replacing) the `active`-state check.
- Surface drift as a `[warn]` (non-blocking), covering the four states: match → Pass, mismatch → Warn, hash missing → Warn, endpoint unreachable → Warn.
- Cover the new behavior with unit tests in `RuntimeConsistencyValidatorSpecs.cs` (three outcome states) and extend the orchestration/dry-run assertions.
- Formalize the previously-implicit `update-runtime-consistency` capability as a spec (done in the proposal/specs — this design covers the implementation).

**Non-Goals:**

- Diagnosing why a prior `mo update` produced a `build-info.json` with a stale hash (transient, hard to repro).
- Changing `write-build-info.mjs` hash extraction or the runner's reporting mechanism.
- Extending `/api/system/info` to embed runner `buildGitHash` (the dedicated `/api/runner/identity` endpoint already serves it).
- Making runner identity a `Fail`. The runner may still be reconnecting post-restart; only the connection (`active`-state) check may Fail.

## Decisions

### D1. New check is a peer method mirroring `CheckServerIdentityAsync`, not an extension of `CheckRunnerConnectionAsync`

Add `internal Task<RuntimeCheckResult> CheckRunnerIdentityAsync(UpdateContext, CancellationToken)` to `RuntimeConsistencyValidator`. Keep `CheckRunnerConnectionAsync` untouched.

**Rationale:** The two checks answer different questions — "is the runner service up?" (connection) vs. "is the runner running the code we just built?" (identity). They resolve independently and print on separate lines, exactly as server connection (implicit, via `/api/system/info` reachability) and server identity are already separated. Folding them into one method would collapse two distinct outcomes into one message and break the one-line-per-check reporting contract.

**Alternative considered:** Generalize `CheckRunnerConnectionAsync` to also compare hashes. Rejected — it would overload a `Fail`-capable check (connection) with `Warn`-only semantics (identity), confusing the aggregation logic.

### D2. Read runner hash from `GET /api/runner/identity`, not from `/api/system/info`

The check calls `/api/runner/identity` and reads `data.buildGitHash` from the response, reusing the request/parse shape already proven in `RunnerRefreshOutcome.TryReadRunnerIdentityAsync`.

**Rationale:** `/api/runner/identity` is the endpoint that already exposes `buildGitHash` with no server change required. `/api/system/info` does not carry the runner hash and would need a server-side contract change — explicitly a Non-Goal.

**Alternative considered:** Extend `/api/system/info` to embed runner `buildGitHash` so both identity checks share one HTTP round-trip. Rejected — out of scope (server API change) and the extra request is one cheap localhost call during an update.

### D3. The check never resolves to `Fail` — deliberate asymmetry with the server check

`CheckServerIdentityAsync` resolves `Fail` when `/api/system/info` is unreachable. The runner identity check resolves `Warn` for the unreachable case instead.

**Rationale:** `VerifyRuntime` runs immediately after runner restart. The runner may still be reconnecting over SignalR when the check executes, so a transiently-absent identity must not block the update. The connection check (`CheckRunnerConnectionAsync`) already gates hard liveness via `Fail` when the service is non-`active`; identity is an additive, advisory layer. This matches the spec's explicit "never Fail" requirement and the acceptance criterion "runner 未上报 hash → Warn, 不阻塞".

### D4. Hostname resolution relies on the server default

Call `/api/runner/identity` without a `hostname` query param. The server defaults `hostname` to `Environment.MachineName` (`RunnerIdentityRoutes.cs:16`).

**Rationale:** `mo update` is a managed single-host deployment tool — the CLI runs on the same host as the runner, so the server's `Environment.MachineName` and the runner's registered hostname coincide. This matches the existing `CheckRunnerConnectionAsync`/`CheckServerIdentityAsync` posture, which also make no host distinction.

**Alternative considered:** Pass `?hostname=<local machine name>` explicitly from the CLI for robustness against multi-runner setups. Rejected as scope creep — Mohist is single-runner-per-host today; adding hostname plumbing now would be speculative. If multi-runner lands, revisit.

### D5. Reuse `TryGetSourceHeadAsync(context)` so server and runner identity share one resolved HEAD

The new check calls the existing private `TryGetSourceHeadAsync(context)`, which memoizes into `context.SourceHead`. Since `CheckServerIdentityAsync` runs earlier in the sequence and populates `context.SourceHead`, the runner check reuses the cached value without a second `git rev-parse` round-trip.

**Rationale:** Single source of truth for "the HEAD we built from"; identical comparison semantics to the server check; no new git invocation.

### D6. Parse the runner identity JSON inline with a small private snapshot type

Add a private `RunnerIdentitySnapshot { BuildGitHash }` and a small `TryGetRunnerIdentityAsync(token)` helper inside `RuntimeConsistencyValidator`, mirroring the existing `SystemInfoSnapshot` / `TryGetSystemInfoAsync` pattern.

**Rationale:** Keeps the validator self-contained and consistent with its current private-snapshot style. The response payload is trivial (one field of interest).

**Alternative considered:** Reuse `RunnerIdentityView` + `TryReadRunnerIdentityAsync` from `RunnerRefreshOutcome.cs`. Rejected — that type/reader is a private implementation detail of a different collaborator and carries fields (status, connectionState, lastHeartbeatAt) the identity check doesn't need. Extracting a shared client would be a larger refactor with no payoff for a one-field read; revisit if a third consumer appears.

### D7. Wire the check into the sequence and update the dry-run line

In `VerifyRuntimeStageAsync` (`MohistCliCommands.Update.Stages.cs:207`), insert `await _validator.CheckRunnerIdentityAsync(context, token)` into the `checks` list **after** `CheckRunnerConnectionAsync` and **before** `CheckManagedSkillAssetsAsync`, matching the spec-mandated order. Update the dry-run message (`:201`) to name "runner identity" alongside the other checks.

No change to aggregation: the existing `Any(Fail)` / `Any(Warn)` logic already handles the new entry because the check only emits `Pass`/`Warn`.

## Risks / Trade-offs

- **[Runner reports stale-but-`active` shortly after restart]** -> The check may Warn on a transient mismatch right after restart while the grain is still showing the pre-restart registration, then resolve on a subsequent `mo update`. Mitigated by `Warn` (non-blocking) semantics; the message quotes both hashes so the user can judge. This is the intended behavior, not a bug.
- **[Hostname default mismatch on atypical deployments]** -> If the CLI host and runner host differ, `/api/runner/identity` (server-defaulted hostname) may return a different (or no) runner. Mitigated by D4 scope (single-host managed deployments); if violated the check Warns rather than mis-reports Pass.
- **[Extra HTTP round-trip per update]** -> One additional localhost `GET /api/runner/identity` during `VerifyRuntime`. Negligible; updates are human-initiated and infrequent.
- **[Inline JSON parsing duplicates logic in `RunnerRefreshOutcome`]** -> Two small readers for the same endpoint. Acceptable for a one-field read; consolidated if a third consumer emerges. Tracked as a trade-off, not a defect.
- **[Test wiring]** -> Existing unit tests stub responses by `AbsolutePath`. Adding a second stubbed path (`/api/runner/identity`) is straightforward via `RecordingHttpHandler`; orchestration-level tests in `UpdateSpecs.cs` that script the full sequence will need an additional queued response.

## Migration Plan

**Deploy:** No schema, config, or API migration. The change is CLI-only and ships in the next `mo update` build. Once deployed, subsequent `mo update` runs emit the new `[ok|warn] Runner identity:` line.

**Activation:** Immediate on next CLI run; no flag, no opt-in.

**Rollback:** Revert the CLI changes. The check is purely additive; removing it returns `VerifyRuntime` to the prior five-check sequence with no state to clean up. Server and runner are untouched, so rollback has no cross-component impact.

## Open Questions

- Should the runner identity check retry briefly (e.g. poll `/api/runner/identity` for a few hundred ms) when the first read returns a stale hash, given the runner may still be reconnecting? Current design resolves immediately to `Warn` to keep `VerifyRuntime` fast and predictable; `RunnerRefreshOutcome` already performs the waiting/reconnect loop earlier in the pipeline, so by `VerifyRuntime` the runner is expected to have re-registered. Confirm this ordering assumption holds during implementation.
