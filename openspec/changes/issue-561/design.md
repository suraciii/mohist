## Context

Issue 561 is a P0 update-integrity bug. `mo update --repo-root <path>` currently resolves the root in several stages, builds the Server with `dotnet build`, builds the Runner into the source worktree, and restarts services whose launch commands still point at that worktree. `SystemdServiceInstaller` renders `dotnet run --project <repo>` and `node packages/runner/dist/cli.js` with the repository as `WorkingDirectory`, so a successful build can still leave a service running a different worktree or an older output directory.

The update pipeline in `SourceCodeUpdater` already has explicit stages, fake process/filesystem boundaries, readiness probes, and a `TimeProvider`. `UpdateContext` currently carries a raw repository-root string and lazily populated `SourceHead`; `RuntimeConsistencyValidator` and `RunnerRefreshVerifier` independently compare runtime hashes with the current source HEAD. Missing or mismatched identities are currently represented as warnings in some paths, and recovery restores a stopped Runner but does not restore a versioned service target. The revised pipeline materializes a read-only snapshot of the selected revision before any build so the source path cannot change the candidate after identity resolution.

The proposal and specs require one immutable source identity, versioned managed Server and Runner artifacts, strict CLI/Server/Runner identity verification, and a recoverable activation transaction. The change affects the CLI update orchestration, both service-installer backends, Server and Runner identity readback, and deterministic tests. It must not change Agent model selection, provider fallback, inference configuration, or Runner workflow behavior.

## Goals / Non-Goals

**Goals:**

- Resolve an absolute, explicit-or-default source context once and use it for every stage, including CLI continuation.
- Materialize a read-only snapshot of the selected revision and build a release from that snapshot into stable managed storage instead of running directly from a mutable worktree.
- Make the release identity available from the installed Server, Runner, and CLI without consulting the source tree at verification time.
- Coordinate candidate activation, runtime readback, commit, rollback, crash reconciliation, and no-verified-release cleanup as one bounded state machine.
- Fail closed for missing or mismatched required identities and report target, observed, failed-stage, and recovery facts.
- Preserve existing fake boundaries and inject filesystem, process, service, HTTP, and time behavior for fast tests.

**Non-Goals:**

- Changing Agent model selection, provider fallback, inference configuration, or Runner work-management behavior.
- Redesigning the Runner management UX or adding a new remote deployment service.
- Updating the Slack adapter as part of the Server/Runner release transaction.
- Pulling source, watching a worktree, supporting dirty worktree content, or inventing a content hash for uncommitted changes in this first implementation.
- Preserving custom service units that depend on source-bound working directories and relative entrypoints. The proposal explicitly makes that deployment contract breaking.
- Making the server-side web update job the authority for local artifact activation. It may report the CLI outcome and expose consistency facts, but the local CLI owns the transaction.

## Decisions

### A. Resolve one immutable source context before any side effect

Add an `UpdateSourceContext` owned by the CLI update boundary. It contains:

- the normalized absolute repository root;
- whether the root came from `--repo-root` or the default resolver;
- the full source revision returned by `git rev-parse HEAD` from that root;
- the immutable snapshot root materialized from that exact revision;
- the source snapshot marker/digest used to validate the snapshot without relying on a `.git` directory;
- the transaction-owned writable `BuildWorkspaceRoot` and candidate release root;
- the build-time dependency projection and toolchain identities used by every build command;
- the clean/usable validation result needed to build an identity; and
- the requested update scope and CLI path needed by the transaction.

The resolver runs once before CLI publication, service changes, or artifact builds. It rejects a missing root, a root without `Mohist.sln`, an unavailable Git revision, and a dirty worktree, then materializes the resolved revision into a staging snapshot and makes the source files read-only to builders. The snapshot carries a sidecar source marker and digest because a staged source tree is not required to contain `.git`. `BuildWorkspaceRoot` is the writable transaction directory and is the parent of the read-only `SnapshotRoot`; it contains only `build/`, build-time dependency/tool caches, and the transaction's `candidate/` directory. The build dependency projection is materialized from a lockfile-matching preinstalled cache with no network access; the supported .NET SDK, Node/npm versions, and toolchain paths are recorded in the transaction, and missing or mismatched tools fail preflight. Compiler intermediates, web output, generated identity files, dependency staging, and release files are written only below `BuildWorkspaceRoot` or `CandidateRoot`. The clean-worktree rule is deliberate: a Git HEAD alone cannot identify uncommitted content, and the issue acceptance criteria explicitly use a clean source directory. Snapshot creation, snapshot validation, toolchain/dependency preparation, and writable-root preparation are part of preflight; a failure stops before managed runtime state changes. Every operation receives the context rather than calling `ResolveRepoRoot` or `git rev-parse` again.

The existing mutable `UpdateContext.SourceHead` becomes a value from this context. Human output and dry-run output use the same context to label explicit/default source mode, authority root, snapshot root, build workspace, candidate root, and target revision. Build commands read source files only from `SnapshotRoot`; the requested repository root is retained for reporting and source-authority diagnostics.

**Alternative considered:** Let each stage resolve the root and read HEAD when it needs it. Rejected because the process working directory, source branch, or source contents can differ between stages, recreating the exact build-one-version/run-another failure.

