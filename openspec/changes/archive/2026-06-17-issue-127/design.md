## Context

Issue #121 failed because the local runner service executed stale ignored `packages/runner/dist` output while Mohist reported the runner as healthy. Two systemic gaps were exposed and are collected in issue #127.

**Current state (verified in code):**

- The runner service runs `node packages/runner/dist/cli.js` (`SystemdServiceInstaller.cs:59`). `dist/` is gitignored (`.gitignore:8`) and rebuilt by `tsc`.
- `SourceCodeUpdater.UpdateRunnerAsync` (`MohistCliCommands.Update.cs:329`) **always** rebuilds (`npm run build -w packages/runner`) and restarts (`systemctl --user restart mohist-runner.service`) when called. It performs **no** post-restart verification and has **no** skip detection for an uninstalled/unmanageable runner.
- `mo update server` (`UpdateServerAsync:283`) updates only the server and prints nothing about the runner being out of scope.
- The **server** has a build-identity mechanism (`RuntimeBuildInfo` → git hash, compared in `SystemUpdateService.cs:429`). The **runner has none**: its ACP `clientInfo` is a hardcoded `0.1.0` label (`acp-connection.ts:98`), and runner availability on the server is purely connection-based (`AgentRoutes.cs:116`).
- The default Integrate workflow (`mohist-default.workflow.yaml`, lines 236–280) declares exactly three tasks: `integrate:spec-sync`, `integrate:archive-change`, `integrate:merge`. There is **no `integrate:push` task and no `push: true` input on `mohist/merge` today**. The `integrate:push.1` observed during #121 was from an earlier workflow revision; the current YAML is already clean. `mergeAction` (`registry.ts:132`) reads no `push` input and issues no `git push`.

This means issue #127's workflow half is about **establishing a regression guardrail** for the single-push-owner invariant, not deleting a live duplicate. The runner half is the substantive new work: giving the runner a build identity and teaching `mo update` to verify the live runtime matches the current source.

## Goals / Non-Goals

**Goals:**
- After full `mo update`, the running runner code provably matches the current source / rebuilt dist — not merely "service is active".
- Skipped runner refresh (uninstalled / unmanageable) is reported with a reason in both command output and verification results.
- `mo update server` output makes explicit that runner runtime was not refreshed and gives follow-up guidance.
- The default Integrate workflow cannot regain a duplicate push owner: a regression test asserts no `*:push` task coexists with merge-owned push.
- Regression coverage for both halves.

