# Self-Review: issue-561

## Artifacts Reviewed

- `proposal.md` - issue motivation, capabilities, impact, and breaking changes
- `design.md` - source snapshot, managed release store, identity contract, activation state machine, migration, and risks
- `tasks.json` - five implementation slices, acceptance criteria, and dependency graph
- `specs/update-source-identity/spec.md`
- `specs/managed-runtime-artifacts/spec.md`
- `specs/update-runtime-consistency/spec.md`
- `specs/update-runtime-recovery/spec.md`

The requested `mo issue show 561 --project proj_f6c141d63b6243bfbb481737b2243b87` command is not supported by this CLI. The supported `mo issue view` command reports issue 561 as a P0, plan-stage, `in_progress` issue titled `mo update：Server 与 Runner 使用指定 repo-root 的同一构建`; its body is empty. This re-review therefore uses the title, the plan's stated objective, and the current update/build/runtime boundaries; the seven findings below remain unresolved.

## Findings

### 1. P1 - Build-time dependency and write isolation is not executable yet

`design.md:104-108`, `specs/update-source-identity/spec.md:23-35`, and `tasks.json:T-001/T-002` name `BuildWorkspaceRoot` and `CandidateRoot`, but do not define the command/property contract that enforces those boundaries. The existing Server project hardcodes the web build working directory and `web/dist` source in `packages/server/src/Mohist.Server/Mohist.Server.csproj:48-63`; `packages/web/vite.config.ts:31-34` hardcodes `dist`; and the root npm workspace in `package.json:10-16` resolves the web build through the root workspace installation. A .NET publish/restore also normally writes project assets and intermediates under `obj` unless all relevant MSBuild paths and package caches are redirected. The plan only gives an explicit production dependency strategy for the Runner, not for the web/.NET build toolchain or workspace dependencies.

As written, an implementation can still write `obj`, web output, package-manager metadata, or build caches into `SnapshotRoot`, or silently depend on the source/workspace `node_modules`, while claiming to build from the immutable snapshot.

**Required revision:** Define the exact Server, web, and Runner build commands, working directories, output flags, MSBuild intermediate/restore/package-cache properties, npm workspace/dependency-cache inputs, and generated-file locations. Add tests that run the builders with an unavailable source worktree and unavailable workspace `node_modules`, then assert every write is under the declared build/candidate roots.

### 2. P1 - The managed Server artifact has no complete launch contract

`design.md:66-92,104,112` describes `server/...` and a `RuntimeTargetSet` but does not specify whether the Server release is framework-dependent or self-contained, which runtime identifier is published, which file is the executable entrypoint, or which command and arguments the stable launcher executes. `specs/managed-runtime-artifacts/spec.md:34-46` only requires absolute paths. The existing Linux installer still launches `dotnet run --project ...csproj` from a source-bound working directory (`packages/cli/Mohist.Cli/SystemdServiceInstaller.cs:39-56`), and the Windows installer likewise renders a source-root launcher (`packages/cli/Mohist.Cli/WindowsScheduledTaskInstaller.cs:62-74`).

Without a precise Server artifact/launcher shape, the implementation can publish a DLL but leave the launcher dependent on an installed SDK, a source project, relative content, or a different runtime. That would preserve the issue's build-one-version/run-another failure despite the new release directory.

**Required revision:** Specify the Server publish mode, runtime identifier/host requirement, entrypoint and arguments, configuration/data-root handoff, web-content location, and stable-launcher rendering for Linux and Windows. Add source-removal startup tests that exercise the actual recorded Server target command.

### 3. P1 - The new `rejected` web-job state conflicts with the existing state machine and startup recovery

`design.md:190-192`, `specs/update-runtime-recovery/spec.md:97-113`, and `tasks.json:T-005` require persisted `running` or `waiting-for-reconnect` web jobs to become `rejected` at Server startup. The current persisted model only defines active statuses `running`/`waiting-for-reconnect` and terminal statuses `succeeded`/`failed`/`recovered`/`superseded`/`cancelled` (`packages/server/src/Mohist.Server/SystemInfo/SystemUpdateJobState.cs:21-22`); `SystemUpdateService.NormalizeOutcomeStatus` also rejects any other status (`SystemUpdateService.cs:421-434`). More importantly, the already-registered `SystemUpdateRecoveryService` (`MohistServiceRegistration.cs:222-227`) marks stale active jobs `failed` with `interrupted by process restart` before the new projection rule can report `UpdateMutationOwnedByCli` (`SystemUpdateRecoveryService.cs:62-96`).

The plan therefore does not determine the production result for an existing web job, and tests of a new quarantine helper could pass while the registered startup service still produces the old `failed` state.

**Required revision:** Define `rejected` in the persisted status contract and terminal transitions, replace or retarget the existing startup recovery service, specify registration/order and retry behavior, and test the real hosted-service startup path including lock release and a second restart.

### 4. P1 - CLI outcome projection is not bound to a CLI-owned transaction

The design says `/api/system/update/outcome` is only a projection sink (`design.md:54,190-192`), and the recovery spec says status is projected only by matching job ID and target identity (`specs/update-runtime-recovery/spec.md:115-118`). The current endpoint accepts an operator-authenticated `SystemUpdateOutcomeRequest` with optional `JobId`, source, target, and outcome facts (`packages/server/src/Mohist.Server/SystemInfo/SystemInfoDtos.cs:52-62`). `RecordCliOutcomeAsync` creates a new persisted job when the supplied ID does not match the latest job (`SystemUpdateService.cs:219-253`) and accepts a successful status without proving that a local transaction with that ID exists.

