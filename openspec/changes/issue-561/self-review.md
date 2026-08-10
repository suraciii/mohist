# Self-Review: issue-561

## Artifacts Reviewed

- `proposal.md` - issue motivation, capabilities, impact, and breaking changes
- `design.md` - source snapshot, managed release store, identity contract, activation state machine, migration, and open questions
- `tasks.json` - five implementation slices, acceptance criteria, and dependency graph
- `specs/update-source-identity/spec.md`
- `specs/managed-runtime-artifacts/spec.md`
- `specs/update-runtime-consistency/spec.md`
- `specs/update-runtime-recovery/spec.md`

The issue was read through the supported `mo issue view 561 --project proj_f6c141d63b6243bfbb481737b2243b87` command because this CLI does not support `mo issue show`. The record identifies issue 561 as a P0 plan-stage update-integrity bug, status `in_progress`; its body is empty. The review was cross-checked against the current CLI update pipeline, Server web-update service, service installers, runtime identity surfaces, and Runner build/runtime packaging.

## Findings

### 1. P1 - The immutable snapshot has no writable build/output boundary

`design.md:42,84` and `specs/update-source-identity/spec.md:23-34` require a read-only `SnapshotRoot` and say every build uses it. The existing Runner build writes `dist` through `packages/runner/tsconfig.json:8-10` and writes `dist/build-info.json` in `packages/runner/scripts/write-build-info.ts:8-31`. The Server publish path also runs the web build and copies `packages/web/dist` from the source tree in `packages/server/src/Mohist.Server/Mohist.Server.csproj:48-63`. A literal read-only snapshot therefore makes the existing build commands fail; making it writable reintroduces mutation of the identity input. The plan does not define writable intermediate/output roots, web asset staging, or how generated Runner/CLI metadata is injected without changing the snapshot.

**Required revision:** Specify separate writable build intermediates and candidate output paths for Server, Runner, web assets, and generated identity files while keeping the source snapshot immutable. Define the exact command/environment contract and add a deterministic test that observes build writes without modifying the snapshot.

### 2. P1 - The managed Runner release is not dependency-complete

The release layout in `design.md:58-76` lists `runner/...` but does not define its Node runtime dependencies. `packages/runner/package.json:25-29` imports packages such as `@microsoft/signalr`, `@opencode-ai/sdk`, and `undici`, while the current build only emits TypeScript output through `package.json:13-16`. A service started from the managed release after the source worktree is removed may still resolve modules from the source/workspace `node_modules`, or fail with module-not-found errors. This contradicts `specs/managed-runtime-artifacts/spec.md:30-33`, which requires a previously installed release to start without its build worktree.

**Required revision:** Choose and specify a production dependency strategy for the Runner release, such as bundling, a copied production `node_modules` tree, or an explicitly managed shared dependency store. Record it in `release.json` and test startup after the source worktree and workspace dependencies are unavailable.

### 3. P1 - Runtime identity is still not bound to the executing artifact or Runner connection

`design.md:86,100-104` allows Server identity to be populated from service environment values before source fallback. The current `RuntimeBuildInfo` already accepts an environment hash and then the current source HEAD (`packages/server/src/Mohist.Server/SystemInfo/RuntimeBuildInfo.cs:56-76`), so an old Server binary launched with a candidate environment can report the candidate identity without containing the candidate artifact. The plan does not require artifact-owned metadata to be authoritative or require launcher environment values to be cross-checked rather than trusted.

The Runner path has a separate trust gap. `RunnerIdentityRoutes.cs:14-24` selects the first registered Runner by hostname, while `RunnerHub.cs:21-31` stores a self-reported `buildGitHash` from connection query data. A stale or different Runner connection on the same host can therefore satisfy an identity request. The planned manifest fields do not define an exact Runner ID, connection generation, or authenticated handshake binding.

**Required revision:** Make Server identity come from metadata embedded in the published artifact, with environment values used only as consistency checks and no source-tree fallback for managed releases. Bind Runner identity readback to the exact active Runner ID and connection generation, and require the Runner to report its artifact-owned manifest rather than accepting an arbitrary hostname/self-reported hash. Add stale-artifact and wrong-connection tests.

### 4. P1 - Rejecting the POST endpoint does not make the Server update service projection-only

The new recovery contract only rejects `POST /api/system/update` in `specs/update-runtime-recovery/spec.md:81-92`. The existing `GET /api/system/update/status` route in `SystemRoutes.cs:31-34` calls `GetStatusEnvelopeAsync`, which calls `AdvanceActiveJobAsync` in `SystemUpdateService.cs:204-208`. That method can restart the Runner at `SystemUpdateService.cs:183-201`, and a previously accepted web job still runs the source-tree `dotnet build` and Server restart in `SystemUpdateService.cs:449-481`. The web service therefore remains an imperative mutator for persisted/running jobs even if new POST requests return 409. The plan has no stale-job startup rule, no migration of existing `running` jobs, and no test that status access performs zero build/restart commands.

**Required revision:** Make the entire Server update service read/report-only, including status polling and background-job resumption. Define how existing nonterminal web jobs are marked rejected or projected into CLI-owned transactions, and add tests for POST, status reads, and stale-job startup proving that no build, service restart, or active-target mutation occurs.