### B. Persist the source context across CLI self-update continuation

Create a transaction record before candidate staging. The record stores the source context including `SnapshotRoot`, `BuildWorkspaceRoot`, `CandidateRoot`, update scope, stable update ID, continuation generation and claim, candidate/previous active-target sets, CLI slots, recovery CLI path, service targets, required identities, desired service states, the projection lease, and state. For a full update, the pre-update CLI builds and validates the complete CLI, Server, and Runner candidate before it changes any CLI slot, stops a service, or publishes `active.json`; `CandidateStaged` is not durable until all three artifacts, the web assets, the Runner dependency closure, and identity metadata are complete. A component update applies the same rule to every artifact in its declared scope. The old early `UpdateCliStage` replacement is therefore replaced by candidate staging. Only after `CandidateStaged` is durable may the process invoke the refreshed CLI with the exact hidden command `update --continue-after-cli-update --update-id <id> --cli-path <candidate-cli-path>` to perform activation and verification; it does not pass a repository root.

The record is written through an `IUpdateTransactionStore` using the durable atomic-file protocol in Decision F. It is local state under the managed runtime root, not server database state. The refreshed CLI must call `OpenContinuationAsync(updateId, currentCliPath)` before constructing `UpdateContext`; the store atomically verifies that the ID is nonterminal, the current path is the recorded candidate CLI slot, the continuation generation is the next expected generation, and the transaction lock is held. It then claims the continuation with the new process ID and process-start token while preserving the source context, projection nonce, sequence, and recovery CLI path. A continuation never creates a new job ID, lease, source context, or lock. A claim failure is terminal for that process and leaves reconciliation to the recorded recovery CLI slot.

The CLI-owned reconciler reads every nonterminal record before a new update and from the stable managed launcher before it starts a target associated with that record. A crash while `Prepared` is still staging artifacts cleans the incomplete candidate and terminalizes the transaction as `RolledBack` when a previous verified target exists or `NoVerifiedRuntime` otherwise; it never treats a partially built CLI as a recovery authority. The launcher invokes the recorded recovery CLI slot, never the unverified candidate slot, except for the narrowly defined bootstrap dispatch in Decision F. The Server outcome endpoint remains a reporting sink; failure to post the outcome cannot change the local activation result.

**Alternative considered:** Pass the repository root, source hash, previous target, and recovery flags as hidden continuation arguments. Rejected because duplicated arguments can drift, are fragile to quoting, and do not provide durable recovery when the continuation process exits unexpectedly.

### B1. Bootstrap legacy installations before managed self-update

An installation whose active CLI does not support the transaction record, recovery slot, and immutable build context SHALL fail preflight with `bootstrap_required` before replacing the CLI, stopping a service, or changing `active.json`. The initial CLI distribution installs `cli/bootstrap/mo` plus a trusted current-CLI payload and `bootstrap.json` containing its path and integrity identity outside both managed CLI slots. The helper is the only stable-wrapper exception when no trusted slot is runnable: `mo install cli` copies that payload into `cli/recovery/mo`, atomically recreates the stable launcher and runtime-root metadata, and never reads a candidate slot. Linux preserves the executable mode through same-directory replacement; Windows uses the payload's `.exe` and the stable `.cmd` wrapper with the same atomic boundary. Missing or corrupt payload metadata reports installation corruption and directs the operator to reinstall the original CLI distribution. The bootstrap operation does not claim that the legacy source-bound runtime is verified. A managed update starts only on the next invocation of the new CLI, which can persist `Prepared` before any self-update.

After bootstrap, the current CLI remains runnable in a recorded recovery slot while the candidate CLI is staged. A crash before or after candidate slot replacement therefore invokes the recorded recovery slot and reconciles the transaction without executing the candidate as its own recovery authority. This is an explicit breaking migration boundary, not an attempt to make an older CLI understand the new transaction schema.

### C. Use a versioned managed release store

Add a `ManagedRuntimeStore` behind the existing `IFileSystem` boundary. The per-user store has this logical layout:

The product runtime root is fixed for the first implementation: `$HOME/.local/share/mohist/runtime` on Linux and `%LOCALAPPDATA%/Mohist/runtime` on Windows. `RuntimeRootResolver` expands and validates the platform path once, records the absolute value in the transaction, and passes it to the CLI, service installers, stable launchers, and reconciler. Tests inject an equivalent absolute root; there is no product-relative or unresolved runtime-root option in this change.

```text
<managed-runtime-root>/
  releases/<release-id>/
    release.json
    cli/mo
    server/
      Mohist.Server
      web-assets/...
    runner/
      dist/...
      package.json
      node_modules/...
      dependency-manifest.json
  active.json
  transactions/<job-id>/
    record.json
    projection-lease.json
    projection-intent.json
    snapshot/
      source-marker.json
      ...read-only source files...
    build/
      dotnet/obj/...
      web/dist/...
      cli-generated/...
      node-workspace/...
      node-modules/...
      npm-cache/...
      nuget/...
    candidate/...
  dependencies/node/<lock-id>/...
  dependencies/node/build/<lock-id>/...
  launchers/server
  launchers/runner
  launchers/cli
  cli/bootstrap/mo
  cli/recovery/mo
  projection.lock
```

