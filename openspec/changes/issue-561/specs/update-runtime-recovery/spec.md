### Requirement: Treat activation and verification as one recoverable transaction
An update SHALL track the last verified runtime release and its managed service target before activating a candidate. A candidate SHALL become the new verified release only after all required runtime identity checks pass. Activation, restart, and verification failures SHALL produce a failed transaction rather than a successful update with an unverified runtime.

#### Scenario: Candidate passes verification
- **WHEN** a complete candidate release is activated and every required runtime reports the expected identity
- **THEN** the candidate becomes the last verified release
- **AND** its service target remains active after the transaction completes
- **AND** the update may return success

#### Scenario: Candidate fails verification after activation
- **WHEN** a candidate service starts but a required runtime reports a mismatch or unavailable identity
- **THEN** the candidate is never recorded as the last verified release
- **AND** recovery is started before the update returns
- **AND** the update returns failure without a success result

### Requirement: Persist activation before destructive changes and reconcile crashes
Before any candidate target, service unit, or CLI slot can affect managed runtime execution, the coordinator SHALL atomically persist a transaction record containing the operation scope, candidate active-target set, previous verified active-target set, CLI slot state, desired service states, required identities, continuation claim, activation lease, and current state. For a full update, `CandidateStaged` SHALL not be persisted until the CLI, Server, Runner, web assets, dependency closure, launch metadata, and identity metadata are complete; the CLI slot SHALL not be changed and a service SHALL not be stopped before that point. A component update SHALL apply the same rule to every artifact in its declared scope. The active-target set SHALL be written as one same-directory temporary file followed by a durable atomic replacement; it SHALL contain one generation, one transaction ID, and the matching activation lease for the complete Server, Runner, and CLI set. A transaction SHALL use this write-ahead order: acquire the transaction lock, persist `Prepared`, stage the scoped candidate, persist `CandidateStaged`, persist `ActivationAuthorized` with a live-owner activation lease, publish the complete candidate active-target record, persist `CandidateActivated`, persist `Verifying`, then persist `Committed` only after required identity checks pass. A stable launcher MAY start a target associated with `ActivationAuthorized`, `CandidateActivated`, or `Verifying` only when the active record and transaction record contain the same live activation lease; this is the coordinator's activation handoff, not a second transaction. A launcher with no matching live lease SHALL invoke reconciliation and SHALL not start the candidate. Rollback SHALL persist `RollingBack` before restoring the previous record and SHALL end in `RolledBack`, `NoVerifiedRuntime`, or `RecoveryFailed`. Cancellation before activation SHALL end in `Cancelled` after candidate cleanup; cancellation after activation SHALL persist `Cancelling`, restore and verify the previous scope when possible, and then end in `Cancelled` or `RecoveryFailed`.

The CLI-owned reconciler SHALL run before a new update accepts work and before a managed launcher starts a target associated with a nonterminal transaction that has no live activation lease. It SHALL resolve every nonterminal transaction: clean a `Prepared` transaction that never reached `CandidateStaged`, clean an `ActivationAuthorized` transaction whose active record was never published, leave activation to the live coordinator when the matching lease owner is alive, verify and commit a candidate that is active after taking over a stale lease, restore the previous verified target when verification fails, clean an unapplied candidate when activation never published it, resume `Cancelling` or `RollingBack`, or report `RecoveryFailed` when neither target can be verified. A second update SHALL not proceed while reconciliation is unresolved.

#### Scenario: Crash before candidate activation
- **WHEN** the process dies after `CandidateStaged` is persisted but before the candidate active-target record is published
- **THEN** the previous active-target record remains authoritative
- **AND** the next update or managed startup reconciles the transaction, cleans the unapplied candidate, and records rollback or no-verified-runtime cleanup

#### Scenario: Crash while the candidate is still being staged
- **WHEN** the process dies in `Prepared` while building or copying a candidate and before `CandidateStaged` is persisted
- **THEN** the previous active-target record and CLI slot remain authoritative
- **AND** the next update or managed startup removes the incomplete candidate and records `RolledBack` when a previous verified target exists or `NoVerifiedRuntime` otherwise
- **AND** no service was stopped and no partially built CLI is used for recovery

#### Scenario: Crash after candidate target publication
- **WHEN** the process dies after the candidate active-target record is atomically published but before `CandidateActivated`, `Verifying`, or `Committed` is persisted
- **THEN** the next update or managed startup identifies the transaction from the active record
- **AND** it performs bounded candidate verification before either committing the candidate or restoring the previous verified target
- **AND** it does not treat the candidate as verified merely because the active record was written