**Non-Goals (from issue #127):**
- Redesigning #112's merge/rebase semantics; adding `push: true` support to `mohist/merge` itself is **not** required by this issue (it is #112's territory). #127 only guarantees no duplicate push *if/when* merge owns push.
- Committing `packages/runner/dist` to git.
- Restructuring the systemd install model.
- Changing server-side (`SystemUpdateService`) HTTP update behavior.

## Decisions

### Decision 1: Runner build identity via a postbuild manifest

Embed a build manifest into `packages/runner/dist` at build time so the runner and the CLI can compare identities cheaply.

- Add a `postbuild` step to `packages/runner/package.json` that writes `dist/build-info.json`: `{ "gitHash": "<rev-parse HEAD>", "builtAt": <epoch ms> }`.
- The runner loads `build-info.json` at startup (relative to `cli.js`, so it works regardless of cwd) and exposes the `gitHash` as `RUNNER_BUILD_GIT_HASH`.

**Alternatives considered:**
- *Hash the dist output*: rejected — source→dist isn't 1:1 (tsc normalization), and it doesn't tie back to the repo HEAD the server already compares against.
- *Generate a TS module into `src/`*: rejected — pollutes `src/` with a generated file and complicates the tsconfig `rootDir`.

**Rationale:** mirrors the server's existing `RuntimeBuildInfo` git-hash approach, so verification logic is symmetric and the comparison target (`git rev-parse HEAD`) is identical.

### Decision 2: Runner reports build identity on connect; CLI verifies live identity

- Extend the runner's SignalR handshake (`runner-signalr.ts`, currently sends only `runnerId`) to also send `buildGitHash`.
- The server stores the reported hash alongside the runner connection state (extend the connection/availability record in `AgentRoutes` / runner registration).
- `UpdateRunnerAsync` gains a verification tail (analogous to `WaitForServerReadyAsync`):
  1. Wait briefly for the runner to reconnect after restart.
  2. Read the live runner's reported `buildGitHash` via the server API.
  3. Compare to `git rev-parse HEAD` of the repo.
  4. Report `current` on match, `stale-runner-runtime` on mismatch (even if service is active), or `runner-refresh-skipped(<reason>)` when refresh was intentionally skipped.

**Alternatives considered:**
- *Verify only the `dist/build-info.json` file against repo HEAD (no live round-trip)*: simpler, but cannot detect a service that restarted against the wrong working directory or a stale process that didn't actually pick up the new dist. Kept as a fast pre-check (Layer 1); the live identity check (Layer 2) is what satisfies the "live runner code identity" requirement.

**Rationale:** the issue explicitly calls out that a service can be "active" while running stale code; only a live-identity comparison closes that gap.

### Decision 3: Skip detection for unmanageable runner

`UpdateRunnerAsync` currently restarts unconditionally. Introduce an explicit manageability probe before build/restart:

- Detect whether the runner service is installed/manageable (e.g., `systemctl --user is-enabled mohist-runner.service` via the existing `IServiceInstaller`, or a new `IsRunnerInstalledAsync` probe).
- When **not** manageable: skip build+restart, print `"Runner refresh skipped: <reason>"`, and have verification report `runner-refresh-skipped(<reason>)` so it is distinguishable from a stale-runtime mismatch.
- This also makes the existing dry-run line `"... (if installed)"` (`UpdateRunnerAsync:339`) truthful for the real path.

**Alternatives considered:**
- *Keep unconditional restart and only add verification*: rejected — fails noisily on dev machines without the service installed, contradicting the spec's skip scenario.

### Decision 4: Explicit `mo update server` runner-scope messaging

In `UpdateServerAsync`, after success, emit a line stating the runner was not refreshed and a follow-up tip (`mo update runner` / full `mo update`) when the runner is installed. This is a messaging-only change; no behavioral change to what `UpdateServerAsync` updates.

### Decision 5: Single-push-owner guardrail via profile test + loader note

Since no `integrate:push` exists today and `push: true` is out of scope for #127, enforce the invariant defensively:

- Add a regression test in `MohistDefaultWorkflowProfileSpecs.cs` asserting the default Integrate stage declares **no** task whose id matches `*:push`, and that `integrate:merge` is the sole delivery task.
- Document the invariant in the workflow-definition spec delta (already done in the specs artifact): if/when `mohist/merge` gains `push: true`, the merge task owns push and no separate push task may be (re)introduced.

**Alternatives considered:**
- *Add a config-validation rule in the workflow loader rejecting conflicting push tasks*: more general but over-engineered for a single default profile and risks rejecting legitimate custom profiles. Deferred; the focused test satisfies the acceptance criterion ("通过测试/配置约束保证不会重复 push").

## Risks / Trade-offs

- `[Runner reconnect timing during verification]` — after `systemctl restart` the runner reconnects asynchronously; verification could time out before reconnect. -> Mitigation: bounded poll loop with a clear `runner-not-reconnected` result distinct from `stale-runner-runtime`; fall back to Layer 1 dist-manifest check so verification still produces a useful answer.
- `[build-info.json missing in packaged/older dist]` — existing deployed runners have no manifest. -> Mitigation: treat absent manifest as `unknown-identity` (warn, don't fail) so the first update after this change is not a hard failure; subsequent updates produce full identity.
- [`git rev-parse HEAD` runs in a detached/shallow checkout]` -> Mitigation: resolve HEAD once at update start; if unavailable, report `identity-unavailable` rather than degrading silently.
- `[Test guardrail is bypassable by editing YAML without running the profile test]` -> Mitigation: the test is part of the default profile spec suite which runs in CI for any workflow change; accept residual risk rather than over-building loader validation (non-goal).
- `[postbuild step assumes git CLI present in build environment]` -> Mitigation: if `git rev-parse` fails, write `gitHash: null` and let verification report `identity-unavailable`; build still succeeds.

## Migration Plan

1. Ship the runner `postbuild` manifest + runner identity reporting. Existing runners without a manifest are reported as `unknown-identity` (non-blocking) until they receive their first rebuilt+restarted update.
2. Ship CLI verification + skip detection + `mo update server` messaging. No schema migration needed; identity is carried over SignalR query params / an existing-status endpoint.
3. Ship the workflow profile regression test. No runtime behavior change for the default workflow (it is already clean).
4. **Rollback:** all changes are additive (manifest, verification tail, messaging, test). Reverting the CLI commit restores prior always-build-and-restart behavior; the runner manifest and test are independently removable. No data migration to undo.

## Open Questions

- **Exact transport for live identity read-back by the CLI.** The CLI verifies by querying the server for the connected runner's reported hash. Confirm whether to reuse the existing runner-status endpoint in `AgentRoutes` or add a dedicated `/api/runner/identity` — decide during Build based on what the server already exposes for runner connection state.
- **Should #127 add `push: true` to the default `integrate:merge`?** Exploration shows it is not implemented and the issue non-goals defer merge-semantics to #112. Default answer: **no** — #127 only adds the guardrail. Confirm with #112's status before Build; if #112 is stalled, consider adding `push: true` here as a minimal follow-on (still guarded by the single-owner test).