`release.json` is the canonical local artifact record. It contains the release ID, authority source root, source revision, scope, canonical version, snapshot marker/digest, dependency-lock identity, build-toolchain identity, and one entry per built component with `component`, `version`, `sourceRevision`, `releaseId`, and an absolute artifact path. Each Server entry also records `runtimeIdentifier`, `entrypoint`, `workingDirectory`, `arguments`, and non-secret runtime environment names; each Runner entry records the absolute `nodeExecutable` and supported Node major. A candidate is complete only when the required component files, runtime dependency closure, launch metadata, and manifest are present. `active.json` is one atomically replaced activation record containing a generation, transaction ID, status, the complete CLI/Server/Runner target set, and an `activationLease` when activation is in progress. The lease contains the transaction ID, target generation, activation phase, owner process ID, and owner process-start token. Component-scoped updates replace one entry while retaining the other active entries and are reported as scoped; a full update publishes one shared release ID for all three entries. A candidate is never written in place over an active release.

The canonical release identity is generated before any artifact is compiled: the normalized full source revision is lowercase with no whitespace, `version` is `0.0.0+<sourceRevision>`, and `releaseId` is `mohist-full-<sourceRevision>` for a full release or `mohist-<scope>-<sourceRevision>` for a component release. Build timestamps, host paths, package-manager paths, and compatibility package versions do not enter the identity. Stable absolute launchers read `active.json` at process start, so services never read the source worktree and never observe a partially written target record.

**Alternative considered:** Continue executing from `packages/server/bin`, `packages/runner/dist`, and the selected worktree. Rejected because those paths are mutable build outputs and are not an installation boundary.

**Alternative considered:** Keep one mutable `current` directory and replace files in place. Rejected because a failed copy can leave a mixed Server/Runner release and makes rollback dependent on reconstructing overwritten files.

### D. Publish artifacts into the candidate release, then activate service targets

`UpdateOperations` gains release-oriented build methods and one `BuildEnvironment` contract. .NET commands use `cwd=SnapshotRoot` and receive explicit roots; no command uses a source-tree default `bin`, `obj`, `dist`, `.publish`, `node_modules`, npm cache, or NuGet cache. Node commands use the read-only `NodeWorkspaceViewRoot` described below, whose package directories are symlinks to `SnapshotRoot` and whose generated manifests and `node_modules` are writable build inputs under `BuildWorkspaceRoot`; the view contains no copied source. The environment assigns `MSBuildProjectExtensionsPath` and `BaseIntermediateOutputPath` below `BuildWorkspaceRoot/build/dotnet/obj`, `RestorePackagesPath` and `NUGET_PACKAGES` below `BuildWorkspaceRoot/build/nuget`, `WebOutputRoot` to `BuildWorkspaceRoot/build/web/dist`, `RunnerOutputRoot` to `CandidateRoot/runner/dist`, `CliGeneratedRoot` to `BuildWorkspaceRoot/build/cli-generated`, and the npm cache and workspace view to `BuildWorkspaceRoot/build/npm-cache` and `BuildWorkspaceRoot/build/node-workspace`. The npm/Node adapter resolves all build-time packages from that projection and never installs from or writes to `SnapshotRoot`.

The web builder runs `npm ci --offline --ignore-scripts --cache <BuildWorkspaceRoot>/build/npm-cache --prefix <NodeWorkspaceViewRoot>` against copied root manifests and the lockfile, with workspace package directories symlinked to the snapshot. It then invokes the projected TypeScript and Vite binaries explicitly: `tsc -p <NodeWorkspaceViewRoot>/packages/web/tsconfig.json --noEmit`, Vite with `--config <NodeWorkspaceViewRoot>/packages/web/vite.config.ts --outDir <WebOutputRoot>`, and the web assertion with `<WebOutputRoot>`. The Vite and Server build targets must accept the explicit output-root properties rather than their source-relative defaults. The Server builder invokes `dotnet publish <SnapshotRoot>/packages/server/src/Mohist.Server/Mohist.Server.csproj -c Release -r <runtimeIdentifier> --self-contained true /p:PublishSingleFile=true /p:PublishTrimmed=false /p:BaseIntermediateOutputPath=<BuildWorkspaceRoot>/build/dotnet/obj/ /p:MSBuildProjectExtensionsPath=<BuildWorkspaceRoot>/build/dotnet/obj/ /p:RestorePackagesPath=<BuildWorkspaceRoot>/build/nuget/ /p:WebSourceRoot=<SnapshotRoot>/packages/web /p:WebOutputRoot=<WebOutputRoot> /p:PublishDir=<CandidateRoot>/server/`. The publish target copies only `<WebOutputRoot>` into the Server candidate. The CLI publish uses the same isolated .NET properties and generated-source root. Supported Server runtime identifiers are `linux-x64` and `win-x64`; an unsupported host fails preflight.

