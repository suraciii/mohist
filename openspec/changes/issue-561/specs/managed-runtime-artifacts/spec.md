### Requirement: Build a complete versioned managed runtime release
The update process SHALL build the Server and Runner into a managed runtime location that contains a distinct release for the selected target source identity. A release SHALL have an identity that can be read without consulting an arbitrary source worktree, and it SHALL not be considered installable until all required Server, Runner, and identity metadata are present.

#### Scenario: Server and Runner artifacts are published together
- **WHEN** the selected source revision is `target-123` and both builds succeed
- **THEN** the process creates one managed release identified by `target-123` or an identity that unambiguously records it
- **AND** the release contains the Server artifact, Runner artifact, and their build identity metadata
- **AND** the release is available as one candidate for activation

#### Scenario: A partial build is not installable
- **WHEN** the Server build succeeds but the Runner build or required identity metadata fails
- **THEN** the incomplete output is not made the active release
- **AND** no managed service is pointed at the incomplete output

### Requirement: Run managed services from absolute active artifacts
Managed service definitions SHALL start the active installed Server and Runner artifacts using absolute paths within the managed runtime location. Service execution SHALL not depend on a relative build output, the process working directory, or an implicit source worktree. Any working directory and environment values required by the service SHALL refer to the selected managed release or its explicit runtime configuration.

#### Scenario: Service units use the installed release
- **WHEN** release `target-123` is activated
- **THEN** the generated or updated Server and Runner service definitions point to the absolute `target-123` artifact paths
- **AND** changing the shell working directory or the selected source worktree does not change which artifacts the services start

#### Scenario: Source worktree is unavailable after installation
- **WHEN** a previously installed release is started after its build worktree is moved or unavailable
- **THEN** the managed services can resolve their configured absolute artifact paths
- **AND** service startup does not silently switch to another worktree or relative output

### Requirement: Record target identity in every runtime artifact
The Server and Runner artifacts SHALL carry the target source revision and artifact version needed for runtime readback. The recorded identity SHALL be produced from the authoritative update source context, not from the machine's current working directory at the end of the build.

#### Scenario: Runner manifest records the selected source
- **WHEN** the Runner is built from source revision `target-123` with an explicit repository root
- **THEN** its installed build manifest reports `target-123`
- **AND** its artifact version identifies the same release as the installed Server artifact

#### Scenario: Server runtime identity records the selected source
- **WHEN** the Server artifact for source revision `target-123` is started
- **THEN** its runtime identity reports `target-123` and its artifact version
- **AND** those values are available to the update verification operation

### Requirement: Publish releases without overwriting the active release
The update process SHALL write a candidate release separately from the active release and SHALL switch the active service target only after the candidate is complete. A build or installation failure SHALL leave the previous active artifact and service target unchanged.

#### Scenario: Candidate publication fails
- **WHEN** installation of a candidate release fails after the current release is active
- **THEN** the current service target remains configured
- **AND** the current release remains runnable
- **AND** the failed candidate is not reported as active
