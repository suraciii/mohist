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

#### Scenario: Explicit CLI path is the activated entrypoint

- **WHEN** `--cli-path` names an existing absolute direct executable outside
  the default managed path
- **THEN** the transaction replaces that exact path with the candidate launcher
- **AND** identity verification invokes that same exact path
- **AND** successful commit makes the named path run the candidate CLI

#### Scenario: Invalid explicit CLI path fails closed

- **WHEN** `--cli-path` is relative or names a path that does not exist
- **THEN** the managed update fails before candidate staging
- **AND** no runtime pointer or launcher is changed
- **AND** the output identifies the source-checkout bootstrap command

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

### Requirement: First deployment has a reachable bootstrap

The first deployment of this behavior MUST be executable without relying on a
pre-change binary to expose a legacy managed-update path.

#### Scenario: Existing installation bootstraps from the source checkout

- **WHEN** an operator runs `bash scripts/install-mo.sh` from the current source
  checkout
- **THEN** the current CLI is published to the stable user path
- **AND** a subsequent managed `mo update cli` can activate and verify the
  candidate launcher