The Runner builder reads TypeScript source and package metadata from `SnapshotRoot`, writes compiler output to `CandidateRoot/runner/dist`, and writes `build-info.json` with the transaction's canonical identity and explicit output path; it does not invoke a script that runs `git rev-parse` or writes under the snapshot. Before publication, the builder materializes the lockfile-resolved production dependency closure into `CandidateRoot/runner/node_modules` and writes the runtime `package.json` and dependency manifest beside it. The package-manager cache/toolchain used to build is separate from the release dependency closure. The update may reuse a preinstalled lockfile-matching cache but does not fetch dependencies from the network; if the closure is unavailable, staging fails before activation.

The CLI is published into `CandidateRoot/cli` from the same snapshot. The builder supplies the canonical identity through generated source under `BuildWorkspaceRoot/build/cli-generated` and redirects .NET intermediates and publish output to transaction-owned paths. The generated identity is compiled into the executable; it is not read from the mutable authority worktree or launcher environment.

The Runner build manifest is extended from `{ gitHash, builtAt }` to include `component`, `version`, `sourceRevision`, and `releaseId`, plus the dependency-lock identity. Server `RuntimeBuildInfo` is extended with the same release identity and reads it from generated artifact-owned metadata. The service environment includes expected identity values only as a cross-check; a missing or conflicting embedded value is unavailable identity, and no managed artifact falls back to source HEAD.

Extend `IServiceInstaller` with a target-set activation seam. A `RuntimeTargetSet` contains the complete CLI, Server, and Runner entries, selected CLI slot, absolute launcher paths, managed release working directories, data roots, runtime identifiers, entrypoints, arguments, supported absolute Node executable, and identity environment values. The Server candidate is a self-contained single-file executable at `server/Mohist.Server` for `linux-x64` or `win-x64`; its launcher never invokes `dotnet`, an SDK, a project file, or a source path. The Runner launcher uses the recorded absolute Node executable and the candidate `runner/dist/cli.js`, with its recorded dependency root. The systemd backend and the Windows scheduled-task/Startup backend are installed once with stable absolute launchers; those launchers resolve the component entry from `active.json` and never use a source worktree or relative `dist` path. Existing data directories, enrollment credentials, operator credentials, and `RUNNER_ROOT` remain outside the release and are passed through unchanged. Per-update service-unit files and direct CLI copies are not rewritten: the stable launchers and the atomic active record are the activation boundary. A launcher may start a candidate during `ActivationAuthorized`, `CandidateActivated`, or `Verifying` only when the active record and transaction record contain the same activation lease and its owner process-start token is live; this is the sole exception to the unresolved-transaction startup rule.

The activation coordinator writes or replaces the complete `active.json` target record only after the candidate release is complete and the transaction write-ahead record is durable. It then reloads the stable launcher units and restarts only the components in the update scope. A component update changes one target entry but publishes the complete record atomically. Legacy unit migration stages unit files and keeps backups under the transaction record; a partial migration is reconciled before any candidate is treated as active.

**Alternative considered:** Continue rendering a concrete versioned artifact path into each service unit for every update. Rejected because systemd and Windows activation write separate unit/task definitions and a crash can expose a mixed target set. Stable launchers plus one atomically replaced active record give both backends the same activation boundary while keeping each release inspectable.

### E. Make runtime identity a shared, fail-closed contract

Introduce a small `RuntimeIdentity` value with `Component`, `Version`, `SourceRevision`, and `ReleaseId`. The expected value is read from the candidate `release.json` and never recomputed from source HEAD during verification. JSON uses the lower-camel field names `component`, `version`, `sourceRevision`, and `releaseId`.

Readback uses existing surfaces with additive identity fields:

- Server `/api/health` and `/api/system/info` expose `component`, `version`, `sourceRevision`, and `releaseId` from artifact-owned `RuntimeBuildInfo`; existing `gitHash` may remain as a compatibility alias but is not the consistency field. `RuntimeBuildInfo` fails closed when generated identity metadata is absent, and launcher environment values are compared against the embedded values rather than used to replace them.
- Runner `/api/runner/identity` exposes `component`, `version`, `sourceRevision`, and `releaseId` from the installed build manifest, plus the exact `runnerId` and Server-issued `connectionGeneration`; existing `buildGitHash` may remain as a compatibility alias but is not the consistency field. The Server stores identity per connection generation and never chooses a Runner only by hostname.
- The CLI identity reader invokes the installed CLI's exact `mo runtime identity --json` surface. The command writes exactly one JSON object to stdout with `component: "cli"`, `version`, `sourceRevision`, and `releaseId`; it writes diagnostics to stderr and exits nonzero when embedded identity metadata is absent, malformed, or ambiguous. A generated `MohistRuntimeIdentity.g.cs` file is supplied to the isolated `dotnet publish` inputs from the candidate manifest, so the four values are compiled into the published CLI assembly rather than inferred from the current source directory. The assembly metadata and `release.json` are cross-checked at staging time.

The authenticated Runner handshake is the only source of connection identity. The Server derives `runnerId` from the machine credential rather than trusting a query value, allocates a monotonically increasing opaque `connectionGeneration` for each accepted connection, and stores `(runnerId, connectionGeneration, connectionId)`. A new connection invalidates the previous generation; disconnect only removes the mapping when both connection ID and generation still match. The handshake and every Runner identity response carry the generation, and `/api/runner/identity` requires the exact `runnerId` plus `connectionGeneration`; hostname is diagnostic metadata only. A request for a stale generation is unavailable and cannot fall back to another connection.

