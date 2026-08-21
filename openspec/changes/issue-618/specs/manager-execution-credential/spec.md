### Requirement: Issue one short-lived credential for each Manager execution
Before every Manager initial execution, follow-up execution, and recovered execution, Server SHALL resolve the durable Manager Slack origin and issue a new short-lived capability credential. The credential SHALL be bound to the immutable Slack origin, authenticated Slack actor, active Workspace enrollment, current Agent Session, and an explicit expiry. A credential issued for one execution SHALL NOT be reused for another turn or recovery attempt.

#### Scenario: Initial Manager execution receives a bound credential
- **WHEN** an authorized Manager DM starts an initial Agent execution
- **THEN** Server issues a unique unexpired credential bound to the Manager enrollment, Workspace, initiating actor, conversation origin, and new Session before the command capability can be used

#### Scenario: Follow-up execution receives a different credential
- **WHEN** a later Manager DM dispatches a follow-up turn for the same Session
- **THEN** Server issues a fresh credential for that turn with the same durable origin binding and the current Session identity, and does not reuse the initial turn's credential

#### Scenario: Recovery execution is reissued a credential
- **WHEN** a Manager execution is recovered after a Runner, Server, or runtime restart
- **THEN** Server resolves the durable origin again and issues a fresh credential for the recovered execution rather than persisting or replaying the prior credential

### Requirement: Deliver the Manager credential only to the capability execution environment
The Manager credential SHALL be delivered only to the command capability bridge and its execution environment. It SHALL NOT be placed in Agent Instructions, prompts, Slack system facts, reply anchors, configured Skills, SessionInput text, AgentTurn text, transcripts, durable AgentJob or Session state, command results, Slack messages, or ordinary operational logs. The execution environment SHALL expose only the approved Manager command capability associated with the credential and SHALL NOT provide unrestricted management API access.

#### Scenario: Dispatch facts exclude the credential value
- **WHEN** a Manager AgentJob or follow-up dispatch is serialized across the AgentJob, Session, and Runner boundaries
- **THEN** the serialized execution definition, Slack context, prompt, provenance, and system facts contain no Manager credential value or secret-bearing environment payload

#### Scenario: Model-visible command output excludes the credential
- **WHEN** the Manager Agent invokes an approved command or receives its result
- **THEN** the result contains only the authoritative operation outcome and safe next action, and never contains the capability credential, protected Slack token, or credential store address contents

#### Scenario: Credential redaction covers execution diagnostics
- **WHEN** execution errors, terminal facts, or operational logs include text produced while the Manager credential was present in the environment
- **THEN** the credential is redacted and the credential value is absent from persisted or emitted diagnostics

### Requirement: Validate the credential and current authority on every call
Every Manager command call SHALL validate the credential's integrity, expiry, bound Workspace origin, bound actor, bound enrollment, and bound Session before invoking an application service. The call SHALL then reauthorize the current actor, enrollment, and target resource. An expired, malformed, replayed, cross-origin, cross-Session, or otherwise mismatched credential SHALL fail closed and SHALL not reach a management service.

#### Scenario: An expired credential is rejected
- **WHEN** the command bridge receives a Manager call after the credential expiry
- **THEN** the call is rejected as unavailable or unauthorized and no management read or write occurs

#### Scenario: A credential from another Session is rejected
- **WHEN** a command call presents a credential bound to a different Manager Session, conversation, actor, or enrollment
- **THEN** the call is rejected before target authorization and no resource state is changed

#### Scenario: Current authorization revocation invalidates an otherwise live credential
- **WHEN** the claimed actor changes, the enrollment is disabled or removed, Manager capability becomes unavailable, or the target is no longer authorized after credential issuance
- **THEN** the next command is rejected immediately and the credential cannot preserve the earlier authorization decision

#### Scenario: A valid credential cannot cross Workspace boundaries
- **WHEN** a command call uses a valid current credential with a target resource outside its bound Workspace enrollment
- **THEN** the call is rejected as unauthorized or not found and the foreign resource is neither read nor modified

### Requirement: Manager execution credentials are ephemeral and non-disclosable
The Manager capability credential SHALL have no durable Manager-state representation and SHALL be discarded when its execution ends or expires. Server SHALL persist only non-secret references and ordinary execution provenance needed for recovery; it MUST NOT persist the credential itself or make it available through status, inspection, audit, transcript, Web, CLI, or Slack projection APIs. Protected CLI/Web credential-entry and authorization flows SHALL continue to use their existing stores and boundaries independently of the ephemeral Manager credential.

#### Scenario: Restart does not recover a prior credential value
- **WHEN** the Server or Runner restarts after issuing a Manager execution credential
- **THEN** no durable row, recovery payload, or status response contains the prior credential, and recovery issues a new credential if execution continues

#### Scenario: Credential expiry leaves no reusable capability
- **WHEN** a Manager execution credential expires or its execution is terminal
- **THEN** later calls using that credential are rejected and no durable or external path can renew it without a new authorized execution

#### Scenario: Protected credential entry remains outside Manager execution
- **WHEN** a CLI or Web operator supplies protected Slack setup or Agent App credentials through its existing authorization path
- **THEN** that path retains its current protected handling, and the Manager Agent does not receive the submitted credential or gain a credential-read capability
