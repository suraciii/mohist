### Requirement: Build a complete versioned managed runtime release
The full update process SHALL build the CLI, Server, and Runner into a managed runtime location that contains a distinct release for the selected target source identity. A full release SHALL have an identity that can be read without consulting an arbitrary source worktree, and it SHALL not be considered installable until all required CLI, Server, Runner, and identity metadata are present. A component-scoped update MAY stage only its requested component, but its active-set manifest SHALL retain the identities of untouched components and SHALL not claim global consistency.

#### Scenario: CLI, Server, and Runner artifacts are published together
- **WHEN** the selected source revision is `target-123` and all required builds succeed
- **THEN** the process creates one managed release identified by `target-123` or an identity that unambiguously records it
- **AND** the release contains the CLI, Server, and Runner artifacts and their build identity metadata
- **AND** the release is available as one candidate for activation

#### Scenario: Component-scoped candidate preserves untouched components
- **WHEN** a Server-only update builds a candidate for source revision `target-123`
- **THEN** the candidate contains the Server artifact and its identity
- **AND** the active-set manifest retains the previously active Runner and CLI identities
- **AND** the result is reported as Server-scoped rather than as a globally consistent release

#### Scenario: A partial build is not installable
- **WHEN** the Server build succeeds but the Runner build or required identity metadata fails
- **THEN** the incomplete output is not made the active release
- **AND** no managed service is pointed at the incomplete output

### Requirement: Make the managed Runner release self-contained
The candidate Runner release SHALL contain its compiled `dist` files, runtime `package.json`, and every production dependency required by the resolved lockfile. Runtime dependency links SHALL resolve inside the candidate release or the documented immutable managed dependency store; they SHALL not resolve through the source worktree, a workspace `node_modules`, or a developer-only package. `release.json` SHALL record the dependency-lock identity and dependency root used by the Runner target.

#### Scenario: Runner starts without source or workspace dependencies
- **WHEN** the active Runner release is started after the source worktree and its workspace `node_modules` are unavailable
- **THEN** Node resolves every production dependency from the managed Runner release or its recorded immutable dependency store
- **AND** the Runner starts using the artifact-owned runtime identity

#### Scenario: Dependency closure cannot be staged
- **WHEN** a required production dependency is absent or cannot be copied into the managed dependency root
- **THEN** the candidate is incomplete and is not activated
- **AND** the failure identifies the dependency-lock identity and missing dependency

### Requirement: Run managed services from absolute active artifacts
Managed service definitions SHALL start the active installed Server and Runner artifacts using absolute paths within the managed runtime location, either directly through a versioned artifact path or through a stable absolute launcher that resolves the active-set manifest. Service execution SHALL not depend on a relative build output, the process working directory, or an implicit source worktree. Any working directory and environment values required by the service SHALL refer to the selected managed release or its explicit runtime configuration.

#### Scenario: Service units use the installed release
- **WHEN** release `target-123` is activated
- **THEN** the generated or updated Server and Runner service definitions point to stable absolute launchers
- **AND** those launchers resolve the absolute `target-123` artifact paths from the active-set manifest
- **AND** changing the shell working directory or the selected source worktree does not change which artifacts the services start

#### Scenario: Source worktree is unavailable after installation
- **WHEN** a previously installed release is started after its build worktree is moved or unavailable
- **THEN** the managed services can resolve their configured absolute artifact paths
- **AND** service startup does not silently switch to another worktree or relative output

### Requirement: Resolve one deterministic managed runtime root
The managed runtime root SHALL be absolute and deterministic across CLI, service launcher, and recovery processes. The first implementation SHALL use `$HOME/.local/share/mohist/runtime` on Linux and `%LOCALAPPDATA%/Mohist/runtime` on Windows, with tests injecting an equivalent absolute root. The implementation SHALL not depend on a process working directory or an unresolved runtime-root configuration.

#### Scenario: CLI and service launcher share the runtime root
- **WHEN** a Linux or Windows managed update writes a transaction and activates a release
- **THEN** the CLI, stable launchers, and recovery process resolve the same platform-specific absolute runtime root
- **AND** the active record and transaction files are readable by the service account that owns the managed installation

### Requirement: Publish one atomic active target set
The managed runtime SHALL represent the active Server, Runner, and CLI targets with one atomically replaced activation record. Service backends SHALL resolve their component target from that record or its stable managed launcher, so a full candidate activation cannot expose a partially published target set. A component-scoped activation MAY intentionally retain the previous entries for untouched components, but the result SHALL be marked scoped. A failed activation SHALL leave the previous activation record intact until recovery chooses a terminal state.

#### Scenario: Activation cannot publish a partial target set
- **WHEN** writing or activating one component target fails before the complete candidate target set is published
- **THEN** the previous active-set record remains authoritative
- **AND** no service backend starts from a partially published candidate set

#### Scenario: Managed startup resolves the active set
- **WHEN** a managed service starts after its source worktree is unavailable
- **THEN** its stable absolute launcher resolves the active-set record to a versioned managed artifact
- **AND** it does not derive a target from the current process directory or source worktree

### Requirement: Record artifact-owned target identity in every runtime artifact
The CLI, Server, and Runner artifacts SHALL carry the target source revision, release version, and release ID needed for runtime readback. Managed runtime readback SHALL use this artifact-owned metadata as authoritative; service environment values may be used only as equality checks and a mismatch SHALL make the identity unavailable. A managed artifact SHALL not fall back to the machine's current working directory or source HEAD at runtime.

#### Scenario: Runner manifest records the selected source
- **WHEN** the Runner is built from source revision `target-123` with an explicit repository root
- **THEN** its installed build manifest reports `target-123`
- **AND** its artifact version identifies the same release as the installed Server artifact

#### Scenario: Server runtime identity records the selected source
- **WHEN** the Server artifact for source revision `target-123` is started
- **THEN** its runtime identity reports `target-123` and its artifact version
- **AND** those values are available to the update verification operation

#### Scenario: Launcher metadata cannot make an old Server current
- **WHEN** an old Server artifact starts with environment values for a newer candidate release
- **THEN** the Server reports the identity embedded in its artifact
- **AND** the environment/artifact mismatch fails runtime verification rather than reporting the newer candidate identity

#### Scenario: Runner identity is bound to the active connection
- **WHEN** two Runner connections use the same hostname and only one is the active managed Runner connection
- **THEN** `/api/runner/identity` reads the exact active Runner ID and connection generation recorded by the Server
- **AND** a stale connection or self-reported hash cannot satisfy the candidate identity check

### Requirement: Publish releases without overwriting the active release
The update process SHALL write a candidate release separately from the active release and SHALL switch the active service target only after the candidate is complete. A build or installation failure SHALL leave the previous active artifact and service target unchanged.

#### Scenario: Candidate publication fails
- **WHEN** installation of a candidate release fails after the current release is active
- **THEN** the current service target remains configured
- **AND** the current release remains runnable
- **AND** the failed candidate is not reported as active