#### Scenario: Activation handoff is owned by a live coordinator
- **WHEN** a stable launcher observes a candidate active record in `ActivationAuthorized`, `CandidateActivated`, or `Verifying`
- **AND** the transaction record contains the same target generation and a live activation owner token
- **THEN** the launcher starts the recorded candidate and does not run a competing reconciliation
- **AND** the coordinator retains the transaction lock until verification or rollback reaches a durable terminal state

#### Scenario: Activation handoff transfers after coordinator death
- **WHEN** a stable launcher observes a candidate active record but the matching activation owner token is stale
- **THEN** it invokes the CLI-owned reconciler
- **AND** the reconciler claims the lock, verifies or restores the recorded target set, and releases the stale lease
- **AND** no launcher starts the candidate before that recovery decision

#### Scenario: Crash during partial service or CLI target activation
- **WHEN** activation fails or the process dies after a Server unit, Runner unit, or CLI slot side effect is applied but before the complete target set is committed
- **THEN** the transaction's recorded prior targets and slot are used to restore every changed side effect
- **AND** no mixed target set is reported as active
- **AND** recovery verifies the restored scope before reporting rollback success

#### Scenario: Managed startup encounters an unresolved transaction
- **WHEN** a stable managed launcher observes an active-target record associated with a nonterminal transaction
- **THEN** it checks the matching activation lease and its owner process-start token
- **AND** it starts the candidate only for a live coordinator-owned activation handoff
- **AND** otherwise it invokes the CLI-owned reconciler before starting any target, or refuses the start and reports no verified runtime when reconciliation fails

### Requirement: Restore the last verified runtime after a candidate failure
When a last verified release exists, a failed candidate transaction SHALL restore the previous release as the active service target, restart any affected managed service as needed, and verify that the restored runtime identity is the recorded last verified identity. Recovery SHALL not silently leave the candidate active.

#### Scenario: Previous verified release is restored
- **WHEN** release `target-123` is active and verified, candidate `target-124` is activated, and candidate verification fails
- **THEN** the service target is restored to `target-123`
- **AND** the managed runtime is restarted or left running according to the restored target
- **AND** recovery verifies `target-123` before reporting that the previous runtime was restored

#### Scenario: Restored runtime cannot be verified
- **WHEN** candidate verification fails and the previous release cannot be started or its identity cannot be read
- **THEN** the update remains failed
- **AND** candidate services are stopped or disabled
- **AND** `active.json` is atomically replaced with `status: "none"` and no target set while the transaction and candidate paths are retained for diagnosis
- **AND** output reports that recovery itself could not verify the previous release
- **AND** no success result is emitted

### Requirement: Remove an unverified candidate when no verified release exists
If no last verified release exists, a failed candidate SHALL be stopped or prevented from starting and its active service target SHALL be removed or disabled. The system SHALL not leave an unverified candidate as the managed runtime.

#### Scenario: First installation fails verification
- **WHEN** no verified release exists and the first candidate starts but fails identity verification
- **THEN** the candidate service is stopped or prevented from remaining active
- **AND** the candidate is removed from the active managed target
- **AND** `active.json` has `status: "none"` and no target set
- **AND** the command reports that no verified runtime is available

#### Scenario: First installation fails before activation
- **WHEN** the first candidate fails during build or installation before activation
- **THEN** no managed service target is created for that candidate
- **AND** the system remains without a claimed verified runtime

### Requirement: Preserve recovery state across CLI self-update continuation
If the CLI is replaced before the Server and Runner transaction completes, the continuation process SHALL receive the candidate identity, previous verified release, service target, and recovery state. A continuation failure SHALL use that state to complete recovery instead of abandoning an active candidate.

#### Scenario: CLI continuation fails after candidate activation
- **WHEN** the CLI self-update succeeds, the continuation activates a candidate, and a later verification step fails
- **THEN** the continuation restores the previous verified service target when one exists
- **AND** it reports the candidate failure and recovery outcome
- **AND** it does not exit with a success result merely because the CLI replacement succeeded

