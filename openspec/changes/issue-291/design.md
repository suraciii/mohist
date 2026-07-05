## Context

Runner executes `git` / `gh` external commands through one chokepoint — `runCommand` (`packages/runner/src/system/process.ts:62`) — and today every call runs only under the caller-supplied work-level `AbortSignal`. There is no per-command timeout. A hung network call (`git fetch`, `git push`, `gh pr create`) is invisible until the 30-minute server-side work timeout fires, and when it does the cleanup is incomplete: `spawn` is **not** detached (`process.ts:71`), so `killProcess` (`process.ts:136`, only `child.kill("SIGTERM")`) never reaches helper processes like `git-remote-http` that the direct child spawned — they leak.

The fix already has a primitive to lean on: `timeoutSignal(parent, timeoutMs)` (`packages/runner/src/actions/registry.ts:190`) layers a timeout over a parent `AbortSignal` and is used today by `core/script` (`registry.ts:88`). This change promotes that primitive into `runCommand` as a per-command `timeoutMs`, switches the spawn to process-group semantics so the whole subtree can be reaped, and applies the resulting knob to the network-bound call sites identified in the proposal.

Stakeholders / scope:
- **Runner only** (`packages/runner`). Server / Web / CLI are unchanged — this is runner-local execution resilience. No DB, no wire schema, no workflow YAML additions (per Non-Goals).
- The delivery actions (`push.ts`, `rebase.ts`, `create-github-pr.ts`, `mark-github-pr-ready.ts`, `merge-github-pr.ts`, `github-pr-status.ts`) and `runtime/workspace.ts` are the call sites that receive the network default.
- The existing `retry-safe` classifier arm (`actions/github-pr-classify.ts:58`, already matches `timeout` / `timed out`) absorbs the new structured output with **no rule changes**.

Constraint carrying over from `design/testing.md`: no real network, no real `git`/`gh`, no wall-clock. The chosen mechanism must be fake-timer-driven and exercised through the existing injection seams (`setXxxGitRunnerForTest`, `setGitHubPrGhRunnerForTest`, …).

## Goals / Non-Goals

**Goals:**
- `runCommand` gains an optional `timeoutMs`; on expiry it kills the **entire subprocess tree** (detached spawn + negative-PID group kill) and resolves a **structured timeout result** distinguishable from a normal non-zero exit.
- One network default timeout (≈120s) is applied to network-bound runner commands (`git clone` / `fetch` / `ls-remote` / `push`, plus `gh` API calls). A network timeout surfaces step name + command summary + duration and classifies `retry-safe` through the existing path.
- `core/script` and `mohist/acp-agent` `with.timeout` behavior is preserved byte-for-byte; the new knob is orthogonal.
- All timeout behavior is verifiable through fake timers + controlled subprocesses / existing runner seams — no real network, no wall-clock.

**Non-Goals** (per proposal):
- No per-command-type timeout budget table (clone vs fetch vs push share one default).
- No new `fetchTimeout` / `pushTimeout` / … YAML override fields. At most one `with.commandTimeout` later, only if needed.
- No change to the server-side 30-minute work completion timeout.
- No automatic retry loop.
- No change to local-only git commands (`rev-parse`, `status`, `checkout`, `diff`, `merge-base`, `reset`, `rebase`/`--abort`, `merge --abort`, `cherry-pick --abort`, `cat-file`, `ls-tree`, `show-ref`, `fsck`, `branch`, `add`, `commit`, `remote get-url`) — they cannot hang on the network and keep using only the work-level signal.

## Decisions

### D1 — `timeoutMs` rides in `CommandLineOptions`, not a new positional parameter

`runCommand(command, args, cwd, signal, env?, options?)` already takes an `options?: CommandLineOptions` bag (`process.ts:32`, currently `{ onLine?, onClose? }`). Add `timeoutMs?: number` to that bag rather than introducing a 7th positional.

- **Rationale:** the `gh` runner seams are typed `GhRunner = typeof runCommand` (`github-pr-runtime.ts:6`, `github-pr-status.ts:6`, `mark-github-pr-ready.ts:10`, `merge-github-pr.ts:10`, `create-github-pr.ts:19`). A positional would force every fake signature in every spec to grow a parameter; folding into `options` keeps the seam contract stable and lets `gh` call sites write `{ timeoutMs: NETWORK_TIMEOUT, onLine }` in one place. `options` already carries orthogonal knobs (`onLine` / `onClose`), so `timeoutMs` is the same kind of citizen.
- **Behavior contract (from spec):** omitted or non-positive `timeoutMs` ⇒ no timer is armed and the resolved object is the existing `{ exitCode, stdout, stderr }` byte-for-byte (no new key serialized). This guarantees zero impact on the 29 existing call sites that do not opt in.
- **Alternative considered:** a 7th positional `timeoutMs?`. Rejected for the seam-ergonomics reason above.

