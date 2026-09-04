### Requirement: Establish one expected runtime identity
Before activation, the update process SHALL establish an expected identity composed of the selected source revision and the versioned artifacts built from it. The expected identity SHALL remain fixed through activation and verification; verification SHALL not replace it with the current HEAD or another identity read from a different source location.

#### Scenario: Verification uses the selected explicit root
- **WHEN** `--repo-root /workspaces/target` resolves to source revision `target-123`
- **AND** the process working directory resolves to a different revision
- **THEN** the expected runtime identity remains `target-123`
- **AND** all runtime comparisons use `target-123`

### Requirement: Define expected identity fields in the candidate manifest
The candidate `release.json` SHALL define, for each component, `component`, `version`, `sourceRevision`, and `releaseId`. A full release SHALL use one exact `sourceRevision` and `releaseId` for its CLI, Server, and Runner entries; a component-scoped release SHALL compare the activated component with its corresponding entry and retain the untouched active entries for reporting.

#### Scenario: Full candidate has one shared release fact
- **WHEN** a full candidate contains CLI, Server, and Runner entries
- **THEN** all three entries carry the same `sourceRevision` and `releaseId`
- **AND** each component's reported version matches its own manifest entry

#### Scenario: Component candidate has an explicit scope
- **WHEN** a Runner-only candidate changes the Runner entry while Server and CLI remain active from the previous set
- **THEN** the expected identity comparison is limited to the Runner entry and activated dependencies
- **AND** the result records that Server and CLI were untouched rather than treating them as matching the Runner release

### Requirement: Generate one canonical release identity
The update SHALL normalize the resolved Git revision to the lowercase full commit ID with no whitespace. For a full update, `version` SHALL be `0.0.0+<sourceRevision>` and `releaseId` SHALL be `mohist-full-<sourceRevision>` for every CLI, Server, and Runner entry. For a component-scoped update, the activated component SHALL use the same normalized `version` and `releaseId` SHALL be `mohist-<scope>-<sourceRevision>`, where `<scope>` is `cli`, `server`, or `runner`; untouched active entries retain their previous identities. Existing package or assembly version values remain compatibility metadata and SHALL not determine runtime consistency.

#### Scenario: Repeated full builds have the same canonical identity
- **WHEN** two full candidates are built from the same normalized source revision and scope
- **THEN** both candidates use the same `version` and `releaseId`
- **AND** the identity does not depend on the process working directory, build timestamp, or host

#### Scenario: Same version with a different source is not equal
- **WHEN** a runtime reports `version` `0.0.0+<source-122>` but the candidate expects `0.0.0+<source-123>`
- **THEN** the identity comparison fails even if a compatibility package version is the same

### Requirement: Expose a canonical identity from every required component
The installed CLI SHALL expose the canonical runtime identity as one machine-readable JSON object through the internal `mo runtime identity --json` command. Server health/system-info and Runner identity readback SHALL expose the same field names and equality facts from artifact-owned metadata. Server identity SHALL fail when embedded metadata is absent or conflicts with launcher environment values; it SHALL not fall back to source HEAD. Runner identity readback SHALL include the exact `runnerId` and Server-issued `connectionGeneration` used for the active connection. A missing, malformed, stale, or incomplete identity SHALL be reported as unavailable and SHALL fail a required consistency check.

#### Scenario: CLI identity is machine-readable and complete
- **WHEN** the installed CLI belongs to release `target-release` for source revision `target-123`
- **THEN** `mo runtime identity --json` returns `component`, `version`, `sourceRevision`, and `releaseId`
- **AND** the returned `sourceRevision` and `releaseId` exactly match the candidate manifest

#### Scenario: Runner identity from another connection is rejected
- **WHEN** the requested hostname has a stale connection generation whose release ID matches an older candidate
- **THEN** the identity readback is unavailable or mismatched for the active Runner connection
- **AND** the update cannot return success based on the stale connection

#### Scenario: CLI identity cannot be inferred from a generic version string
- **WHEN** the installed CLI returns a version string without a source revision or release ID
- **THEN** the CLI identity check reports unavailable
- **AND** the update cannot return success

### Requirement: Bind Runner identity to an authenticated connection generation
The Server SHALL derive the Runner ID from the authenticated machine credential, not from an untrusted hostname or query parameter. For every accepted Runner connection it SHALL allocate an opaque monotonically increasing `connectionGeneration` and store the tuple `(runnerId, connectionGeneration, connectionId)`. A new connection SHALL invalidate the previous generation, and disconnect SHALL remove the mapping only when both connection ID and generation match. The handshake and identity response SHALL carry the generation. `/api/runner/identity` SHALL require the exact Runner ID and generation selected by the update operation; hostname SHALL be diagnostic only. A response for an invalidated generation SHALL be unavailable and SHALL never be substituted with another connection.