`RuntimeConsistencyValidator` captures the active Runner ID and generation before the identity request and rejects a response when either value changed during the request. It accepts the expected identity and produces check results containing expected and observed values. CLI, Server, and Runner identity checks are required only for the components in the operation scope and any component the operation actually activates or restarts. Unavailable, ambiguous, stale, or mismatched values are failures. Readiness and asset checks can retain their existing advisory behavior where the specs do not make them identity gates, but an identity mismatch can never become `Recovered` with exit code zero. The normal success line is emitted only after all required checks pass.

**Alternative considered:** Compare each running process with the current source HEAD. Rejected because source HEAD may be read from a different root or may advance while the update is in flight; it also cannot prove that the installed artifact is the one just built.

**Alternative considered:** Treat process startup, HTTP health, or Runner reconnection as proof of freshness. Rejected because a healthy old process satisfies those signals while still violating the issue's core contract.

### F. Coordinate activation and rollback with an explicit transaction state machine

Add a `RuntimeActivationCoordinator` that owns the destructive boundary. The transaction states are:

1. `Prepared`: source context and previous active target captured.
2. `CandidateStaged`: every required artifact for the declared scope and its manifest are complete.
3. `ActivationAuthorized`: the coordinator durably recorded the activation lease and intended target generation, but has not yet published the candidate active record.
4. `CandidateActivated`: the complete candidate active-target record was atomically published with the matching activation lease.
5. `Verifying`: readiness and required identity readbacks are in progress under the matching activation lease.
6. `Committed`: candidate is the last verified release and the transaction is terminally successful.
7. `Cancelling`: cancellation cleanup or rollback is in progress.
8. `RollingBack`: previous target restoration is in progress.
9. `RolledBack`: previous verified target was restored and verified; the update still returns failure.
10. `Cancelled`: cancellation cleanup or rollback completed; the command returns exit code 130 and never success.
11. `NoVerifiedRuntime`: candidate was stopped/removed because no verified target existed.
12. `RecoveryFailed`: candidate services are stopped/disabled, `active.json` has `status: "none"`, and neither candidate nor previous target could be verified.
13. `OrphanedLock`: recovery-only diagnostic state created when a stale acquisition lock has no durable `Prepared` record; it transitions immediately to `RolledBack` or `NoVerifiedRuntime` after the lock recovery record is durable.

For a full update, `CandidateStaged` means that the CLI, Server, Runner, web assets, dependency closure, launch metadata, and identity metadata are complete. No CLI-slot replacement, service stop, service-unit change, or active-record publication occurs before that state is durable. The coordinator then persists `ActivationAuthorized` with an activation lease containing the transaction ID, target generation, phase, owner process ID, and owner process-start token. It publishes `active.json` with the same lease, persists `CandidateActivated`, persists `Verifying`, and restarts only the requested scope. A stable launcher that sees a matching live lease starts the candidate and does not invoke reconciliation; it is an activation participant, not a second transaction owner. A launcher with a missing or stale lease invokes the reconciler, which claims recovery ownership only after the recorded owner is no longer live. The coordinator commits only after identity checks pass. A crash in `Prepared` before `CandidateStaged` cleans the incomplete candidate without changing the previous active record. A crash in `ActivationAuthorized` or after active-record publication transfers ownership to reconciliation when the activation owner token is stale; a live owner leaves activation in progress and does not start a competing recovery operation.

On candidate failure after activation it records `RollingBack`, restores the previous active record and any desired service states, restarts affected services, and verifies the restored identity. If cancellation arrives before activation, it records `Cancelled` after candidate cleanup. If cancellation arrives after activation or after a service/CLI target effect, it records `Cancelling`, restores the previous target when possible, verifies the restored scope, and then records `Cancelled`; if restoration cannot be verified it follows the `RecoveryFailed` quarantine path and records cancellation as the failed command outcome. `Cancelling` is reconciled exactly like `RollingBack` before any new update. If there is no previous verified release, it stops or disables the candidate and atomically publishes an `active.json` record with `status: "none"` and no target set. If the previous release cannot be verified, it also stops or disables every candidate service, atomically clears the active target to `status: "none"`, preserves the transaction and candidate paths for diagnosis, and ends in `RecoveryFailed`. A stable launcher refuses to start Server or Runner while the active record has no target. Candidate files are deleted only after the terminal state is persisted, so a failed cleanup remains diagnosable.

`RuntimeTransactionReconciler` runs before a new update and from the stable managed launcher. For every nonterminal record it compares the active record with the recorded candidate and previous sets. A `Prepared` record without `CandidateStaged` is cleaned and terminally marked; an `ActivationAuthorized` record without a published matching active record is cleaned; an active candidate with a live matching activation lease is reported as `activation_in_progress` without mutation; an active candidate with a stale or missing lease is claimed by reconciliation and bounded-verified or rolled back; `RollingBack` and `Cancelling` records resume restoration; a missing or corrupt active record attempts the previous target and ends in `RecoveryFailed` only after stopping candidate services and atomically publishing `active.json` with `status: "none"` when that target cannot be verified. Recovery has a bounded timeout using `TimeProvider`; it does not poll without a deadline, and no concurrent update can start while a record remains unresolved.