### D2 — `git.ts` threads `timeoutMs` through `GitOptions`; network policy lives at the call site, not in the primitive

`actions/git.ts:27` wraps `runCommand` for every delivery action. Extend `GitOptions` with `timeoutMs?: number` and have `git()` forward it into `CommandLineOptions.timeoutMs`. The delivery-action seams (`setPushGitRunnerForTest`, `setRebaseGitRunnerForTest`, `setGitHubPrGitRunnerForTest`) replace this `git()` function, so they sit **above** `runCommand`.

- **Rationale (placement):** per `design/architecture.md`, the primitive (`runCommand`) provides the knob; the **policy** (which commands get which timeout) is decided at the call site. `runCommand` MUST NOT know that `git clone` is network but `git rev-parse` is not (spec: "The timeout does not by itself decide which commands are timed"). The single source of the network default is one exported constant (e.g. `NETWORK_COMMAND_TIMEOUT_MS = 120_000`) imported by the network call sites; local call sites simply omit `timeoutMs`.
- **Testing consequence (important):** because the action seams replace `git()` / `gh()` wholesale, they **cannot** exercise `runCommand`'s internal timeout. This forces a clean two-layer test split (see D5):
  - The **primitive** (`runCommand` timeout + group kill) is proven directly against a controlled real child under fake timers — no seam can reach it.
  - The **policy** (network vs local call sites) is proven through the seams by asserting the `options.timeoutMs` argument each call site passes (`120000` for network, `undefined` for local).
- **Alternative considered:** route every call through `runCommand` directly and drop `git.ts`. Rejected — `git.ts` also normalizes `success` / `combinedOutput` and is the seam surface; removing it is out of scope and would churn every action.

### D3 — Detached spawn + negative-PID group kill; `killProcess` hardened to reap the group

Switch `spawn(...)` in `runCommand` (`process.ts:71`) to `detached: true` so the child leads its own process group. Do **not** `unref()` — the parent still awaits `close`. On termination (timeout **or** parent abort) call `process.kill(-child.pid, "SIGTERM")` to signal the whole group, so `git-remote-http` / `git-remote-https` die with the parent instead of being orphaned.

- **`killProcess` hardening (spec requirement):** `killProcess` (`process.ts:136`) is also called by ACP (`runtime/acp-connection.ts:138`, `actions/acp/process.ts:83`). Change it to attempt group kill first (`process.kill(-child.pid, sig)`) with fallback to `child.kill(sig)` on error (ESRCH/EINVAL), so any detached child is reaped regardless of who spawned it. This also closes today's leak on **work-level abort**: currently `spawn({ signal })` only SIGTERMs the direct child; once spawns are detached, the abort path also needs group kill. `runCommand` therefore group-kills on **both** timeout and parent-signal abort.
- **Platform guard:** negative-PID kill is a POSIX concept and throws on Windows. Gate group kill behind `process.platform !== "win32"` (direct `child.kill` on Windows). Runner is Linux-first; see Risks.
- **Signal choice:** SIGTERM, matching the existing `killProcess`. Considered SIGKILL-after-grace (more reliable against a wedged git, but loses captured output tail and is harsher); deferred to Open Questions.

### D4 — Structured timeout result via optional fields; classifier unchanged

Model the result as the existing `CommandResult` plus two optional fields, present **only** on timeout:

```ts
interface CommandResult {
  exitCode: number
  stdout: string
  stderr: string
  status?: "timeout"   // absent on normal exit (byte-identical today)
  timeoutMs?: number   // absent on normal exit
}
```

