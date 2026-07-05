## Why

Runner executes `git`/`gh` external commands with no per-command timeout — every command shares one work-level abort signal, so a hung network call (`git fetch`, `git push`, `gh pr create`, …) is invisible until the 30-minute work timeout fires. The operator cannot tell which command stalled, and orphaned helper processes (`git-remote-http`, `git-remote-https`) leak because `killProcess` only sends `SIGTERM` to the direct child (the spawn is not `detached`, so the process group is never signaled). This matters now because a single hung clone/fetch already burns a whole work slot for half an hour and the mechanism to fix it already exists: `actions/registry.ts:190` ships a `timeoutSignal(parent, timeoutMs)` helper that `core/script` uses today.

## What Changes

- `runCommand` (`system/process.ts:62`) gains an optional `timeoutMs`. On expiry it terminates the **entire subprocess tree** — spawn with `detached: true` so the child leads its own process group, then `process.kill(-pid)` to signal the group — so `git-remote-http` and similar helpers die with the parent, and returns a **structured timeout result** instead of falling through to the work-level abort.
- The timeout result is distinguishable from a normal non-zero exit and carries an exit/status category, so callers can tell "the command hung" from "the command ran and failed".
- **Network-bound call sites run under a single default timeout** (≈120s): `git clone` / `git fetch` / `git ls-remote` in `runtime/workspace.ts`; `git push`, `git fetch` (base), and `gh pr list` / `edit` / `create` in the delivery actions (`push.ts`, `rebase.ts`, `create-github-pr.ts`, `mark-github-pr-ready.ts`, `merge-github-pr.ts`, `github-pr-status.ts`).
- **Local-only git commands are explicitly excluded** — `rev-parse`, `status`, `checkout`, `diff`, `merge-base`, `reset`, `rebase --abort`, `cat-file`, `ls-tree`, `show-ref`, `fsck`, etc. do not hang on the network and keep using only the work-level signal.
- A network-command timeout surfaces a **structured failure** carrying the step name, a command summary, and the timeout duration, and classifies as **`retry-safe`** through the existing path (`github-pr-classify.ts` already matches `timeout` / `timed out`).
- `core/script`'s `with.timeout` and `mohist/acp-agent`'s existing behavior are unchanged — they continue to layer `timeoutSignal` over `context.signal`; the new `runCommand` `timeoutMs` is an additional, orthogonal knob.

## Capabilities

### New Capabilities

- `command-timeout`: The `runCommand` per-command timeout mechanism — optional `timeoutMs`, subprocess-tree termination on expiry (`detached` spawn + process-group kill so helper processes like `git-remote-http` are reaped, not orphaned), and a structured result that distinguishes a timeout from a normal exit. The primitive every caller composes with; it does not by itself decide which commands are timed out.
- `network-command-timeout`: The policy that applies `command-timeout` to network-bound runner commands. Network commands (git `clone`/`fetch`/`ls-remote`/`push`, and the `gh pr`/`gh` API calls in the delivery actions) SHALL run under one default per-command timeout; a timeout SHALL surface as a structured failure carrying step name, command summary, and duration; and a network timeout SHALL classify `retry-safe` via the existing `classifyGhFailure` / `classifyPushFailure` path. Local-only git commands are out of scope.

### Modified Capabilities

None. The work-level (30-minute) abort and `core/script` / `mohist/acp-agent` `with.timeout` semantics are unchanged; the retry-safe classification table already matches timeout text and is only fed a new, structured source.

## Impact

- **Runner** (`packages/runner`):
  - `system/process.ts` — `runCommand` signature gains `timeoutMs`; `spawn` switches to `detached: true`; on timeout the process group is killed and a structured result is returned. `killProcess` is hardened to kill the group when a child is detached.
  - `runtime/workspace.ts` — `cloneFresh`, `verifyBaseBranch` (`ls-remote`), and the base `fetch` path pass the network default.
  - `actions/` — `push.ts` (`push`, `ls-remote` probe), `rebase.ts` (`fetch`), `create-github-pr.ts` (`git fetch`/`push`, `gh pr list`/`edit`/`create`), `mark-github-pr-ready.ts`, `merge-github-pr.ts`, `github-pr-status.ts` pass the network default for their network calls; local git probes (`rev-parse`, `merge-base`, `status`, …) are left untouched.
  - `actions/registry.ts` — `timeoutSignal` is reused (not duplicated) when wiring `runCommand`'s `timeoutMs`; `core/script` and `mohist/acp-agent` paths are behavior-preserving.
  - `actions/github-pr-classify.ts` — no rule changes; verify the structured timeout output is matched by the existing `retry-safe` arm.
- **Server / Web / CLI**: no changes. This is runner-local execution resilience.
- **Dependencies**: none. Uses only `node:child_process` + `process.kill`.
- **Tests**: fake/controlled subprocesses (existing `setXxxGitRunnerForTest` / `gh` runner injection seams) cover a hung network command → subprocess tree killed → structured timeout result → `retry-safe` classification; no real network, no wall-clock (timeout driven by injected/fake timer, not `setTimeout`-as-truth). Local-git-command call sites asserted to remain on the work-level signal only.