### Requirement: Bootstrap legacy CLI installations before managed self-update
An installed CLI that cannot read or write the managed transaction record SHALL return `bootstrap_required` before replacing its executable, stopping a service, or changing `active.json`. The bootstrap instruction SHALL identify the supported current CLI installation operation. The bootstrap operation SHALL install an always-available `cli/bootstrap/mo` helper outside both update slots, the stable launcher, recovery CLI slot, fixed runtime-root metadata, and transaction schema without claiming the legacy source-bound runtime as verified. A managed self-update SHALL begin only after the new CLI persists `Prepared` with a recovery path that remains runnable if the candidate slot is invalid. The bootstrap helper SHALL be installed with the initial CLI distribution, SHALL never be replaced by candidate activation, and SHALL implement only the restricted `install cli` repair operation.

#### Scenario: Legacy CLI fails closed before mutation
- **WHEN** a pre-transaction CLI invokes `mo update`
- **THEN** it reports `bootstrap_required` and the supported bootstrap action
- **AND** it does not replace the CLI, stop or restart a service, or modify `active.json`

#### Scenario: Candidate CLI crash uses the recovery slot
- **WHEN** a managed transaction crashes before or after replacing the candidate CLI slot
- **THEN** the stable launcher invokes the recorded recovery CLI slot rather than the candidate slot
- **AND** reconciliation restores or quarantines the target set without recursively executing the unverified candidate

#### Scenario: Legacy bootstrap has a runnable repair path
- **WHEN** the active CLI reports `bootstrap_required` or no trusted recovery CLI slot is available
- **THEN** the stable wrapper dispatches only the exact `install cli` command to `cli/bootstrap/mo`
- **AND** every other command returns `bootstrap_required` without executing the candidate slot
- **AND** the bootstrap helper recreates the recovery slot, stable launchers, and runtime-root metadata before another update

### Requirement: Make cancellation a durable transaction outcome
Cancellation SHALL be recorded as a state-machine event rather than only as a process interruption. Before activation, cancellation SHALL clean the candidate and persist `Cancelled` while leaving the previous active target and CLI slot unchanged. After activation or any service/CLI target effect, cancellation SHALL persist `Cancelling`, restore and verify the previous scope when possible, persist `Cancelled`, return exit code 130, release the lock only after the terminal record is durable, and project the terminal cancellation outcome. If restoration cannot be verified, cancellation SHALL stop or disable the candidate, clear the active target when required, preserve diagnostic paths, and end in `RecoveryFailed` without success. A `Cancelling` record SHALL be reconciled before any new update or managed launch.

#### Scenario: Cancellation before candidate activation
- **WHEN** cancellation arrives after `Prepared` or `CandidateStaged` but before `active.json` publishes the candidate
- **THEN** the candidate is cleaned without changing the previous active target or CLI slot
- **AND** the transaction becomes `Cancelled`, releases its lock after durable persistence, and returns exit code 130

#### Scenario: Cancellation after target publication
- **WHEN** cancellation arrives after the candidate active record or a service effect has been applied
- **THEN** the transaction first persists `Cancelling`
- **AND** it restores and verifies the previous target and desired service states before persisting `Cancelled`
- **AND** it does not report success or leave a nonterminal record

#### Scenario: Cancellation after CLI replacement
- **WHEN** cancellation arrives after the candidate CLI slot has been selected and before runtime verification completes
- **THEN** the recovery CLI slot performs reconciliation rather than the candidate slot
- **AND** the previous verified CLI slot is selected when restoration succeeds, or the trusted recovery slot is selected when no verified runtime remains

#### Scenario: Cancellation rollback fails
- **WHEN** cancellation arrives after activation and the previous target cannot be verified
- **THEN** candidate services are stopped or disabled and `active.json` is set to `status: "none"` when no target is verified
- **AND** the transaction ends in `RecoveryFailed` with the cancellation and recovery facts

### Requirement: Keep a trusted CLI executable for every terminal recovery state
The selected CLI slot SHALL be part of `active.json` and the stable `launchers/cli` wrapper SHALL resolve only that record for normal commands. `Committed` and `RolledBack` SHALL select a verified release CLI slot. `NoVerifiedRuntime`, `Cancelled` after candidate activation, and `RecoveryFailed` SHALL set the Server and Runner target set to none while selecting the last trusted recovery CLI slot. If no trusted recovery slot exists, the wrapper SHALL dispatch only `install cli` to the always-available `cli/bootstrap/mo` helper and SHALL report `bootstrap_required` for every other command; it SHALL never execute the candidate slot as a general CLI. The supported manual repair command SHALL be `mo install cli`; it SHALL recreate the bootstrap helper, recovery slot, stable launchers, and runtime-root metadata before another `mo update`.