- **Byte-identical normal path:** on a normal exit the object is constructed exactly `{ exitCode, stdout, stderr }` (the optional keys are not assigned), so JSON serialization and every existing reader are unchanged. `git()` propagates the two new fields unchanged; `success` stays `exitCode === 0` (on timeout `exitCode` is the killed code, i.e. non-zero, so `success = false` naturally).
- **Distinguishability:** callers that care check `result.status === "timeout"`. No stderr parsing required (spec: "distinguishable … without parsing stderr").
- **Classifier feeding:** on timeout, `runCommand` appends a sentinel line to the captured stderr — `Command timed out after ${timeoutMs/1000}s` — while preserving everything captured up to the kill (spec: "Captured output is preserved up to the kill"). The existing `looksLikeRetrySafe` (`github-pr-classify.ts:58`) already matches `timed out`, so `classifyGhFailure` / `classifyPushFailure` return `retry-safe` with **no rule change and no new `GitHubPrErrorCode` arm**. The structured fields (`status`, `timeoutMs`) are how callers detect "this was a timeout"; the sentinel text is how the unchanged classifier absorbs it. The two mechanisms are deliberately complementary.
- **Step name + command summary + duration (network policy):** these are surfaced by the **delivery actions**, not by `runCommand`. Each network call site already runs under a phase label (e.g. `git-fetch-base`, `gh-pr-create`, `git-push`) recorded via `record(step, summary, exitCode, output)` (see `create-github-pr.ts`). When a network command's result carries `status === "timeout"`, the action records the step name (existing label), the command summary (existing safe summary, no secrets), and the duration (`result.timeoutMs`) into its existing structured output — flowing into the same JSON the CLI delivery-failure guidance and web delivery-failure view already render. No new transport field.

### D5 — Reuse `timeoutSignal` inside `runCommand`; fake-timer-testable by construction

`runCommand` builds its layered signal by calling the existing `timeoutSignal(parent, options.timeoutMs)` when `timeoutMs > 0`, else uses the caller signal directly — the same helper `core/script` uses (`registry.ts:88,190`). On the layered signal's abort:

- If `layeredSignal.reason` is the timeout error (`/Timed out after/`, the exact reason string `timeoutSignal` already aborts with at `registry.ts:201`) ⇒ group-kill the child, then **resolve** with the structured timeout result (`status: "timeout"`, `timeoutMs`, captured output + sentinel).
- Else (parent propagated its own reason) ⇒ group-kill the child, then preserve today's behavior (reject via the spawn `error` event).

- **Rationale:** one implementation of signal-layered timeout (spec: "reused, not duplicated"). Because `timeoutSignal` is just `setTimeout` + `AbortController`, `vi.useFakeTimers()` (the established pattern — `cleanup-loop.spec.ts`, `merge-github-pr.spec.ts:655`, `runner-host-cleanup-config.spec.ts`) intercepts the timer globally, so advancing the fake clock fires the timeout deterministically. No wall-clock.
- **Alternative considered:** a bespoke `setTimeout` inside `runCommand`. Rejected — duplicates `timeoutSignal` and the spec forbids it.

### D6 — Test split (spec compliance)

- **`runCommand` primitive** — a new spec (e.g. `tests/system-process-timeout.spec.ts`). Under `vi.useFakeTimers()`, spawn a **real controlled child** that hangs and (for the tree-reap case) itself spawns a sub-child: a tiny inline `node -e` script, never real `git`/`gh`, never network. Advance the fake clock past `timeoutMs`. Assert: `status === "timeout"`, `timeoutMs` recorded, captured stdout preserved + sentinel present, and both PIDs are no longer alive (`process.kill(pid, 0)` throws ESRCH). The kill call itself is real; only the **timer** is fake. Covers: hung direct child killed, helper subprocess reaped with parent, `killProcess` reaps group for detached child, captured output preserved.
- **Network policy** — extend the existing action specs (`push.spec.ts`, `rebase.spec.ts`, `create-github-pr.spec.ts`, `github-pr-status.spec.ts`, `mark-github-pr-ready.spec.ts`, `merge-github-pr.spec.ts`, `workspace-prepare*.spec.ts`). The fake `GitRunner` / `GhRunner` records the `options.timeoutMs` argument; assert network call sites (`clone`, `ls-remote`, `fetch`, `push`, `gh pr list/edit/create`, precheck) are invoked with `120000` and local sites (`rev-parse`, `status`, `checkout`, `merge-base`, `reset`, `rebase`, …) with `undefined`. For the structured-failure → `retry-safe` path, the fake returns a `CommandResult` shaped exactly as D4 produces (`status: "timeout"`, stderr containing `timed out`); the action output is asserted to carry the right `failureKind: "retry-safe"`, step name, summary, and duration — proving the contract between primitive and classifier without invoking `runCommand`.

## Risks / Trade-offs

