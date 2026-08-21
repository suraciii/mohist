### Requirement: Issue one short-lived credential for each Manager execution
Before every Manager initial execution, follow-up execution, and recovered execution, Server SHALL resolve the durable Manager Slack origin and issue fresh short-lived route-scoped capability credentials. When the execution can perform both management and reply actions, Server SHALL issue one `manager-management` credential and one `manager-slack-reply` credential; each credential SHALL be bound to the immutable Slack origin, authenticated Slack actor, active Workspace enrollment, current Agent Session, dispatch attempt, and an explicit expiry. A credential issued for one execution SHALL NOT be reused for another turn or recovery attempt, and a credential for one route SHALL NOT authorize the other route.

#### Scenario: Initial Manager execution receives a bound credential
- **WHEN** an authorized Manager DM starts an initial Agent execution
- **THEN** Server issues the distinct unexpired route-scoped credentials needed by the execution, each bound to the Manager enrollment, Workspace, initiating actor, conversation origin, dispatch attempt, and new Session before either capability can be used

#### Scenario: Follow-up execution receives a different credential
- **WHEN** a later Manager DM dispatches a follow-up turn for the same Session
- **THEN** Server issues fresh route-scoped credentials for that turn with the same durable origin binding and current Session identity, and does not reuse either initial-turn credential

#### Scenario: Recovery execution is reissued a credential
- **WHEN** a Manager execution is recovered after a Runner, Server, or runtime restart
- **THEN** Server resolves the durable origin again and issues a fresh route-scoped credential pair for the recovered execution rather than persisting or replaying either prior credential

### Requirement: Deliver the Manager credential only to the capability execution environment
Each route-scoped Manager credential SHALL be delivered only to its matching capability bridge and execution environment. Credentials SHALL NOT be placed in Agent Instructions, prompts, Slack system facts, reply anchors, configured Skills, SessionInput text, AgentTurn text, transcripts, durable AgentJob or Session state, command results, Slack messages, or ordinary operational logs. The execution environment SHALL expose only the approved Manager capability associated with the credential and SHALL NOT provide unrestricted management API access.

#### Scenario: Dispatch facts exclude the credential value
- **WHEN** a Manager AgentJob or follow-up dispatch is serialized across the AgentJob, Session, and Runner boundaries
- **THEN** the serialized execution definition, Slack context, prompt, provenance, and system facts contain no Manager credential value or secret-bearing environment payload

#### Scenario: Model-visible command output excludes the credential
- **WHEN** the Manager Agent invokes an approved command or receives its result
- **THEN** the result contains only the authoritative operation outcome and safe next action, and never contains the capability credential, protected Slack token, or credential store address contents

#### Scenario: Credential redaction covers execution diagnostics
- **WHEN** execution errors, terminal facts, or operational logs include text produced while the Manager credential was present in the environment
- **THEN** the credential is redacted and the credential value is absent from persisted or emitted diagnostics

### Requirement: Grant audiences keep management and replies disjoint
Every route-scoped Manager execution grant SHALL carry exactly one explicit capability audience: `manager-management` or `manager-slack-reply`. The management bridge SHALL accept only the former and the nine operations in the Manager command capability specification. The reply bridge SHALL accept only the latter and the exact `mo slack message send` action. A valid grant SHALL NOT authorize an unlisted `mo` command, an arbitrary route, or a request routed through the other bridge. The two grants issued for one execution share the same non-secret origin and dispatch claims but have distinct credential values and route audiences.

#### Scenario: A management grant cannot invoke a reply or arbitrary command
- **WHEN** a Runner presents a valid `manager-management` grant with `mo slack message send`, another `mo` command, or an endpoint/HTTP/database selector
- **THEN** the management bridge rejects the call before any application or outbox service is invoked

#### Scenario: A reply grant cannot invoke management or arbitrary access
- **WHEN** a Runner presents a valid `manager-slack-reply` grant with a management operation, another `mo` command, or an arbitrary route
- **THEN** the reply bridge rejects the call before any application or outbox service is invoked

### Requirement: Validate the credential and current authority on every call
Every Manager capability call SHALL validate the credential's integrity, expiry, bound Workspace origin, bound actor, bound enrollment, and bound Session before invoking an application or outbox service. A management call SHALL then reauthorize the current actor, enrollment, and target resource; a reply call SHALL reauthorize the current actor, enrollment, Session, dispatch, and injected anchor. An expired, malformed, replayed, cross-origin, cross-Session, or otherwise mismatched credential SHALL fail closed and SHALL not reach a management or outbox service.

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

### Requirement: The reply grant reaches the reply endpoint with trusted anchor metadata
For a `manager-slack-reply` call, the Server SHALL validate the grant's Workspace, conversation origin, enrollment/Manager connection identity, actor, Session, and current dispatch attempt against the injected `SlackReplyAnchor` before accepting content. Conversation, thread, Project, Connection, Session, and dispatch fields supplied by the Agent or CLI are untrusted assertions: destination fields MUST equal the injected anchor or the call is rejected, and the Server SHALL enqueue using the injected values. The reply route SHALL use the Manager owner (`ProjectId = __mohist_slack_manager__`, `OwnerKind = manager`, `ConnectionId = enrollment id`) and an input-scoped idempotency key derived from `SessionId` and `TriggeringMessageId`, so retries and recovered attempts for one input reuse one intent while sequential inputs in one DM do not share it.

#### Scenario: A reply uses only the current Manager anchor
- **WHEN** the Manager Agent invokes `mo slack message send` with a valid reply grant and destination assertions matching the injected anchor
- **THEN** the Server validates the current actor, enrollment, Session, and dispatch and creates or reuses one Manager-owned outbox intent at the injected conversation/thread

#### Scenario: A stale or foreign reply anchor is denied
- **WHEN** the Agent supplies a different conversation, thread, Project, Connection, Session, or dispatch reference, or the grant no longer matches the current Manager actor/enrollment
- **THEN** the reply is rejected before outbox enqueue and no delivery is created for the foreign or stale destination

#### Scenario: Reply redelivery is input-idempotent
- **WHEN** the same Manager input sends the same reply action again, including after recovery with a new execution attempt
- **THEN** the existing input-scoped Manager outbox intent is returned and no second final text intent is appended; a different payload for the same input returns an idempotency conflict

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