### 5. P1 - `RecoveryFailed` has no required physical runtime end state

`design.md:118-126` and `specs/update-runtime-recovery/spec.md:43-56` describe reporting a failed restoration, but do not state what happens to an active candidate when the previous release cannot start or cannot be verified. The candidate may remain selected by `active.json`, or a partially changed service/CLI slot may remain in place, while the plan still promises that no unverified candidate is left as the managed runtime (`specs/update-runtime-recovery/spec.md:58-70`). Reporting `RecoveryFailed` alone does not restore the issue's no-half-update invariant.

**Required revision:** Define the physical terminal state for rollback failure: stop or disable the candidate, clear or quarantine the active-target record, preserve the transaction and forensic paths, and report `NoVerifiedRuntime` or `RecoveryFailed` with the exact restart/reinstall action. Add tests that assert service state, CLI slot, and `active.json` after rollback failure.

### 6. P1 - Initial managed self-update and crash recovery have no trusted bootstrap executable

The design requires a transaction record before CLI replacement and says a stable launcher invokes the CLI-owned reconciler (`design.md:48-52,124-126`; `specs/update-runtime-recovery/spec.md:16-41`). The migration section simultaneously targets existing source-bound installations (`design.md:180-187`). An existing installed CLI predating this transaction schema cannot create the required record or run the new reconciler before it replaces itself. The current flow confirms the gap: `SourceCodeUpdater.UpdateAllAsync` enters the CLI update stage and only then continues with the refreshed process (`packages/cli/Mohist.Cli/MohistCliCommands.Update.cs:236-263`), while `UpdateOperations.UpdateCliResolvedAsync` replaces the target executable directly (`packages/cli/Mohist.Cli/Update/UpdateOperations.cs:183-250`). If replacement or continuation crashes, the plan does not identify an older trusted binary, a recovery helper, or a durable handoff that can reconcile the candidate.

**Required revision:** Define a first-run bootstrap protocol for legacy installations and a recovery executable/slot that remains runnable when the candidate CLI is unverified. Specify the handoff and migration state transitions, including crashes before and after CLI replacement, and add tests from a pre-transaction installation.

### 7. P2 - Required platform/path choices remain open questions

`design.md:58-74` relies on a managed runtime root and stable launchers, while `design.md:193` leaves the root location/configuration undecided and `design.md:196` leaves Windows rollout undecided. `tasks.json:T-002` already requires both systemd and Windows launcher behavior and its acceptance criteria depend on deterministic paths. The transaction store, launcher bootstrap, permissions, and migration behavior cannot be implemented or tested consistently while these are still open questions.

**Required revision:** Choose the cross-platform runtime-root resolution, permissions, and launcher installation contract before implementation, or explicitly scope the first implementation to one backend and remove the other backend from the current acceptance criteria and migration claims.

### 8. P2 - Component version and release ID generation is not canonical

`design.md:74,76,96,102` requires exact equality for `version` and `releaseId`, but only says the release ID is derived from source revision and component build identity. It does not define the algorithm, normalization, or whether the value is transaction-generated, and it does not define how component versions are selected. The current inputs are independently static (`packages/cli/Mohist.Cli/Mohist.Cli.csproj:11-13` has version `1.0.0`, while `packages/runner/package.json:2-3` has version `0.1.0`). Without a canonical generation/injection contract, repeated builds, same-source builds, and component-scoped releases can produce identities that are not reproducibly comparable.

**Required revision:** Define the release ID algorithm and component-version sources, including normalization and generated .NET informational-version/Runner manifest fields. Add tests for repeated builds, same version with different source revisions, and component-scoped active sets.

## Issue Coverage Check

| Issue objective | Planned coverage | Review status |
|---|---|---|
| Build Server and Runner from the selected root | Source identity and snapshot requirements | Blocked by the missing writable build/dependency closure (findings 1 and 2) |
| Prove the running artifacts match the target | Structured identity fields and strict checks | Blocked by artifact/connection trust and noncanonical identity generation (findings 3 and 8) |
| Success only after verified consistency | Scope matrix and activation state machine | Blocked by the remaining Server mutator and incomplete recovery end state (findings 4 and 5) |
| Failure does not leave a half-update | Durable transaction and reconciliation | Blocked by rollback physical state and bootstrap ownership (findings 5 and 6) |
| Existing installations can adopt the managed runtime | Migration plan | Blocked by bootstrap and unresolved platform/root choices (findings 6 and 7) |

## Verification Limits

- No product tests or full gates were run; this was a read-only plan review and implementation cross-check.
- The issue record returned no body text or acceptance-criteria list; the review used its P0 plan-stage status, title, the plan's stated objective, and the current implementation boundaries.
- No files other than this review artifact were changed.

## Verdict

The plan is not ready to build. Findings 1-6 are P1 blockers because they can make the immutable build fail, allow a stale artifact to self-identify as current, preserve an alternate mutator, leave an unverified runtime after rollback failure, or strand the system during initial CLI replacement. Findings 7-8 are P2 contract gaps that must be resolved before the implementation tasks can be deterministic.

<promise>FAIL</promise>
