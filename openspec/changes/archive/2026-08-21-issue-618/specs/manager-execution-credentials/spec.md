### Requirement: Per-execution Manager capability credentials
The system SHALL issue two new short-lived Manager execution credentials for every Manager execution: one management credential and one reply-delivery credential. A new execution includes an initial turn, every new follow-up turn, and every recovered, replacement, or expiry-recovery execution. Each credential SHALL be bound to the immutable Slack origin, current actor, Enrollment, Session, execution identity, deployment epoch, and an explicit validity window. The management credential SHALL authorize only the Manager CLI capability surface, and the reply credential SHALL authorize only the anchored Manager reply route.

#### Scenario: Initial Manager turn receives a credential
- **WHEN** an authorized Manager message starts an Agent execution
- **THEN** the system issues distinct management and reply credentials with the current workspace and Slack origin, actor identity, Enrollment identity, Session identity, and expiry, and makes them available only to their corresponding scoped CLI routes

#### Scenario: Follow-up turn receives a fresh credential
- **WHEN** a new Manager follow-up turn is dispatched for an existing Session
- **THEN** the system issues distinct management and reply credentials, both different from the prior turn's values, and binds them to the follow-up's current execution context

#### Scenario: Recovered execution receives a fresh credential
- **WHEN** a Manager execution is recovered or replaced after a runtime, Runner, or process interruption
- **THEN** the recovered execution receives newly issued management and reply credentials and cannot reuse either interrupted credential

#### Scenario: Server restart invalidates a live execution
- **WHEN** a Server process restarts gracefully or after a crash while a Manager execution has a live grant
- **THEN** the new Server deployment epoch rejects both old credentials, connected or subsequently polling Runners destroy the old grant, broker, and launcher, and durable recovery starts at most one replacement execution from the immutable Slack origin with fresh credentials

### Requirement: Restart and expiry recovery
The system SHALL use one authoritative Server restart boundary for every deployment topology. Every Server process start advances the shared Manager deployment epoch before accepting Manager polls; a graceful shutdown revokes active leases, and an ungraceful restart invalidates them through the new epoch. Lease-store loss, epoch disagreement, or inability to notify or reconcile a Runner SHALL fail closed. Credential expiry SHALL close the current execution and trigger one durable recovery/new-turn transition that reauthorizes from the immutable Slack origin and receives new credentials; it SHALL never renew a live lease in place.

#### Scenario: Graceful Server shutdown closes Manager work
- **WHEN** the Server begins a graceful restart with Manager executions in flight
- **THEN** it stops accepting new Manager claims, revokes both leases for each active execution, closes or instructs the Runner to close each broker boundary, and records each execution for recovery without reusing its grant

#### Scenario: Crash recovery closes Manager work
- **WHEN** the Server becomes unavailable without graceful shutdown and starts with a new deployment epoch
- **THEN** every old lease is rejected even if its hash remains in the shared store, the next Runner heartbeat or poll discards the old grant and closes its broker/process boundary, and durable recovery can issue only a fresh replacement grant

#### Scenario: Credential expires during an active turn
- **WHEN** either Manager credential reaches its expiry while the Agent turn is active
- **THEN** the current execution is closed, both leases and the broker are revoked, further invocations are rejected before side effects, and exactly one expiry-recovery/new-turn transition is recorded against the same immutable origin and current Session

#### Scenario: Expiry recovery reauthorizes without replay
- **WHEN** the expiry-recovery/new-turn transition is dispatched
- **THEN** current actor, Enrollment, Session, and target authorization are evaluated again before issuing distinct credentials, the Agent receives only a non-secret recovery fact, and the prior prompt or any state-changing operation with an uncertain result is never automatically replayed

#### Scenario: State-changing result is uncertain at restart or expiry
- **WHEN** a state-changing Manager invocation may have reached the Server but its result is unknown because of restart, expiry, process loss, or transport failure
- **THEN** durable recovery marks the operation outcome unknown, issues no automatic retry, and requires authoritative inspection or a new explicit Agent/user decision before another mutation