The transaction store uses one OS-backed exclusive lock file per managed runtime root. The lock file is created with exclusive creation and contains the schema version, transaction ID, owner process ID, owner process-start token, creation time, and phase (`acquiring`, `prepared`, or `terminal`); the file contents and containing directory are flushed before the lock acquisition call returns. The process-local semaphore is only an optimization and never proves ownership. A lock is stale only when the recorded owner process-start token is not live or the transaction is already terminal; an ambiguous owner fails closed. If an `acquiring` lock has no transaction record, reconciliation first proves that the owner token is stale, persists an `OrphanedLock` diagnostic record with the current active target snapshot, then persists `RolledBack` when a previous verified target exists or `NoVerifiedRuntime` otherwise. It removes the lock only after that record is durable; a record-write failure leaves the lock for retry. Reconciliation must resolve the transaction before removing any other stale lock. The lock remains held until the terminal state is durably written; a release failure sets `lockReleasePending` in that terminal record and is retried before the next update. Transaction files and `active.json` are written to same-directory temporary files, flushed through the injected atomic-file boundary, and replaced with write-through semantics; Unix directory entries are flushed after rename and Windows uses the platform replace operation with write-through semantics. Kill-point tests cover acquisition without `Prepared`, each state, target, service, CLI-slot, activation-lease, and lock boundary.

The selected CLI slot is part of `active.json`. `Committed` and `RolledBack` select a verified release CLI slot. `NoVerifiedRuntime`, `Cancelled` after failed candidate activation, and `RecoveryFailed` set the Server/Runner target set to none but select the last trusted recovery CLI slot. The stable `launchers/cli` wrapper executes only that slot for normal commands. If no trusted recovery slot exists, it dispatches only the exact `install cli` command to the always-installed `cli/bootstrap/mo` helper and prints `bootstrap_required` for every other command; the helper and the trusted payload recorded by `bootstrap.json` are outside both update slots, are never replaced by candidate activation, and recreate the bootstrap helper, recovery slot, stable launchers, and runtime-root metadata. On Windows the equivalent `.cmd` wrapper applies the same command restriction. If the helper or payload is missing, the wrapper reports installation corruption and directs the operator to reinstall the original Mohist CLI distribution before rerunning `mo install cli`. This makes `mo install cli` an executable recovery path without granting the candidate slot general execution authority.

The existing Runner-stop recovery becomes one operation in this coordinator. `FinalizeAfterServerAsync` and `RunRecoveryStageAsync` delegate to it instead of independently deciding whether to start the Runner. Required identity failure is never converted to the existing warning-based `UpdateOutcome.Recovered` state.

**Alternative considered:** Keep recovery as best-effort Runner start logic in `SourceCodeUpdater`. Rejected because it cannot restore service definitions, CLI slots, or a previous artifact set and cannot distinguish a restored verified runtime from an unverified process.

**Alternative considered:** Store transaction state only in `UpdateContext`. Rejected because CLI self-update crosses process boundaries and a process crash must not lose the candidate/previous-target relationship.

### G. Keep the current orchestration facade and fake boundaries

`SourceCodeUpdater` remains the command facade and stage coordinator, but it passes `UpdateSourceContext`, `RuntimeRelease`, and `UpdateTransaction` to focused collaborators:

- `SourceContextResolver` validates and identifies the selected source.
- `SourceSnapshotStore` materializes and protects the immutable build snapshot.
- `ManagedRuntimeStore` stages releases and persists active/transaction records.
- `RuntimeArtifactBuilder` invokes Server, Runner, and CLI builds through `ICommandExecutor`.
- `RuntimeActivationCoordinator` snapshots, activates, commits, and rolls back service targets.
- `RuntimeTransactionReconciler` resumes or rolls back nonterminal transactions before new work or managed startup.
- `RuntimeConsistencyValidator` performs strict readback against a fixed expected identity.
- `UpdateReport` renders target, observed, failed-stage, and recovery facts for terminal output and the server outcome payload.

These collaborators hide path, serialization, activation, and comparison rules from the facade. All filesystem, process, service, HTTP, and clock access remains injectable; tests do not invoke real network, process, systemd, scheduled-task, Git, or database services.

**Alternative considered:** Put release paths, manifest parsing, rollback, and identity formatting directly in the stage methods. Rejected because the same rules would be duplicated across full, Server-only, Runner-only, and CLI-continuation flows.

### H. Make component update guarantees explicit

The command surface uses one source snapshot and one activation/recovery protocol, but the artifact and verification scope is explicit:

| Command | Build from snapshot | Active targets changed | Required identity checks | Rollback scope | Success claim |
| --- | --- | --- | --- | --- | --- |
| `mo update` | CLI, Server, Runner | Complete CLI/Server/Runner set | CLI, Server, Runner | Complete previous set | Global consistency |
| `mo update cli` | CLI | CLI entry/slot only | CLI | Previous CLI entry/slot | CLI-scoped |
| `mo update server` | Server | Server entry/service only | Server | Previous Server entry/service | Server-scoped |
| `mo update runner` | Runner | Runner entry/service only | Runner | Previous Runner entry/service | Runner-scoped |

