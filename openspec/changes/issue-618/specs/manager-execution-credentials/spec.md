### Requirement: Per-execution Manager capability credentials
The system SHALL issue a new short-lived Manager capability credential for every Manager execution. A new execution includes an initial turn, every new follow-up turn, and every recovered or replacement execution. A credential SHALL be bound to the immutable Slack origin, current actor, Enrollment, Session, and an explicit validity window, and SHALL authorize only the Manager CLI capability surface.

#### Scenario: Initial Manager turn receives a credential
- **WHEN** an authorized Manager message starts an Agent execution
- **THEN** the system issues a credential with the current workspace and Slack origin, actor identity, Enrollment identity, Session identity, and expiry, and makes that credential available to that execution

#### Scenario: Follow-up turn receives a fresh credential
- **WHEN** a new Manager follow-up turn is dispatched for an existing Session
- **THEN** the system issues a credential distinct from the prior turn's credential and binds it to the follow-up's current execution context

#### Scenario: Recovered execution receives a fresh credential
- **WHEN** a Manager execution is recovered or replaced after a runtime, Runner, or process interruption
- **THEN** the recovered execution receives a newly issued credential and cannot reuse the interrupted execution's credential

### Requirement: Runtime-only credential handling
The plaintext Manager capability credential SHALL be injected only into the execution environment required by the Manager CLI. It MUST NOT be placed in Agent instructions, prompts, system facts, collaboration Skill content, transcripts, Session inputs or state, AgentJob durable state, Slack inbox or outbox payloads, audit records, logs, error messages, or any other durable or model-visible surface.

#### Scenario: Manager execution is assembled
- **WHEN** the Server builds the prompt, execution envelope, Slack execution context, or Session record for a Manager turn
- **THEN** none of those values contains the plaintext credential or a command argument carrying the credential

#### Scenario: Manager CLI runs inside the execution
- **WHEN** the Agent invokes an allowlisted `mo` capability
- **THEN** the credential is available through the runtime environment or equivalent process-only credential carrier, while the Agent model and command transcript receive no plaintext credential value

#### Scenario: Credential-bearing output is produced
- **WHEN** a command, exception, diagnostic, or runtime log contains a token-like value
- **THEN** the value is redacted before it reaches model output, durable records, Slack delivery payloads, or logs

### Requirement: Per-invocation authorization revalidation
Every Manager CLI invocation SHALL validate the presented credential, its expiry, its origin and Session binding, its actor and Enrollment binding, the requested capability, and the current target authorization before performing any side effect. Authorization SHALL be evaluated against current state rather than only the facts captured when the turn started.

#### Scenario: Current authorized invocation is made
- **WHEN** an unexpired credential is used for an allowlisted operation against a target in the bound workspace and Project that the current actor is authorized to manage
- **THEN** the invocation is authorized and the existing application service performs the operation

#### Scenario: Credential is expired
- **WHEN** a Manager CLI invocation presents a credential at or after its expiry
- **THEN** the invocation is rejected before the management service mutates any resource

#### Scenario: Enrollment state changes
- **WHEN** the Enrollment becomes disabled, removed, not ready, or otherwise loses Manager capability after credential issuance but before a CLI invocation
- **THEN** current authorization rejects the invocation and the target has no side effect

#### Scenario: Actor authorization changes
- **WHEN** the claimed actor is removed, replaced, or no longer authorized after credential issuance
- **THEN** current authorization rejects the invocation even if the credential's validity window has not elapsed

#### Scenario: Target authorization changes
- **WHEN** a Manager invocation targets a deleted, moved, cross-workspace, or otherwise unauthorized Project, Agent, or Slack Connection
- **THEN** the invocation is rejected before mutation and no unrelated target is selected as a fallback

### Requirement: Credential failure is fail-closed
A credential validation or authorization failure SHALL fail closed, expose only a non-secret error class and actionable next step to the Agent, and SHALL NOT retry the management mutation with a broader credential or an unbound target.

#### Scenario: Invalid credential is presented
- **WHEN** a Manager CLI invocation presents an unknown, malformed, replayed, or incorrectly bound credential
- **THEN** the invocation is rejected without side effects and the response contains no credential value or protected secret

#### Scenario: Authorization fails during a mutation
- **WHEN** reauthorization fails immediately before a state-changing CLI operation
- **THEN** the operation is not attempted, the resource remains unchanged, and the Agent receives an authorization result rather than a success result