#### Scenario: Rollback selects the verified CLI slot
- **WHEN** a candidate fails after the candidate CLI was selected and the previous verified release is restored
- **THEN** `active.json` selects the previous verified CLI slot
- **AND** the stable wrapper executes that slot rather than the failed candidate

#### Scenario: No verified runtime keeps only recovery CLI access
- **WHEN** there is no verified release or restoration fails
- **THEN** Server and Runner targets are stopped or disabled and `active.json` has no service target
- **AND** the stable CLI wrapper selects the trusted recovery slot
- **AND** the output identifies `mo install cli` as the manual repair action

#### Scenario: Recovery slot is unavailable
- **WHEN** rollback or recovery fails and no trusted recovery CLI slot is runnable
- **THEN** the stable wrapper executes only the restricted bootstrap helper for `mo install cli`
- **AND** it refuses every other command and returns `bootstrap_required` with no normal update success result

#### Scenario: Bootstrap helper is unavailable
- **WHEN** no trusted recovery slot and no `cli/bootstrap/mo` helper are present
- **THEN** the stable wrapper reports installation corruption and does not execute the candidate slot
- **AND** operator guidance directs reinstall of the original Mohist CLI distribution before rerunning `mo install cli`

### Requirement: Make transaction persistence and locking crash-durable
The transaction lock SHALL be an OS-backed exclusive file containing a schema version, transaction ID, owner process ID, owner process-start token, creation time, and phase (`acquiring`, `prepared`, or `terminal`). File contents and the containing directory SHALL be flushed before the lock acquisition call returns. A process-local semaphore SHALL be only an optimization and SHALL not establish ownership. A lock SHALL be considered stale only when its owner process-start token is not live or its transaction is terminal; an ambiguous owner SHALL fail closed. If an `acquiring` lock has no transaction record, the reconciler SHALL first prove that the owner token is stale, persist an `OrphanedLock` record with the current active target snapshot, and persist `RolledBack` when a previous verified target exists or `NoVerifiedRuntime` otherwise. It SHALL remove the orphan lock only after the diagnostic and terminal records are durable; a record-write failure SHALL leave the lock for retry. The reconciler SHALL resolve the transaction before removing any other stale lock. The lock SHALL remain held until a terminal transaction state is durable; a release failure SHALL persist `lockReleasePending` and SHALL be retried before the next update.

Transaction records and `active.json` SHALL be written through an injected atomic-file boundary that flushes file contents, replaces only within the same directory, and flushes the directory entry after replacement. The Linux implementation SHALL use a write-through file flush and directory flush; the Windows implementation SHALL use the platform replace operation with write-through semantics. The write order SHALL be: lock with `acquiring` phase, `Prepared`, candidate files and manifest, `CandidateStaged`, `ActivationAuthorized` with a live activation lease, active record with the same lease, `CandidateActivated`, service/launcher effects, `Verifying`, terminal state, and lock release. Stable launchers SHALL start a candidate during an unresolved transaction only for the matching live activation lease; all other unresolved transactions SHALL be reconciled before launch.

#### Scenario: Crash leaves a lock file
- **WHEN** the process dies after acquiring the transaction lock or after a terminal state is persisted but before lock release
- **THEN** startup reads the lock owner and transaction record
- **AND** it removes the lock only when the owner is stale or the transaction is terminal
- **AND** a new update is rejected until reconciliation completes

#### Scenario: Crash before `Prepared` leaves only an acquisition lock
- **WHEN** the process dies after creating and flushing an `acquiring` lock but before a transaction record is durable
- **THEN** startup proves whether the recorded owner process-start token is stale
- **AND** for a stale owner it persists an `OrphanedLock` diagnostic record and then `RolledBack` when the current active target is verified or `NoVerifiedRuntime` otherwise
- **AND** it removes the lock only after those records are durable
- **AND** for a live or ambiguous owner it leaves the lock in place and rejects new work

#### Scenario: Atomic replacement fails
- **WHEN** a state or active-record replacement cannot complete or its durability boundary fails
- **THEN** the previous complete record remains authoritative
- **AND** the transaction remains nonterminal until reconciliation either restores it or reports `RecoveryFailed`