### Requirement: Runtime-only credential handling
The plaintext Manager execution credentials SHALL be carried only in a one-shot, non-durable Server-to-Runner poll-response grant and injected only into the process boundary required by the Manager CLI. It MUST NOT be placed in Agent instructions, prompts, system facts, collaboration Skill content, transcripts, Session inputs or state, AgentJob durable state, Slack inbox or outbox payloads, audit records, logs, error messages, or any other durable or model-visible surface.

#### Scenario: Manager execution is assembled
- **WHEN** the Server builds the prompt, execution envelope, Slack execution context, or Session record for a Manager turn
- **THEN** none of those values contains either plaintext credential or a command argument carrying either credential

#### Scenario: Manager CLI runs inside the execution
- **WHEN** the Agent invokes an allowlisted management command or `mo slack message send`
- **THEN** the corresponding management or reply credential is available only through the per-execution private broker and Manager `mo` child-process boundary, while the Agent model, generic shell, Pi/OpenCode base environment, and command transcript receive no plaintext credential value; Pi uses the scoped Runner command path and OpenCode uses an isolated per-execution server/client process rather than the shared OpenCode runtime

#### Scenario: Manager reply uses a separate authentication path
- **WHEN** the Agent invokes `mo slack message send` for the bound Manager origin
- **THEN** the CLI presents the reply credential to `POST /api/slack-manager/reply`; the management credential, ordinary operator credential, and Connection-owned reply route are not accepted as substitutes

#### Scenario: Poll response carries an execution grant
- **WHEN** a Runner claims an initial, follow-up, recovered, or replacement Manager dispatch
- **THEN** the Server includes a one-shot grant only in that HTTP poll response, the Runner keeps it outside `WorkDispatch` and all result/recovery records, and a redelivery or replacement receives new credentials

#### Scenario: Credential-bearing output is produced
- **WHEN** a command, exception, diagnostic, or runtime log contains a token-like value
- **THEN** the value is redacted before it reaches model output, durable records, Slack delivery payloads, or logs

### Requirement: Per-invocation authorization revalidation
Every Manager CLI invocation SHALL validate the presented credential, its expiry, its origin and Session binding, its actor and Enrollment binding, the requested capability, and the current target authorization before performing any side effect. Authorization SHALL be evaluated against current state rather than only the facts captured when the turn started.

#### Scenario: Current authorized invocation is made
- **WHEN** an unexpired credential is used for an allowlisted operation against a target in the bound workspace and Project that the current actor is authorized to manage
- **THEN** the invocation is authorized and the existing application service performs the operation

#### Scenario: Credential is expired
- **WHEN** a Manager CLI invocation presents a credential at or after its expiry, including during an active turn
- **THEN** the invocation is rejected before the management service mutates any resource, and the execution transitions to the durable expiry-recovery path rather than renewing the credential in place

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
A credential validation or authorization failure SHALL fail closed, expose only a non-secret error class and actionable next step to the Agent, and SHALL NOT retry a management mutation or reply delivery with a broader credential or an unbound target.

#### Scenario: Invalid credential is presented
- **WHEN** a Manager CLI invocation presents an unknown, malformed, replayed, or incorrectly bound credential
- **THEN** the invocation is rejected without side effects and the response contains no credential value or protected secret

#### Scenario: Authorization fails during a mutation
- **WHEN** reauthorization fails immediately before a state-changing CLI operation
- **THEN** the operation is not attempted, the resource remains unchanged, and the Agent receives an authorization result rather than a success result

### Requirement: Manager execution grant cleanup
The system SHALL destroy the per-execution broker, launcher, and isolated OpenCode process tree after completion, cancellation, expiry, epoch change, or replacement, and SHALL revoke both lease hashes on completion, cancellation, replacement, recovery, expiry, or Server restart. A Runner, process-boundary, epoch, or lease-store failure MUST fail closed.

#### Scenario: Execution ends
- **WHEN** a Manager execution completes, is cancelled, expires, is replaced, or is invalidated by a Server restart
- **THEN** its broker, launcher, and isolated runtime process are removed, both credentials are unusable, and no plaintext or broker handle is retained in Runner state
