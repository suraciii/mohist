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
Before any candidate target, service unit, or CLI slot can affect managed runtime execution, the coordinator SHALL atomically persist a transaction record containing the operation scope, candidate active-target set, previous verified active-target set, CLI slot state, required identities, and current state. The active-target set SHALL be written as one temporary file followed by an atomic rename; it SHALL contain one generation and one transaction ID for the complete Server, Runner, and CLI set. A transaction SHALL use this write-ahead order: persist `CandidateStaged`, publish the complete candidate active-target record, persist `CandidateActivated`, persist `Verifying`, then persist `Committed` only after required identity checks pass. Rollback SHALL persist `RollingBack` before restoring the previous record and SHALL end in `RolledBack`, `NoVerifiedRuntime`, or `RecoveryFailed`.

The CLI-owned reconciler SHALL run before a new update accepts work and before a managed launcher starts a target associated with a nonterminal transaction. It SHALL resolve every nonterminal transaction to a terminal state: verify and commit a candidate that is active and passes bounded checks, restore the previous verified target when verification fails, clean an unapplied candidate when activation never published it, or report `RecoveryFailed` when neither target can be verified. A second update SHALL not proceed while reconciliation is unresolved.

#### Scenario: Crash before candidate activation
- **WHEN** the process dies after `CandidateStaged` is persisted but before the candidate active-target record is published
- **THEN** the previous active-target record remains authoritative
- **AND** the next update or managed startup reconciles the transaction, cleans the unapplied candidate, and records rollback or no-verified-runtime cleanup

#### Scenario: Crash after candidate target publication
- **WHEN** the process dies after the candidate active-target record is atomically published but before `CandidateActivated`, `Verifying`, or `Committed` is persisted
- **THEN** the next update or managed startup identifies the transaction from the active record
- **AND** it performs bounded candidate verification before either committing the candidate or restoring the previous verified target
- **AND** it does not treat the candidate as verified merely because the active record was written

#### Scenario: Crash during partial service or CLI target activation
- **WHEN** activation fails or the process dies after a Server unit, Runner unit, or CLI slot side effect is applied but before the complete target set is committed
- **THEN** the transaction's recorded prior targets and slot are used to restore every changed side effect
- **AND** no mixed target set is reported as active
- **AND** recovery verifies the restored scope before reporting rollback success

#### Scenario: Managed startup encounters an unresolved transaction
- **WHEN** a stable managed launcher observes an active-target record associated with a nonterminal transaction
- **THEN** it invokes the CLI-owned reconciler before starting that candidate
- **AND** it starts only the reconciled verified target, or refuses the start and reports no verified runtime when reconciliation fails

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
- **AND** output reports that recovery itself could not verify the previous release
- **AND** no success result is emitted

### Requirement: Remove an unverified candidate when no verified release exists
If no last verified release exists, a failed candidate SHALL be stopped or prevented from starting and its active service target SHALL be removed or disabled. The system SHALL not leave an unverified candidate as the managed runtime.

#### Scenario: First installation fails verification
- **WHEN** no verified release exists and the first candidate starts but fails identity verification
- **THEN** the candidate service is stopped or prevented from remaining active
- **AND** the candidate is removed from the active managed target
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

### Requirement: Keep managed-runtime mutation in the CLI transaction
The Server web-update mutation endpoint SHALL not build artifacts, change service targets, replace a CLI slot, or start/stop a managed service. `POST /api/system/update` SHALL reject direct mutation with HTTP `409 Conflict` and a machine-readable `UpdateMutationOwnedByCli` outcome that directs the caller to the local CLI transaction. Server status and outcome read surfaces MAY project the CLI transaction, but they SHALL not create a successful update result without the CLI-owned candidate activation and required identity verification.

#### Scenario: Web update mutation is rejected
- **WHEN** a caller invokes `POST /api/system/update`
- **THEN** the Server returns HTTP `409 Conflict` with `UpdateMutationOwnedByCli`
- **AND** no build, service restart, active-target change, or CLI-slot change occurs

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
