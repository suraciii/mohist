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
- the clean/usable validation result needed to build an identity; and
- the requested update scope and CLI path needed by the transaction.

The resolver runs once before CLI publication, service changes, or artifact builds. It rejects a missing root, a root without `Mohist.sln`, an unavailable Git revision, and a dirty worktree, then materializes the resolved revision into a staging snapshot and makes that snapshot read-only to builders. The clean-worktree rule is deliberate: a Git HEAD alone cannot identify uncommitted content, and the issue acceptance criteria explicitly use a clean source directory. Snapshot creation and a second revision check on the snapshot are part of preflight; a failure stops before managed runtime state changes. Every operation receives the context rather than calling `ResolveRepoRoot` or `git rev-parse` again.

The existing mutable `UpdateContext.SourceHead` becomes a value from this context. Human output and dry-run output use the same context to label explicit/default source mode, authority root, snapshot root, and target revision. Build commands use only `SnapshotRoot`; the requested repository root is retained for reporting and source-authority diagnostics.

**Alternative considered:** Let each stage resolve the root and read HEAD when it needs it. Rejected because the process working directory, source branch, or source contents can differ between stages, recreating the exact build-one-version/run-another failure.

### B. Persist the source context across CLI self-update continuation

Create a transaction record before the CLI self-update stage. The record stores the source context including `SnapshotRoot`, update scope, job ID, candidate/previous active-target sets, CLI slots, service targets, required identities, and state. The pre-update process invokes the refreshed CLI with an internal transaction identifier, for example `--continue-after-cli-update --update-id <id>`, instead of reconstructing the whole context from command-line strings. The continuation loads and validates the record before it performs any post-CLI stage.

The record is written through an `IUpdateTransactionStore` using a temporary file plus atomic rename. It is local state under the managed runtime root, not server database state. The CLI-owned reconciler reads every nonterminal record before a new update and from the stable managed launcher before it starts a target associated with that record. The Server outcome endpoint remains a reporting sink; failure to post the outcome cannot change the local activation result.

**Alternative considered:** Pass the repository root, source hash, previous target, and recovery flags as hidden continuation arguments. Rejected because duplicated arguments can drift, are fragile to quoting, and do not provide durable recovery when the continuation process exits unexpectedly.

### C. Use a versioned managed release store

Add a `ManagedRuntimeStore` behind the existing `IFileSystem` boundary. The per-user store has this logical layout:

```text
<managed-runtime-root>/
  releases/<release-id>/
    release.json
    cli/mo
    server/...
    runner/...
  active.json
  transactions/<job-id>.json
  launchers/server
  launchers/runner
  launchers/cli
```

`release.json` is the canonical local artifact record. It contains the release ID, authority source root, source revision, component scope, and one entry per built component with `component`, `version`, `sourceRevision`, `releaseId`, and an absolute artifact path. A candidate is complete only when the required component files and manifest are present. `active.json` is one atomically replaced activation record containing a generation, transaction ID, status, and the complete CLI/Server/Runner target set. Component-scoped updates replace one entry while retaining the other active entries and are reported as scoped; a full update publishes one shared release ID for all three entries. A candidate is never written in place over an active release.

The release ID is derived from the target source revision plus the component build identity. The source revision remains the primary equality fact; component versions and release ID make the installed artifact set inspectable and prevent a Server artifact from being paired silently with a Runner artifact from another build. Stable absolute launchers read `active.json` at process start, so services never read the source worktree and never observe a partially written target record.

**Alternative considered:** Continue executing from `packages/server/bin`, `packages/runner/dist`, and the selected worktree. Rejected because those paths are mutable build outputs and are not an installation boundary.

**Alternative considered:** Keep one mutable `current` directory and replace files in place. Rejected because a failed copy can leave a mixed Server/Runner release and makes rollback dependent on reconstructing overwritten files.

### D. Publish artifacts into the candidate release, then activate service targets

`UpdateOperations` gains release-oriented build methods. The Server is published into the candidate `server` directory rather than started with `dotnet run`; the publish command is executed with `SnapshotRoot` as its working directory and includes the web assets required by the publish output. The Runner build receives `MOHIST_REPO_ROOT=SnapshotRoot` from the source context and writes its build manifest into the staged Runner artifact. The CLI is published into the candidate `cli` directory from the same snapshot, with generated identity metadata supplied by the transaction rather than read from the mutable authority worktree.

The Runner build manifest is extended from `{ gitHash, builtAt }` to include `component`, `version`, `sourceRevision`, and `releaseId`. Server `RuntimeBuildInfo` is extended with the same release identity and is populated from explicit service environment/manifest data before any source fallback. The service environment includes the expected source revision and release ID, so a managed artifact remains identifiable after its source worktree is moved or removed.

