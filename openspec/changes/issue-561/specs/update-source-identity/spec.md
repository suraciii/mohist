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
The command SHALL create an update source context containing the effective normalized repository root and the source revision identity resolved from that root. The context SHALL be passed unchanged to CLI self-update continuation, Server build, Runner build, artifact installation, service activation, and runtime verification. No stage SHALL independently re-resolve a root or compare against the process working directory.

#### Scenario: Server and Runner are built from the same explicit source
- **WHEN** the selected repository root resolves to source revision `target-123`
- **THEN** the Server build receives that root and target identity
- **AND** the Runner build receives that same root and target identity
- **AND** the installed services and final verification use that same target identity

#### Scenario: CLI continuation preserves the selected source
- **WHEN** a full update replaces the CLI and resumes through a continuation process
- **THEN** the continuation receives the original source context
- **AND** it does not fall back to the continuation process working directory or reselect a different repository

### Requirement: Reject an unidentifiable source before changing managed runtime state
The command SHALL fail before changing a managed service target when the effective repository root does not exist, is not a usable repository, or has no resolvable source revision identity. The failure SHALL identify the selected root and the reason that the target identity could not be established.

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
