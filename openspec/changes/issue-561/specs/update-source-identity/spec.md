### Requirement: Select one authoritative update source
The update command SHALL resolve one effective repository root before it performs any build, self-update continuation, artifact installation, service activation, or runtime verification. When `--repo-root` is supplied, its normalized absolute path SHALL be the effective root. When it is omitted, the command SHALL resolve the default root once and record that the default was used.

#### Scenario: Explicit repository root is used from another working directory
- **WHEN** the command is invoked with `--repo-root /workspaces/target` while its process working directory is `/workspaces/other`
- **THEN** the effective source root is `/workspaces/target`
- **AND** no update stage uses `/workspaces/other` as a source root

#### Scenario: Default repository root is selected
- **WHEN** the command is invoked without `--repo-root`
- **THEN** the command resolves its default repository root before starting the update
- **AND** the resolved root is recorded as a default-root update rather than being indistinguishable from an explicit-root update

### Requirement: Carry an immutable source identity through the update
The command SHALL create an update source context containing the effective normalized repository root, the source revision identity resolved from that root, the immutable `SnapshotRoot` used as read-only source input, and transaction-owned writable `BuildWorkspaceRoot` and `CandidateRoot` paths. The context SHALL be passed unchanged to CLI self-update continuation, Server build, Runner build, artifact installation, service activation, and runtime verification. No stage SHALL independently re-resolve a root or compare against the process working directory.

#### Scenario: Server and Runner are built from the same explicit source
- **WHEN** the selected repository root resolves to source revision `target-123`
- **THEN** the Server build reads source files from the same `SnapshotRoot` and receives the target identity
- **AND** the Runner build reads source files from that same `SnapshotRoot` and receives the target identity
- **AND** the installed services and final verification use that same target identity

### Requirement: Build from an immutable source snapshot
After resolving a usable clean source revision, the command SHALL materialize a read-only build snapshot of that revision before any artifact build or service change. Build commands SHALL read source files only from `SnapshotRoot` and SHALL write compiler intermediates, generated metadata, web output, and candidate artifacts only under `BuildWorkspaceRoot` or `CandidateRoot`; no build command may write to `SnapshotRoot`. The requested repository root remains the reported source authority but changes to it after snapshot creation SHALL not change the candidate artifacts. Snapshot creation or writable build-root preparation failure SHALL stop the update before managed runtime state is changed.

#### Scenario: Selected worktree changes after source resolution
- **WHEN** the selected repository resolves to `target-123` and the original worktree is modified or advances after the snapshot is materialized
- **THEN** Server, Runner, and CLI artifacts are built from the snapshot for `target-123`
- **AND** the later worktree change does not change the candidate identity or its contents

#### Scenario: Build output does not mutate the source snapshot
- **WHEN** Server, web, Runner, and CLI builds run for source revision `target-123`
- **THEN** all compiler intermediates, generated identity files, web output, and Runner build metadata are written below the transaction build or candidate roots
- **AND** the snapshot revision marker and source-file digest remain unchanged
- **AND** the candidate records both the snapshot revision and its writable output roots

#### Scenario: Snapshot creation fails
- **WHEN** the selected revision is readable but the immutable build snapshot cannot be materialized
- **THEN** the command exits unsuccessfully before stopping, replacing, or activating a managed service
- **AND** the failure identifies the selected root, target revision, and snapshot failure

#### Scenario: CLI continuation preserves the selected source
- **WHEN** a full update replaces the CLI and resumes through a continuation process
- **THEN** the continuation receives the original source context
- **AND** it does not fall back to the continuation process working directory or reselect a different repository

### Requirement: Reject an unidentifiable source before changing managed runtime state
The command SHALL fail before changing a managed service target when the effective repository root does not exist, is not a usable repository, has no resolvable source revision identity, or cannot produce the required immutable snapshot. The failure SHALL identify the selected root and the reason that the target identity or snapshot could not be established.

#### Scenario: Explicit root is missing
- **WHEN** `--repo-root /workspaces/missing` is supplied and that path does not exist
- **THEN** the command exits unsuccessfully before stopping or replacing a managed service
- **AND** the error identifies `/workspaces/missing` as the selected root

#### Scenario: Source revision cannot be resolved
- **WHEN** the selected root exists but its source revision cannot be read
- **THEN** the command exits unsuccessfully before artifact activation
- **AND** it does not claim that the update target is current

### Requirement: Make source selection visible in previews and results
Human-readable update output and dry-run output SHALL identify whether the source was explicit or default, show the effective repository root, and show the target source revision when it is available. Results that include observed runtime identity SHALL distinguish the target identity from the observed identity.

#### Scenario: Explicit-root dry run is distinguishable
- **WHEN** a dry run is invoked with `--repo-root /workspaces/target` and source revision `target-123`
- **THEN** the preview identifies the update as explicit-root
- **AND** it shows `/workspaces/target` and `target-123` as the planned source
- **AND** it does not present a default-root result

#### Scenario: Default-root result includes the selected source
- **WHEN** an update without `--repo-root` resolves `/workspaces/default` at source revision `default-456`
- **THEN** the result identifies the update as default-root
- **AND** it shows `/workspaces/default` and `default-456` as the target source