Extend `IServiceInstaller` with a target-set activation seam. A `RuntimeTargetSet` contains the complete CLI, Server, and Runner entries, absolute launcher paths, managed release working directories, data roots, and identity environment values. The systemd backend and the Windows scheduled-task/Startup backend are installed once with stable absolute launchers; those launchers resolve the component entry from `active.json` and never use a source worktree or relative `dist` path. Existing data directories, enrollment credentials, operator credentials, and `RUNNER_ROOT` remain outside the release and are passed through unchanged.

The activation coordinator writes or replaces the complete `active.json` target record only after the candidate release is complete and the transaction write-ahead record is durable. It then reloads the stable launcher units and restarts only the components in the update scope. A component update changes one target entry but publishes the complete record atomically. Legacy unit migration stages unit files and keeps backups under the transaction record; a partial migration is reconciled before any candidate is treated as active.

**Alternative considered:** Continue rendering a concrete versioned artifact path into each service unit for every update. Rejected because systemd and Windows activation write separate unit/task definitions and a crash can expose a mixed target set. Stable launchers plus one atomically replaced active record give both backends the same activation boundary while keeping each release inspectable.

### E. Make runtime identity a shared, fail-closed contract

Introduce a small `RuntimeIdentity` value with `Component`, `Version`, `SourceRevision`, and `ReleaseId`. The expected value is read from the candidate `release.json` and never recomputed from source HEAD during verification. JSON uses the lower-camel field names `component`, `version`, `sourceRevision`, and `releaseId`.

Readback uses existing surfaces with additive identity fields:

- Server `/api/health` and `/api/system/info` expose `component`, `version`, `sourceRevision`, and `releaseId` from `RuntimeBuildInfo`; existing `gitHash` may remain as a compatibility alias but is not the consistency field.
- Runner `/api/runner/identity` exposes `component`, `version`, `sourceRevision`, and `releaseId` from the installed build manifest; existing `buildGitHash` may remain as a compatibility alias but is not the consistency field.
- The CLI identity reader invokes the installed CLI's exact `mo runtime identity --json` surface. The command writes exactly one JSON object to stdout with `component: "cli"`, `version`, `sourceRevision`, and `releaseId`; it writes diagnostics to stderr and exits nonzero when embedded identity metadata is absent, malformed, or ambiguous. A generated `MohistRuntimeIdentity.g.cs` file is supplied to the isolated `dotnet publish` inputs from the candidate manifest, so the four values are compiled into the published CLI assembly rather than inferred from the current source directory. The assembly metadata and `release.json` are cross-checked at staging time.

`RuntimeConsistencyValidator` accepts the expected identity and produces check results containing expected and observed values. CLI, Server, and Runner identity checks are required only for the components in the operation scope and any component the operation actually activates or restarts. Unavailable, ambiguous, or mismatched values are failures. Readiness and asset checks can retain their existing advisory behavior where the specs do not make them identity gates, but an identity mismatch can never become `Recovered` with exit code zero. The normal success line is emitted only after all required checks pass.

**Alternative considered:** Compare each running process with the current source HEAD. Rejected because source HEAD may be read from a different root or may advance while the update is in flight; it also cannot prove that the installed artifact is the one just built.

**Alternative considered:** Treat process startup, HTTP health, or Runner reconnection as proof of freshness. Rejected because a healthy old process satisfies those signals while still violating the issue's core contract.

### F. Coordinate activation and rollback with an explicit transaction state machine

Add a `RuntimeActivationCoordinator` that owns the destructive boundary. The transaction states are:

1. `Prepared`: source context and previous active target captured.
2. `CandidateStaged`: required artifacts and manifest are complete.
3. `CandidateActivated`: the complete candidate active-target record was atomically published.
4. `Verifying`: readiness and required identity readbacks are in progress.
5. `Committed`: candidate is the last verified release and the transaction is terminally successful.
6. `RollingBack`: previous target restoration is in progress.
7. `RolledBack`: previous verified target was restored and verified; the update still returns failure.
8. `NoVerifiedRuntime`: candidate was stopped/removed because no verified target existed.
9. `RecoveryFailed`: neither candidate nor previous target could be verified.

The coordinator persists `CandidateStaged` with the candidate and previous complete target sets before changing a service unit, active record, or CLI slot. It atomically replaces `active.json` once for the complete target set, then records `CandidateActivated` and `Verifying`. On candidate failure after activation it records `RollingBack`, restores the previous active record and any recorded unit/slot backups, restarts affected services, and verifies the restored identity. If there is no previous verified release, it stops or disables the candidate and removes its active target. Candidate files are deleted only after the terminal state is persisted, so a failed cleanup remains diagnosable.

`RuntimeTransactionReconciler` runs before a new update and from the stable managed launcher. For every nonterminal record it compares the active record with the recorded candidate and previous sets. An unapplied candidate is cleaned and terminally marked; an active candidate is bounded-verified and either committed or rolled back; a `RollingBack` record resumes restoration; a missing or corrupt active record attempts the previous target and ends in `RecoveryFailed` if that target cannot be verified. Recovery has a bounded timeout using `TimeProvider`; it does not poll without a deadline, and no concurrent update can start while a record remains unresolved.

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