#### Scenario: Same-host stale Runner connection is rejected
- **WHEN** two authenticated Runner connections report the same hostname and the second connection replaces the first generation
- **THEN** identity readback for the first generation is rejected as stale
- **AND** readback for the second generation includes its exact Runner ID and generation

#### Scenario: Reconnect races with identity verification
- **WHEN** a Runner reconnects while an identity request for the previous generation is in flight
- **THEN** the validator rejects the response unless the Runner ID and generation still match the active mapping
- **AND** online status or a matching build hash from the old connection cannot satisfy verification

### Requirement: Define the scope contract for every update command
The update commands SHALL use the following operation contract. A component-scoped command SHALL not claim global consistency for untouched components, even when their existing identities happen to match the candidate source revision.

| Command | Artifacts built | Targets changed | Required identities for success | Rollback scope | Result scope |
| --- | --- | --- | --- | --- | --- |
| `mo update` | CLI, Server, Runner from one snapshot | CLI, Server, Runner active target set | CLI, Server, Runner | Entire previous active target set | Global |
| `mo update cli` | CLI | CLI slot and active CLI entry | CLI | Previous CLI slot and active CLI entry | CLI-scoped |
| `mo update server` | Server | Server target and Server service | Server | Previous Server target and Server service | Server-scoped |
| `mo update runner` | Runner | Runner target and Runner service | Runner | Previous Runner target and Runner service | Runner-scoped |

If a component-scoped operation activates or restarts another component, that component becomes required for the operation and its rollback scope; otherwise untouched components remain the previous active entries and are reported as untouched.

### Requirement: Define the full-update Runner precondition
A full `mo update` SHALL require an existing managed Runner target before it stages or mutates the CLI, Server, or active target. When no Runner service or active Runner target exists, the command SHALL fail closed with `RunnerNotInstalled`, leave all managed targets unchanged, and direct the operator to `mo install runner` before retrying. The update SHALL not silently skip the Runner and SHALL not downgrade a full result to component scope. A stopped but installed Runner MAY be started temporarily for the required identity check; its prior desired stopped state SHALL be restored after commit. An installed but unreachable Runner SHALL fail the transaction and use the normal rollback contract.

#### Scenario: Full update has no Runner target
- **WHEN** `mo update` is invoked without an installed managed Runner target
- **THEN** preflight returns `RunnerNotInstalled` before candidate staging, service stop, CLI replacement, or active-record publication
- **AND** output directs the operator to `mo install runner`

#### Scenario: Full update preserves a stopped Runner
- **WHEN** an installed Runner target is stopped before a full update
- **THEN** the update records the desired stopped state, starts the candidate only as needed for identity verification, and restores the stopped state after commit
- **AND** it does not claim success without first verifying the candidate Runner identity

#### Scenario: Full update cannot reach the installed Runner
- **WHEN** a full update has an installed Runner target but the required generation or identity cannot be read
- **THEN** the update fails as unavailable or stale identity
- **AND** it does not claim global consistency or leave the candidate as the verified release

#### Scenario: Full update claims global consistency only after all checks
- **WHEN** `mo update` activates a candidate built from one source snapshot
- **THEN** CLI, Server, and Runner identities all match the candidate manifest before success
- **AND** the result is global and names all three verified components

#### Scenario: Full update fails on a Runner mismatch
- **WHEN** `mo update` activates a candidate but the Runner reports a different `sourceRevision` or `releaseId`
- **THEN** the full update fails
- **AND** it does not claim global consistency based on the CLI and Server checks alone

#### Scenario: CLI-only update has a narrow success claim
- **WHEN** `mo update cli` activates a new CLI entry and the CLI reports the expected identity
- **THEN** the command may succeed with CLI scope
- **AND** the result reports Server and Runner as untouched rather than verified by this operation

#### Scenario: CLI-only update fails on an unavailable identity
- **WHEN** `mo update cli` cannot read the activated CLI identity
- **THEN** the CLI-scoped update fails
- **AND** it does not claim that the CLI target is current

#### Scenario: Server-only update preserves the existing Runner target
- **WHEN** `mo update server` activates a new Server entry without changing the Runner or CLI entries
- **THEN** the command verifies the Server identity and may succeed with Server scope
- **AND** the result reports the existing Runner and CLI entries as untouched

#### Scenario: Server-only update fails on a Server mismatch
- **WHEN** `mo update server` activates a candidate but the Server reports a different `releaseId`
- **THEN** the Server-scoped update fails
- **AND** the previous Server target is the only target eligible for rollback

#### Scenario: Runner-only update has a narrow success claim
- **WHEN** `mo update runner` activates a new Runner entry and the Runner reports the expected identity
- **THEN** the command may succeed with Runner scope
- **AND** the result reports the existing Server and CLI entries as untouched

#### Scenario: Runner-only update fails on an unavailable identity
- **WHEN** `mo update runner` reconnects successfully but cannot read the Runner identity
- **THEN** the Runner-scoped update fails as unknown identity
- **AND** online status is not treated as proof that the selected Runner artifact is active

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
