## ADDED Requirements

### Requirement: A managed CLI update activates the stable entrypoint

For every managed update scope that includes CLI, the runtime transaction MUST
activate the default managed launcher path, or the caller-supplied explicit CLI
path, before it reports a successful update.

#### Scenario: Existing direct executable migrates to a candidate launcher

- **WHEN** a CLI update targets an existing direct executable
- **THEN** the transaction preserves that executable until commit
- **AND** the stable entrypoint atomically delegates to the candidate CLI
- **AND** successful commit removes the temporary backup

#### Scenario: Repeating the same candidate is idempotent

- **WHEN** the stable launcher already delegates to the same complete runtime
  identity
- **THEN** activation leaves the launcher unchanged
- **AND** no backup is created

### Requirement: Verification proves the stable launcher identity

Before committing a CLI-containing managed update, the updater MUST invoke the
stable launcher and require its version output to contain the candidate source
revision.

#### Scenario: A stale launcher responds successfully

- **WHEN** the stable launcher exits successfully but reports a different
  source revision
- **THEN** verification fails
- **AND** the transaction does not commit the candidate runtime pointer

### Requirement: CLI activation failures recover atomically

When launcher activation, launcher identity verification, or pointer commit
fails, the updater MUST restore the preceding launcher and active target set
before returning failure.

#### Scenario: Candidate verification fails after launcher activation

- **WHEN** the launcher has been switched to a candidate that reports the
  wrong source revision
- **THEN** the preceding direct executable or launcher is restored
- **AND** no candidate verified pointer remains