Component-scoped commands retain untouched entries in `active.json` and explicitly report them as untouched. If an implementation restarts or activates another component, that component is added to the required checks and rollback scope; it is never silently omitted. Only `mo update` may claim that CLI, Server, and Runner form one globally verified release.

A full `mo update` has a preflight requirement that a managed Runner target already exists. If no Runner target or service is installed, preflight fails with `RunnerNotInstalled` before candidate staging, service stop, CLI replacement, or active-record publication and directs the operator to `mo install runner`; the full command never silently skips Runner or narrows its result. An installed but stopped Runner has its desired stopped state recorded, is started temporarily when required for candidate identity verification, and is returned to that state after commit. An installed but unreachable Runner fails the transaction and uses the normal rollback path.

### I. Make the Server update surface projection-only

`POST /api/system/update` returns HTTP 409 with `UpdateMutationOwnedByCli` and does not create a background job. `GET /api/system/update/status` and `GET /api/system/consistency` only read the latest CLI outcome and local runtime facts; they do not call an advance method, run a command, restart a service, acquire an update lock, or mark a job successful. `POST /api/system/update/outcome` remains a projection sink for CLI-reported terminal and nonterminal facts, not an activation authority.

The persisted `SystemUpdateJobState` schema is versioned and records `owner` (`web`, `cli`, or `legacy`), `status`, `scope`, `sourceMode`, target identity, sequence, and `lockReleasePending`. Existing records without `owner` cannot prove whether they came from the old web mutator or the old CLI outcome endpoint. Startup therefore migrates them to schema version 2 with `owner: "legacy"`; active ownerless records become terminal `legacy-unknown` with stage `Legacy ownership unknown`, reason `Legacy update ownership cannot be proven; run mo update`, preserved target facts and logs, and no attempt to resume or claim them. Ownerless terminal records retain their terminal status while gaining the `legacy` owner. `rejected` is a terminal status reserved for records that are positively identified as web-owned. The registered `SystemUpdateStartupReconciler` replaces `SystemUpdateRecoveryService` and runs as a direct hosted service before the HTTP listener accepts requests. It persists the legacy or rejected state, completion time, reason, and log entry before releasing the stale lock. If release fails it persists `lockReleasePending: true`, leaves the terminal state unchanged, and retries the release on the next startup; it never resumes an ownerless record.

A CLI transaction creates `transactions/<job-id>/projection-lease.json` before its first outcome. The lease binds the job ID, a cryptographically random nonce hash, scope, source mode, target identity, and last accepted sequence, and the local transaction record must exist with owner `cli`. `IUpdateProjectionStore` owns a per-runtime-root OS-backed `projection.lock` and a same-directory `projection-intent.json` write-ahead record. Under that lock it reads the local transaction, lease, and latest Server projection, requires their last accepted sequence and payload hash to agree, validates the transition, writes the complete next projection to the intent record, atomically replaces the lease and Server projection, and removes the intent only after both replacements are durable. Startup resolves an intent before serving status; if one replacement completed, it completes the other from the intent, and if neither completed it discards the intent after verifying the expected old hash. A process-local semaphore is only an optimization.

`SystemUpdateOutcomeRequest` requires the job ID, nonce, sequence, status, stage, outcome, scope, source mode, and target facts. The allowed projection transitions are `prepared -> running|waiting-for-reconnect|failed|cancelled`, `running -> running|waiting-for-reconnect|succeeded|failed|recovered|cancelled`, and `waiting-for-reconnect -> running|waiting-for-reconnect|succeeded|failed|recovered|cancelled|superseded`. Terminal `succeeded`, `failed`, `recovered`, `superseded`, `cancelled`, `rejected`, and `legacy-unknown` states accept only an identical duplicate payload. `succeeded` requires a matching durable `Committed` transaction, `recovered` requires a matching durable `RolledBack` transaction, and `cancelled` requires `Cancelled` or `RecoveryFailed`; no terminal state can transition to another outcome. The Server rejects an unknown job, missing or mismatched lease, changed target facts, invalid transition, non-increasing sequence, or mismatched old hash and never creates a projection for an arbitrary job ID. A repeated sequence with the identical payload is idempotent; concurrent requests are serialized by the OS-backed projection lock and compare-and-set intent. The status endpoint never advances the local transaction.

## Risks / Trade-offs