An old CLI, stale retry, or any caller with the operator API credential can consequently create or overwrite the latest projection as a successful update with arbitrary identity facts, even though no CLI-owned candidate activation was verified. That contradicts the plan's single-authority and fail-closed guarantees.

**Required revision:** Make the outcome contract require an existing transaction ID and an ownership proof or transaction nonce issued by the local CLI; reject unknown, mismatched, stale, or out-of-order outcomes; never create a successful projection from an arbitrary POST. Add spoofed, duplicate, stale, and mismatched-target tests.

### 5. P1 - `RecoveryFailed` still does not define the executable CLI terminal state

`design.md:146,148-150` and `specs/update-runtime-recovery/spec.md:52-58` define failed recovery as stopped candidate services plus `active.json` with `status: "none"`, but they do not say which CLI slot remains executable or how the ordinary `mo` entrypoint behaves. The design separately promises a recovery slot for CLI self-update (`design.md:52,60-62`), yet the failed-restoration state does not require that slot to be selected, retained, or exposed as the only recovery path. If the candidate CLI slot replaced the previous executable before Server/Runner verification failed, `active.json` being empty does not prevent a direct CLI path from launching that unverified candidate.

This leaves the system with no explicit invariant that the next recovery command runs trusted code rather than the candidate that caused the failure.

**Required revision:** Specify the CLI-slot state for `RolledBack`, `NoVerifiedRuntime`, and `RecoveryFailed`, including whether the stable wrapper points to the recovery slot, whether both slots are disabled, and the exact manual bootstrap/reinstall command. Add kill-point tests asserting the selected executable and wrapper behavior after failed restoration.

### 6. P1 - Cancellation has no transaction outcome or recovery contract

`tasks.json:T-004` promises cancellation coverage, but `specs/update-runtime-recovery/spec.md` has no cancellation requirement or scenario, and the state machine in `design.md:134-150` has no `Cancelled` state or rule for cancellation at `CandidateStaged`, after `active.json` publication, during service restart, or during CLI-slot replacement. Existing orchestration treats cancellation as an interruption of stage execution (`packages/cli/Mohist.Cli/MohistCliCommands.Update.cs:243-250`), which is not enough to determine whether a candidate must be cleaned, rolled back, or reconciled on the next startup.

An implementation can therefore return cancellation while leaving a nonterminal record, a published candidate target, or a stopped Runner without a specified terminal result, violating the no-half-update objective.

**Required revision:** Define cancellation as a state-machine event for every destructive boundary, including write-ahead persistence, rollback/cleanup ordering, exit code, lock release, and next-start reconciliation. Add deterministic cancellation tests before activation, after target publication, during service effects, and after CLI replacement.

### 7. P1 - Durable transaction and lock guarantees are underspecified across crashes

The plan requires durable `CandidateStaged`, atomic `active.json` replacement, and an exclusive lock (`design.md:52-54,114,148-150,208`; `specs/update-runtime-recovery/spec.md:16-19`), but it does not define the durability boundary or lock ownership protocol. It does not say when file contents and directory entries are flushed, how atomic replacement behaves on Windows, how a lock records its owner and transaction ID, or how a process crash between state-file replacement and lock release is recovered. The existing store uses separate state and lock files and a temp-file move (`packages/server/src/Mohist.Server/SystemInfo/FileSystemSystemUpdateStore.cs:60-77,116-127`), so “atomic active record” and “release the lock” are not one operation.

Without these rules, a kill point can leave a durable candidate with an apparently free lock, or a durable terminal state with a stale lock that blocks all future updates; both outcomes undermine the required crash reconciliation.

**Required revision:** Specify file/ directory flush and replacement semantics for both platforms, lock owner/transaction identity and stale-lock recovery, and the ordering for state, active-record, service-unit, CLI-slot, and lock writes. Add process-crash tests for each boundary and verify that a new update cannot start until reconciliation has completed.

## Issue Coverage Check

| Issue objective | Planned coverage | Review status |
|---|---|---|
| Build Server and Runner from the selected root | Immutable snapshot and shared source identity | Blocked by incomplete build-time dependency/write isolation and Server launch contract (findings 1-2) |
| Prove the running artifacts match the target | Artifact-owned identities and exact Runner connection | The identity model is directionally covered, but projection trust and executable rollback state remain unresolved (findings 4-5) |
| Success only after verified consistency | Scope matrix and activation state machine | Blocked by web-state authority and missing cancellation semantics (findings 3 and 6) |
| Failure does not leave a half-update | Transaction records and reconciliation | Blocked by CLI terminal state and durability/lock gaps (findings 5 and 7) |
| Existing installations can adopt the managed runtime | Bootstrap and platform migration | Bootstrap is specified, but the runtime launcher and recovery executable contract is still incomplete (findings 2 and 5) |

## Verification Limits

- No product tests or full gates were run; this was a read-only plan review and implementation cross-check.
- The issue body returned no acceptance-criteria list; the review used the issue title, P0 plan-stage status, and the plan's stated objective.
- No files other than this review artifact were changed.

## Verdict

The plan is not ready to build. Findings 1-7 are P1 blockers because they leave the build boundary, Server execution target, web projection authority, rollback executable, cancellation behavior, or crash durability open at the exact points where issue 561 requires a trustworthy same-source update.

<promise>FAIL</promise>