### Requirement: Keep managed-runtime mutation in the CLI transaction
The Server web-update service SHALL not build artifacts, change service targets, replace a CLI slot, start/stop a managed service, or advance a persisted web job. Persisted update state SHALL have schema version 2 and record an `owner` of `web` or `cli`, a scope, source mode, target identity, sequence, and `lockReleasePending`; records without an owner SHALL be classified as legacy `web` records before they are migrated. `rejected` SHALL be a terminal status reserved for quarantined web jobs. `POST /api/system/update` SHALL reject direct mutation with HTTP `409 Conflict` and a machine-readable `UpdateMutationOwnedByCli` outcome that directs the caller to the local CLI transaction. `GET /api/system/update/status` and `GET /api/system/consistency` SHALL be side-effect-free projections; they SHALL not resume stale web jobs or create a successful update result without the CLI-owned candidate activation and required identity verification. Persisted `running` or `waiting-for-reconnect` web jobs SHALL be marked rejected on Server startup and SHALL not be resumed.

CLI outcome projection SHALL require an existing local CLI transaction and its durable `projection-lease.json`. The lease SHALL bind the job ID, nonce hash, operation scope, source mode, target identity, and last accepted sequence. Each outcome request SHALL include the job ID, nonce, sequence, status, stage, outcome, scope, source mode, and target facts. The Server SHALL reject an unknown job, missing or mismatched lease, changed target facts, invalid transition, or non-increasing sequence. Duplicate delivery of an identical sequence and payload SHALL be idempotent. A successful projection SHALL require the matching local transaction record to be durably `Committed`; the outcome endpoint SHALL never create a successful projection from an arbitrary POST or from Server-local source/runtime facts.

#### Scenario: Web update mutation is rejected
- **WHEN** a caller invokes `POST /api/system/update`
- **THEN** the Server returns HTTP `409 Conflict` with `UpdateMutationOwnedByCli`
- **AND** no build, service restart, active-target change, or CLI-slot change occurs

#### Scenario: Web status does not advance a stale job
- **WHEN** a caller reads `/api/system/update/status` while a persisted web job is `running` or `waiting-for-reconnect`
- **THEN** the Server returns the rejected/projection state
- **AND** it does not build, restart, acquire an update lock, or mark the job successful

#### Scenario: Server startup quarantines a legacy web job
- **WHEN** the Server starts with a nonterminal web-owned update job
- **THEN** the registered `SystemUpdateStartupReconciler` migrates the record to schema version 2 and durably persists `owner: "web"`, `status: "rejected"`, `stage: "Rejected"`, and reason `UpdateMutationOwnedByCli`
- **AND** it releases the web lock only after the rejected state is durable
- **AND** if release fails, it persists `lockReleasePending: true` and retries on the next startup
- **AND** no background update task is resumed

#### Scenario: CLI outcome requires an owned lease
- **WHEN** the CLI posts a prepared or later outcome with a valid local transaction, matching lease, target facts, and a greater sequence
- **THEN** the Server persists an `owner: "cli"` projection without building or activating anything
- **AND** the projection preserves the scope, source mode, target identity, sequence, and recovery facts

#### Scenario: Unknown or stale outcome is rejected
- **WHEN** an outcome has no local transaction, no matching lease, a changed target, an old sequence, or a different nonce
- **THEN** the Server rejects it without changing the latest projection or lock
- **AND** it cannot create a successful update result

#### Scenario: Web status projects a CLI transaction
- **WHEN** the CLI owns an update transaction and the Server status surface is queried
- **THEN** the response projects the CLI transaction state, target identity, observed identities, and recovery result
- **AND** the status surface does not independently mark the update successful

### Requirement: Emit actionable recovery results
Every failed transaction SHALL report the failed stage, the expected target identity, the observed identity or failure reason, and the recovery outcome. The recovery outcome SHALL state whether the last verified release was restored, no verified runtime remains, or recovery failed, and SHALL include an actionable next operation when manual intervention is required.

#### Scenario: Rollback succeeds
- **WHEN** candidate verification fails but the previous verified release is restored and verified
- **THEN** error output identifies the expected candidate identity and the observed failure
- **AND** it states that the previous verified release was restored
- **AND** it provides the failed update result without a normal success message

#### Scenario: Rollback is unavailable
- **WHEN** candidate verification fails and there is no verified release to restore
- **THEN** error output states that no verified runtime is available
- **AND** it identifies the candidate and observed failure
- **AND** it provides the next recovery action needed to make a verified runtime available
