### Requirement: Credential rotation is a synchronous verified operation

Rotating Slack credentials SHALL submit new App and Bot tokens and immediately re-run Slack verification (workspace, App, and Bot consistency plus required scopes) before the rotation is accepted. Rotation SHALL NOT defer verification to a later adapter heartbeat and SHALL NOT silently overwrite stored secrets without confirming the new credentials are valid.

#### Scenario: Valid rotation re-verifies and stores new credentials
- **WHEN** an operator submits new App and Bot tokens for a Connection whose existing credentials are already verified
- **THEN** Mohist synchronously verifies the new tokens against Slack, and only after verification succeeds stores the new credentials and clears any prior credential-failure health reason

#### Scenario: Rotation with invalid tokens is rejected
- **WHEN** an operator submits tokens that Slack rejects, that lack a required scope, or whose App and Bot do not belong to the same install
- **THEN** Mohist rejects the rotation, reports the concrete failure reason, and the previously stored credentials remain unchanged and in force

### Requirement: Rotation rejects credentials that resolve to a different binding

Credential rotation SHALL reject new tokens whose resolved workspace, App, or Bot identity differs from the Connection's existing bound identity. Rotation SHALL NOT rebind the Connection to a different workspace, App, or Bot; changing any of these bindings MUST be expressed as creating a new Connection, not as rotating credentials on the existing one.

#### Scenario: Same workspace, App, and Bot accepted
- **WHEN** the new tokens resolve to the same workspace team id, App id, and Bot user id as the existing binding
- **THEN** the rotation is accepted and the binding is unchanged

#### Scenario: Different workspace, App, or Bot rejected
- **WHEN** the new tokens resolve to a different workspace, a different App, or a different Bot than the existing binding
- **THEN** the rotation is rejected with an actionable reason, the existing credentials and binding remain unchanged, and no partial rotation is persisted

### Requirement: Rotation preserves the Owner and accepted work

A successful credential rotation SHALL NOT reset the established Owner, the Setup progress, any accepted AgentJob, AgentSession, SessionInput, AgentTurn, conversation mapping, or pending outbound delivery. Rotation touches only the stored credentials and the health reason derived from verification.

#### Scenario: Owner survives rotation
- **WHEN** credentials are rotated on a Connection that has a claimed Owner and accepted work
- **THEN** the Owner remains the same, accepted work is preserved, and the Connection does not regress to an earlier setup step

### Requirement: Rotation failure rolls back to the original credentials

When a rotation fails verification or is rejected for an identity mismatch, Mohist SHALL leave the previously stored credentials in place and the Connection SHALL continue operating on the prior credentials without interruption.

#### Scenario: Rollback on verification failure
- **WHEN** rotation verification fails because Slack is unreachable or the tokens are invalid
- **THEN** the original credentials remain stored and in force, the Connection health reflects the original state, and the operator is told to retry

### Requirement: Rotation credentials enter only through protected channels

Credential rotation SHALL accept App and Bot tokens only through hidden terminal input or a `--credentials-file` pointing at a UTF-8 JSON document containing exactly `appToken` and `botToken`. The credential file MUST be a regular non-symlink file readable and writable only by the current user. Tokens MUST NOT be accepted as command-line arguments and MUST NOT appear in command echo, logs, or transcripts.

#### Scenario: Rotation reads from a protected file
- **WHEN** an operator runs `mo agent connection rotate-credentials <id> --credentials-file <path>` with a `chmod 600` regular file
- **THEN** the tokens are read, rotated, and never printed to the terminal or written into Agent configuration

#### Scenario: Command-line tokens are refused
- **WHEN** a token value is passed directly as a command argument to rotate-credentials
- **THEN** the command rejects the invocation and stores no credentials

### Requirement: The CLI exposes credential rotation

The CLI SHALL provide `mo agent connection rotate-credentials <connection-id>` that submits new credentials through the protected channel and reports the verification result.

#### Scenario: Rotating credentials from the CLI
- **WHEN** an operator runs `mo agent connection rotate-credentials <id> --credentials-file <path>` with valid same-binding tokens
- **THEN** the command reports success and the updated Connection health; on rejection it reports the concrete failure reason and a non-zero exit code
