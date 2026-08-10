### Requirement: Establish one expected runtime identity
Before activation, the update process SHALL establish an expected identity composed of the selected source revision and the versioned artifacts built from it. The expected identity SHALL remain fixed through activation and verification; verification SHALL not replace it with the current HEAD or another identity read from a different source location.

#### Scenario: Verification uses the selected explicit root
- **WHEN** `--repo-root /workspaces/target` resolves to source revision `target-123`
- **AND** the process working directory resolves to a different revision
- **THEN** the expected runtime identity remains `target-123`
- **AND** all runtime comparisons use `target-123`

### Requirement: Verify every required running component before success
For a full update, the process SHALL read the identity of the executing CLI, the running Server, and the running Runner after activation and SHALL compare each with the expected identity. A component-specific update SHALL apply the same comparison rules to the component it updates and every component that the operation activates or relies on.

#### Scenario: Full update reaches one verified version
- **WHEN** the CLI, Server, and Runner each report the expected source revision and artifact version
- **THEN** the full update is considered runtime-consistent
- **AND** the command may return success

#### Scenario: Component update verifies its affected runtime
- **WHEN** a Server-only update activates a new Server release
- **THEN** the process verifies the running Server against the target identity
- **AND** it verifies any Runner or CLI identity that was restarted, replaced, or required by that operation
- **AND** it does not claim that an unverified affected component is current

### Requirement: Treat missing or mismatched identity as a failed update
The process SHALL fail the update when a required runtime identity is unavailable, ambiguous, or different from the expected source revision or artifact version. Build completion, process startup, service health, or network reconnection alone SHALL not satisfy the identity requirement.

#### Scenario: Server reports a different build identity
- **WHEN** the expected Server identity is `target-123` but the running Server reports `target-122`
- **THEN** runtime verification fails
- **AND** the command does not return a successful update result

#### Scenario: Runner is online without a build identity
- **WHEN** the Runner reconnects and reports online status but does not report a build identity
- **THEN** runtime verification fails as unknown identity
- **AND** online status is not treated as proof that the selected artifact is running

#### Scenario: CLI continuation has the wrong identity
- **WHEN** the continuation CLI is executable but reports an identity different from the expected target
- **THEN** the full update fails
- **AND** it does not report success based only on the Server and Runner identities

### Requirement: Report target and observed identities on verification failure
For every failed consistency check, human-readable output SHALL identify the expected source revision and artifact version, the observed identity for each failed or unavailable component, and the component that caused the failure. The output SHALL distinguish an unavailable identity from a mismatched identity and SHALL not emit the normal success result.

#### Scenario: Identity mismatch is actionable
- **WHEN** the Runner reports `target-122` while the expected identity is `target-123`
- **THEN** error output names the Runner, expected `target-123`, and observed `target-122`
- **AND** the result is a failure rather than a warning-only update

#### Scenario: Identity is unavailable
- **WHEN** the Server cannot provide its runtime identity after activation
- **THEN** error output identifies the Server identity as unavailable and states that runtime consistency could not be confirmed
- **AND** the result does not say that the update is current

### Requirement: Use the same verification contract in dry-run and live updates
Dry-run output SHALL show the target identity and the runtime checks that a live update would require without activating or claiming that any runtime is verified. Live output SHALL report verification only after the actual running processes have been read back.

#### Scenario: Dry run does not claim runtime consistency
- **WHEN** a dry run is requested for source revision `target-123`
- **THEN** it shows `target-123` as the expected target and lists the required component checks
- **AND** it does not report the CLI, Server, or Runner as verified