## Risks / Trade-offs

- [Existing custom systemd units and Windows launchers may continue to execute source-bound paths] -> Detect legacy targets during preflight, report the migration command and exact unsupported dependency, and do not claim them as managed verified releases.
- [The first managed update can leave no runnable service if it fails and no verified release exists] -> Stop/remove the candidate rather than leave an unverified process, report `NoVerifiedRuntime`, and provide the concrete reinstall/retry action.
- [Release directories consume disk space] -> Retain the active and previous verified releases during the transaction, clean failed candidates after their terminal record is persisted, and make retention limits an explicit follow-up rather than deleting rollback material prematurely.
- [Server or Runner versions without the new identity fields cannot be proven current] -> Treat missing identity as unavailable and require a managed rebuild; never downgrade this to a warning.
- [CLI replacement can fail after its executable has been swapped] -> Use two managed CLI slots, record the previous slot in the transaction, and restore it during rollback or resume through the transaction ID.
- [Runner reconnect and Server readiness are asynchronous] -> Use existing bounded probes with injected `TimeProvider`, capture the last observed identity, and report timeout as an actionable verification failure.
- [Systemd and Windows activation semantics differ] -> Keep platform-specific stable-launcher rendering inside `IServiceInstaller`, pass the same `RuntimeTargetSet` to both, and test rendered commands plus rollback with fake command/file boundaries.
- [The source authority worktree changes during a long build] -> Build only from the read-only revision snapshot and fail preflight if snapshot materialization or revision validation fails.
- [A crash leaves a durable nonterminal transaction] -> Run `RuntimeTransactionReconciler` before new updates and managed startup, using the recorded candidate/previous target sets to commit or restore deterministically.
- [A second update could race the first transaction] -> Acquire an exclusive transaction lock before staging, reject concurrent updates with the active job ID, and release the lock only in a terminal state.
- [The web update job currently mutates source-bound artifacts] -> Reject `POST /api/system/update` with `UpdateMutationOwnedByCli`; status and outcome surfaces remain projections and the CLI transaction remains authoritative for local success.

## Migration Plan

1. Add `RuntimeIdentity`, `UpdateSourceContext`, immutable snapshot materialization, release manifest serialization, and the transaction store with unit tests. Preserve current command registration while routing all update scopes through the single source resolver.
2. Add staged Server, Runner, and CLI artifact builds from `SnapshotRoot`. Update Runner build-info generation, generated CLI identity metadata, and Server runtime identity to carry release metadata. Add dry-run output for release paths and target identities.
3. Add stable absolute launchers for systemd and Windows scheduled-task/Startup fallback. Make `active.json` the single atomic target-set pointer and migrate supported units without silently rewriting unsupported custom source-bound units.
4. Add activation/rollback orchestration, transaction-ID continuation, and crash reconciliation. Publish a complete candidate active-target record only after staging; commit it as verified only after required readback, and restore it when verification or reconciliation fails.
5. Make CLI, Server, and Runner identity checks fail closed, implement the explicit full/component operation matrix, reject the Server web mutation endpoint, and extend terminal/outcome reporting with target/observed facts.
6. Update focused CLI, snapshot, installer, Server, Runner, artifact-manifest, web-route, and transaction tests, then add scenarios for explicit/default roots, source mutation during build, every operation scope, stale identities, successful commit, kill-point recovery, no verified release, CLI continuation failure, and both service backends.

For an existing installation, the first managed update records the legacy target and creates a managed candidate. A supported service install/update operation must install the stable launchers before relying on automatic activation; custom source-bound units are not silently rewritten. User data roots and credentials are retained. If the first candidate fails before any managed release is verified, the candidate is removed or disabled as specified; the system does not pretend that the old source-bound unit is a verified managed release. Once a managed release exists, rollback restores the previous `active.json` target set and its recorded artifact paths.

Rollback is transaction-local and automatic. A failed candidate restores the previous verified release when possible; if restoration cannot be verified, the command reports `RecoveryFailed` and never prints success. Code rollback is a normal deployment rollback to the previous CLI/server build; no database migration or external service migration is introduced by this change.

## Open Questions

- Should the managed runtime root be fixed to `~/.local/share/mohist/runtime` and `%LOCALAPPDATA%/Mohist/runtime`, or be configurable before the first implementation lands?
- Should release cleanup retain exactly one previous verified release, or should a configurable count/size policy be added with the initial store?
- Should the identity fields be added only to existing health/system-info/Runner identity payloads, or should a dedicated authenticated runtime-identity endpoint be introduced later?
- Should Windows scheduled-task/Startup activation ship in the same rollout as systemd activation, or be explicitly gated until its launcher rollback tests are complete?
- Should a future version support dirty worktrees by recording a content digest, or should updates continue to require clean Git revisions permanently?
- Should the server-side `SystemUpdateService` eventually read the local transaction record directly, or remain a projection of CLI-posted outcomes and runtime readback?