- **[Negative-PID kill throws on Windows]** → Mitigation: platform guard (`process.platform !== "win32"` ⇒ direct `child.kill`). Runner is Linux-first; on Windows the group reap degrades to direct-child kill (today's behavior). Documented; no Windows CI regression because group kill is best-effort with fallback.
- **[Group kill races child exit (ESRCH between timer fire and `process.kill`)]** → Mitigation: wrap `process.kill(-pid)` in try/catch; treat ESRCH as "already reaped, success". The promise still resolves with the structured timeout result because the **timer** fired, regardless of whether the kill landed.
- **[120s default may be tight for large `git clone` on slow links]** → Mitigation: one shared constant, trivially tuned in one place; the spec explicitly trades per-command budgets for maintainability. If clones regress, raise the single default rather than add a budget table (Non-Goal). Tracked under Open Questions.
- **[SIGTERM may be ignored by a wedged helper (`git-remote-http` in a bad state)]** → Mitigation: captured-so-far output is preserved; the structured result still resolves. A SIGKILL-after-grace escalation is the considered stronger alternative (Open Questions) — not adopted now to keep behavior closest to today's `killProcess`.
- **[Detached spawn subtly changes process-tree behavior for every `runCommand` call, not just timed ones]** → Mitigation: detached-on means group-kill is always available; the normal-exit path is untouched (we never call group kill when the child exits on its own). The `CommandResult` on the normal path is byte-identical (D1/D4). The new spec must explicitly assert a non-timed call still resolves unchanged to lock this in.
- **[Action seams cannot reach `runCommand`'s timeout, risking a coverage gap]** → Mitigation: the D6 two-layer split deliberately tests the primitive directly and tests the policy via seams. The contract between them (the D4 result shape) is asserted from both sides.

## Migration Plan

Runner-only change. No server / web / CLI / DB / schema / workflow-YAML changes.

1. **Primitive first** — extend `CommandResult` (optional fields), switch `spawn` to `detached: true`, harden `killProcess`, wire `timeoutMs` through `CommandLineOptions` + `timeoutSignal` inside `runCommand`. Land `tests/system-process-timeout.spec.ts`. At this point every existing caller still opts out (no `timeoutMs`) and behaves identically.
2. **Policy second** — add the `NETWORK_COMMAND_TIMEOUT_MS` constant; thread `timeoutMs` through `GitOptions` / `git()`; update the network call sites in `runtime/workspace.ts` (`clone` `:199`, `ls-remote` `:223`), `actions/push.ts` (`push` `:85`, `ls-remote` probe `:156`), `actions/rebase.ts` (`fetch` `:57`), `actions/create-github-pr.ts` (`push` `:102`, `fetch` `:257`, `gh pr list/edit/create`), `mark-github-pr-ready.ts`, `merge-github-pr.ts`, `github-pr-status.ts`, and the `gh --version` / `gh auth status` precheck in `github-pr-runtime.ts` (`:32`, `:43`). Leave all local-only call sites untouched.
3. **Classify-path verification** — assert the structured timeout output is absorbed by the existing `retry-safe` arm; no rule additions.
4. **Verify gates** — `npm run typecheck -w packages/runner`; `npm test -w packages/runner` (spec < 500ms, no real network/wall-clock).
5. **Deploy** — `mo update runner` (managed restart; avoids runner-id drift per `AGENTS.md`).
6. **Rollback** — revert the runner commit and `mo update runner`. Because the change is runner-local and the normal-path `CommandResult` is byte-identical, rollback needs no server coordination and no data migration. The only externally observable difference post-rollout is "hung network commands fail in 120s instead of 30m"; rolling back simply restores the 30m behavior.

## Open Questions

- **Network default magnitude.** Is 120s the right single value, or should it be higher to absorb slow-link clones of large repos? The spec fixes one shared default; confirm before shipping, tune the single constant if clones regress.
- **SIGTERM vs SIGKILL-after-grace on timeout.** SIGTERM matches `killProcess` and is gentlest; a wedged helper may ignore it. Adopt SIGKILL escalation only if post-ship telemetry shows leftover `git-remote-http` processes.
- **`with.commandTimeout` override.** The proposal reserves one optional action-level override. Not built now (YAGNI); revisit only if the single default proves wrong for a specific workflow.
- **Should work-level abort also emit a structured result?** This design group-kills on parent abort but preserves today's reject behavior there. Surfacing parent-abort as structured too is out of scope; revisit if downstream renderers want to distinguish abort from timeout uniformly.