- [Existing custom systemd units and Windows launchers may continue to execute source-bound paths] -> Detect legacy targets during preflight, report the migration command and exact unsupported dependency, and do not claim them as managed verified releases.
- [The first managed update can leave no runnable service if it fails and no verified release exists] -> Stop/remove the candidate rather than leave an unverified process, report `NoVerifiedRuntime`, and provide the concrete reinstall/retry action.
- [Release directories consume disk space] -> Retain the active and previous verified releases during the transaction, clean failed candidates after their terminal record is persisted, and make retention limits an explicit follow-up rather than deleting rollback material prematurely.
- [Runner production dependencies are larger than the compiled dist] -> Copy only the lockfile-resolved production closure into the candidate, record its lock identity, and fail before activation when the closure cannot be staged.
- [Server or Runner versions without the new identity fields cannot be proven current] -> Treat missing identity as unavailable and require a managed rebuild; never downgrade this to a warning.
- [Launcher environment or a stale Runner connection can lie about artifact identity] -> Use embedded Server/CLI metadata, local Runner manifests, exact runner ID/connection generation, and environment equality checks only; reject hostname-only or self-reported identity.
- [CLI replacement can fail after its executable has been swapped] -> Use two managed CLI slots, record the previous slot in the transaction, and restore it during rollback or resume through the transaction ID.
- [An older installed CLI cannot understand the managed transaction] -> Fail closed with `bootstrap_required` and require the supported current-CLI bootstrap before any managed mutation; do not attempt compatibility parsing in the old binary.
- [Runner reconnect and Server readiness are asynchronous] -> Use existing bounded probes with injected `TimeProvider`, capture the last observed identity, and report timeout as an actionable verification failure.
- [Systemd and Windows activation semantics differ] -> Keep platform-specific stable-launcher rendering inside `IServiceInstaller`, pass the same `RuntimeTargetSet` to both, and test rendered commands plus rollback with fake command/file boundaries.
- [The source authority worktree changes during a long build] -> Build only from the read-only revision snapshot and fail preflight if snapshot materialization or revision validation fails.
- [A crash leaves a durable nonterminal transaction] -> Run `RuntimeTransactionReconciler` before new updates and managed startup, using the recorded candidate/previous target sets to commit or restore deterministically.
- [A second update could race the first transaction] -> Acquire an exclusive transaction lock before staging, reject concurrent updates with the active job ID, and release the lock only in a terminal state.
- [The web update job currently mutates source-bound artifacts] -> Reject `POST /api/system/update`, quarantine persisted nonterminal web jobs on Server startup, and keep status/consistency reads free of command and service side effects; the CLI transaction remains authoritative for local success.

## Migration Plan

1. Add `RuntimeIdentity`, `UpdateSourceContext`, fixed `RuntimeRootResolver`, immutable snapshot materialization, writable build/candidate roots, the symlink-only npm workspace view, release manifest serialization, transaction locking, and the transaction store with unit tests. Add `mo install cli` bootstrap behavior and route all update scopes through the single source resolver while returning `bootstrap_required` for legacy CLIs.
2. Add complete staged Server, web, Runner, and CLI artifact builds from `SnapshotRoot` or `NodeWorkspaceViewRoot` with the explicit `BuildEnvironment` contract and all writes redirected to transaction-owned roots. Update npm projection, Runner build-info/dependency closure generation, generated CLI identity metadata, self-contained Server publish/launcher metadata, and Server artifact-owned runtime identity to carry release metadata. Add dry-run output for snapshot, toolchain, build, dependency, release, and target identities.
3. Add stable absolute launchers for systemd and Windows scheduled-task/Startup fallback. Make `active.json` the single atomic target-set pointer and migrate supported units without silently rewriting unsupported custom source-bound units.
4. Add activation/rollback/cancellation orchestration, transaction-ID continuation, durable CLI-slot selection, lock/write-through boundaries, and crash reconciliation. Publish a complete candidate active-target record only after all scoped artifacts are staged; commit it as verified only after required readback, and restore it when verification, cancellation, or reconciliation fails.
5. Make CLI, Server, and Runner identity checks fail closed, implement the authenticated Runner connection-generation protocol and explicit full/component operation matrix, reject and quarantine the Server web mutation path, and extend terminal/outcome reporting with target/observed facts.
6. Update focused CLI, snapshot/write-boundary, npm-projection, dependency-closure, installer, Server identity, Runner handshake/generation, artifact-manifest, web-route/status/consistency, bootstrap/CLI-slot, cancellation, lock-durability, and transaction tests, then add scenarios for explicit/default roots, source mutation during build, source/workspace removal, every operation scope, canonical identity generation, stale identities, wrong connection generation, successful commit, kill-point recovery before/after full candidate staging, rollback quarantine, cancellation at every destructive boundary, no verified release, no installed Runner, legacy bootstrap, CLI continuation failure, stale web-job startup/retry, and both service backends.

For an existing installation, the operator first bootstraps the current CLI and stable launchers when the installed CLI reports `bootstrap_required`. The bootstrap does not claim the legacy source-bound runtime as verified. A supported service install/update operation must install the stable launchers before relying on automatic activation; custom source-bound units are not silently rewritten. User data roots and credentials are retained. If the first candidate fails before any managed release is verified, the candidate is removed or disabled as specified; the system does not pretend that the old source-bound unit is a verified managed release. Once a managed release exists, rollback restores the previous `active.json` target set and its recorded artifact paths.

Rollback is transaction-local and automatic. A failed candidate restores the previous verified release when possible; if restoration cannot be verified, the command reports `RecoveryFailed` and never prints success. Code rollback is a normal deployment rollback to the previous CLI/server build; no database migration or external service migration is introduced by this change.

## Open Questions

- Should release cleanup retain exactly one previous verified release, or should a configurable count/size policy be added with the initial store?
- Should a future version support dirty worktrees by recording a content digest, or should updates continue to require clean Git revisions permanently?
