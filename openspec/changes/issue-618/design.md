## Context

Issue #618 changes the Slack Manager from a Server-owned tool protocol into an ordinary Slack Agent. The proposal and four capability specs define a breaking change: the model must produce normal Agent turns, management must run through a narrow `mo` CLI surface, replies must be sent by the Agent reply action, and liveness must be durable and idempotent.

The current implementation already has useful durable boundaries, but Manager behavior diverges from ordinary Slack Agents:

- `SlackManagerIngressService` authenticates and deduplicates Slack events, but currently accepts Manager conversation results that contain Server-authored reply text.
- `SlackManagerConversationService` wraps user prompts in Manager-specific instructions, returns synthesized acknowledgements, and has a separate continuation path.
- `SlackTerminalDeliveryHandler` invokes `SlackManagerToolTurnProcessor`, which parses `mohistManagerTool`, executes Server-side tools, and queues a `managerToolResult` follow-up.
- `AgentSlackExecutionContext` already provides the immutable Slack reply anchor and pinned collaboration Skill used by ordinary Agent Connections.
- `SlackStatusProjection` and `SlackOutboxStore` already provide durable reaction and replaceable-progress primitives, while the existing CLI and Web routes call the application services that should remain authoritative.

The target flow is therefore:

1. Manager ingress validates the managed bot, direct-message origin, enrollment, actor, and inbox identity.
2. The inbox and conversation-to-Session mapping are made durable before dispatch is considered accepted.
3. A normal Agent launch or follow-up is dispatched with the Slack execution context and a fresh, ephemeral Manager capability.
4. The Runner exposes the capability only to the Manager CLI invocation boundary. The CLI calls existing Server application services.
5. The Server validates the capability and current authorization for every CLI invocation.
6. The Agent sends any user-visible reply through `mo slack message send`; terminal delivery only closes liveness.

Durable inbox, Session, execution, and outbox facts remain the recovery source of truth. Plaintext capability credentials must not enter prompts, instructions, `AgentSlackExecutionContext`, Session or AgentJob state, Slack payloads, terminal events, logs, or transcripts. Existing CLI and Web callers must retain their current authorization and result behavior.

## Goals / Non-Goals

**Goals:**

- Make Manager initial turns and follow-ups use the ordinary Agent Session launch and follow-up contracts.
- Persist one recoverable Session mapping per Manager Slack origin and deduplicate replayed messages, replacement Sessions, and follow-up dispatches.
- Remove the private model-output envelope, parser, Server-side Manager tool follow-up, synthesized acknowledgement, and Server-authored terminal reply.
- Make the Agent reply action the sole owner of Manager reply text while preserving the authoritative conversation and thread anchor.
- Expose only the supported Manager management capabilities through `mo`: status/list/view/diagnostics, Agent creation or mounting, access-policy changes, enable/disable, and owner transfer where applicable.
- Reuse existing application services and authorization rules for Project, Agent, Enrollment, actor, and Slack Connection targets.
- Issue a new short-lived capability for each initial, follow-up, recovered, or replacement execution, and reauthorize every invocation against current state.
- Converge receipt, progress, and terminal reactions through the durable Slack outbox with exactly one terminal reaction for every known or unknown outcome.
- Preserve loop prevention so events authored by the managed Manager bot or projected Agent replies cannot start another Manager turn.

**Non-Goals:**

- Adding a new Manager HTTP API, a second Server-side management protocol, or a general-purpose management RPC for the Agent.
- Supporting the retired `mohistManagerTool` envelope or translating old model output into the new capability calls.
- Enabling secret submission, credential reads or rotation, credential addresses, binding removal, permanent deletion, arbitrary API access, or other destructive control-plane operations.
- Changing ordinary non-Manager Slack Agent execution, reply ownership, CLI/Web authorization, or Slack setup and credential provisioning workflows.
- Persisting Manager capability values, replaying a state-changing operation after an unknown result, or creating a Server fallback message when the Agent sends no reply.

## Decisions

### 1. Use the ordinary durable Agent Session path

Manager ingress will retain its existing authentication, inbox, and DM mapping responsibilities, then delegate execution to the same Session launch and follow-up mechanisms used by Slack Agent Connections. Initial messages use a pre-minted Session identity and persist the origin, initiating actor, mapping, and accepted input before submitting the AgentJob. Follow-ups use the mapped Session's `AcceptFollowup` and ordinary follow-up dispatcher with an idempotency key derived from the immutable Slack message identity.

If the mapped runtime Session is missing, the conversation coordinator creates a replacement Session for the current origin, updates the mapping conditionally, and accepts the current input exactly once. A replay uses the inbox identity and Session input idempotency records rather than creating another Session or dispatch.

This preserves the existing recovery model and keeps the Manager Session visible as a normal Agent Session. Keeping a dedicated Manager conversation protocol would preserve less code but would continue to duplicate Session routing, authorization, and delivery semantics. Creating a new Manager-specific Session aggregate would add another durable state machine without solving the protocol drift.

### 2. Carry Slack origin through the shared execution context and give reply ownership to the Agent

Every Manager execution receives `AgentSlackExecutionContext` built from the durable origin: workspace, conversation, thread root, triggering message, initiating actor, Enrollment or Connection identity, Session, and dispatch reference. The pinned collaboration Skill remains the instruction for sending a reply and must not be replaced with Manager-specific delivery instructions.

The built-in Manager instructions will describe normal natural-language Agent behavior and the available CLI capabilities. The Manager conversation service will pass the user's request as ordinary input. It will not add a private JSON format or a Server result protocol.

`SlackTerminalDeliveryHandler` will treat Manager terminal events like ordinary Slack Agent terminal events: it finalizes liveness only. It will not inspect `AssistantText`, render a fallback, or enqueue a Manager-specific follow-up. `mo slack message send` remains the only source of reply text.

Manager replies use a dedicated `POST /api/slack-manager/reply` route and a separate, ephemeral `ManagerReplyLease`; the management capability lease is rejected by this route and the ordinary operator/Connection reply route is unchanged. The Manager-mode CLI sends the conversation and thread values from the command, but the route compares them with the lease's immutable origin. The route derives `projectId = SlackDeliveryOwnerIds.ManagerProjectId`, `ownerKind = SlackDeliveryOwnerKinds.Manager`, and `ownerId = enrollment.Id` from the validated lease; none is selected by the Agent. It then requires the durable Manager DM/thread mapping and accepted inbox origin to match workspace, conversation, thread root, triggering message, enrollment, Session, actor, and dispatch identity. A mismatch, stale Session mapping, inactive enrollment, expired/revoked lease, or a distinct duplicate execution identity is rejected without an outbox write.

For a valid first send, `SlackOutboxStore.EnqueueManagerAgentReplyAsync` selects only the pending Manager replaceable-progress row for that exact logical execution and promotes it in place, preserving its `StatusDispatchRef` and dispatch identity. If progress was never stored, it inserts one Manager-owned terminal row with that same execution dispatch reference. A repeated request with the same execution idempotency key returns the existing row without appending or creating another row; a distinct execution cannot merge into it. Terminal liveness uses the same owner, origin, and execution reference, so it removes receipt/working state and adds one terminal reaction independently of whether the reply promoted progress. Initial turns, follow-ups, fast completion, duplicate sends, and managed-bot/Agent-reply loop prevention are covered by route, outbox, and adapter tests.

The alternative of keeping Server terminal rendering would make natural-language output and reply-action output compete, recreating duplicate replies and authorization drift. The alternative of putting destination fields in the user prompt would make routing model-controlled; the execution context and lease keep routing authoritative while the Manager route enforces it.

### 3. Implement the management boundary as a CLI capability mode plus Server enforcement

The CLI will have an explicit Manager capability mode. In that mode, command registration and argument validation allow only the logical operations required by the specs:

- workspace or Connection status, list, view, and diagnostics;
- supported Agent creation or mounting;
- Connection access-policy changes;
- Connection enable and disable;
- owner claim or transfer operations only where the existing management contract requires them.

Setup, credential-file input, credential provisioning, credential reads or rotation, `remove-binding`, permanent deletion, delivery recovery controls, arbitrary `mo` commands, and direct management API calls are outside this mode. The CLI returns the existing service result, error class, and next action as the Agent's authoritative input; the Server does not convert it into a synthetic acknowledgement.

The capability is enforced twice. The CLI rejects commands outside the logical allowlist before making an HTTP request, and Server authentication recognizes the Manager capability principal and rejects routes or operations outside its scope before invoking a mutating service. This protects against bypassing the CLI with `curl` or another command while preserving the existing operator and Web authentication paths for non-Manager callers.

The anchored Slack reply action is a separate delivery capability of the ordinary Slack Agent contract, not a general management capability. It remains limited to the reply anchor and existing outbox ownership rules. This prevents the Manager from turning the management credential into an arbitrary Slack messaging credential.

A hidden Server tool endpoint was considered, but it would retain the same private protocol and a second command surface. An unrestricted CLI with only natural-language instructions was rejected because prompt instructions cannot enforce a security boundary.

### 4. Issue opaque, per-execution capability leases

A `ManagerExecutionCapabilityIssuer` will mint two cryptographically random opaque bearer values for every execution: a management lease and a separate reply lease. Each non-secret lease record contains the capability id, scope, immutable Slack origin, actor, Enrollment, Session, dispatch or execution identity, issue time, expiry, and active state. The server stores only hashes of the bearer values and their binding metadata in a short-lived, revocable runtime store; it does not persist either plaintext value. Completion, cancellation, replacement, or recovery invalidates both leases. A new follow-up or recovered execution always receives new values.

The leases are transported in a one-shot `managerExecutionGrant` field on the in-memory Runner poll response, deliberately separate from `AgentJobInput`, `WorkDispatch.with`, the serialized active-work ledger, the Slack execution context, and all durable Session or job state. The Server creates the grant only after loading the durable dispatch and before serializing the HTTP response; it contains the two plaintext bearers, the execution identity, and expiry. `WorkDispatchResponse`/poll response is the only wire carrier, and request/response logging must redact and never persist that field. The Runner keeps the grant only in an in-memory `BoundManagerExecution` wrapper keyed by the work key; it never copies the grant into `DispatchWorkItem`, the result journal, `inFlight`, `awaitingAck`, recovery receipts, or terminal reports. A re-poll or replacement execution gets a new grant; a recovered runtime does not reuse the old one.

For each Manager execution the Runner creates a private, one-shot CLI broker and a per-execution `mo` launcher placed in a private directory at the front of `PATH`. On Linux the broker is a mode-0600 Unix-domain socket; on Windows it is a user/Runner-ACL named pipe. The launcher has only a broker handle, not a bearer. When and only when the command resolves to the Manager `mo` launcher, it obtains the needed bearer from the broker and `exec`s the real `mo` child with the bearer in a private process environment/header. The parent model shell, `printenv`, generic commands, command arguments, prompt, transcript, and the Pi/OpenCode runtime base environment never receive it. Both Pi and OpenCode use this same Runner command boundary; a runtime or command executor that cannot preserve the boundary is rejected by capability/version gating rather than receiving a base-environment credential. The Manager CLI uses the management bearer for the allowlisted management route and the separate reply bearer only for `POST /api/slack-manager/reply`; it cannot use either bearer as an operator or ordinary Connection credential.

The broker, launcher, and child environment are destroyed in a `finally` path when the execution ends or is cancelled. The Server revokes both lease hashes on completion, cancellation, replacement, or recovery; an expiry sweeper removes expired runtime records, and lease-store shutdown revokes the remaining active records. A lease-store or broker failure fails closed. Server validation checks the requested scope, lease, expiry, execution and full-origin binding, current actor and Enrollment state, and current target authorization before calling an application service. It never broadens the target, retries with an operator credential, or falls back to a less restrictive authorization path. Runner and Server redactors register both bearers before execution and redact them from line capture, aggregate command results, exceptions, runtime events, task logs, work reports, audit data, outbox payloads, and terminal events.

A signed self-contained token was considered because it avoids a lease lookup, but it makes immediate invalidation and same-execution replay control harder. A durable token table was rejected because credentials must not become durable application data and cleanup would create an additional recovery obligation.

### 5. Keep existing application services authoritative for CLI results

The Manager CLI capability adapter will translate a logical command into the existing CLI request path or a shared service command handler, then return the actual service projection. The management application services remain responsible for validation, current Enrollment state, Project and Agent lookup, Connection workspace ownership, and mutation results. The Manager-specific `SlackManagerToolExecutor` behavior will be moved into or reused by the CLI capability adapter only where it represents an existing supported operation; it will not remain as a terminal-output protocol.

Inspection results, readiness states, mutation results, validation failures, authorization failures, unavailable-service results, and next actions are returned without inference. In particular, a successful request does not cause the Server to claim that a resource is ready unless the authoritative service result says so.

Duplicating those operations in a new Manager service was rejected because it would recreate the current drift between CLI, Web, and Slack behavior. Directly exposing the broad management API was rejected because target selection and destructive operations would no longer be constrained by an explicit Agent capability catalog.

### 6. Drive Manager liveness through the shared durable Slack projection

On accepted ingress, the Server will enqueue one receipt reaction through `SlackStatusProjection` using a dispatch identity derived from the immutable origin. Queueing or starting the Agent execution transitions the same logical execution to replaceable progress: receipt is removed before the working projection is shown, and repeated progress events replace or deduplicate the existing projection.

Terminal delivery derives its liveness key from the logical Manager execution, not from a transient retry or recovered runtime. For `completed`, it removes working state and adds one success reaction. For `failed`, `cancelled`, and `unknown`, it removes working state and adds one attention reaction. Terminal convergence must also add the terminal reaction when progress was never successfully emitted, so an execution cannot remain in receipt state merely because it completed quickly.

Outbox uniqueness, stable dispatch references, and durable recovery facts make receipt, progress, reaction removal, reply promotion, and terminal delivery idempotent across duplicate ingress, redelivery, process restart, Runner recovery, and uncertain Slack delivery. Manager receipt, progress, reply, and terminal mutations use the Enrollment-owned Manager row throughout; terminal convergence removes receipt/working state and adds the terminal reaction even when no progress row was ever stored. The Manager-specific owner remains available for Manager DM outbox rows, but the projection and adapter semantics are shared with ordinary Agent Connections.

A Server-authored terminal message was considered as a liveness fallback, but it conflicts with the requirement that silence is a valid Agent outcome and would reintroduce duplicate reply ownership. Incomplete progress should be repaired by reaction convergence, not by inventing user-visible text.

### 7. Remove the retired protocol as a breaking change

The built-in instruction asset will be replaced. The parser, Manager tool-result follow-up processor, Server-side terminal tool executor, execution fence, and Manager-specific terminal text path will be removed or unregistered once no other caller depends on them. The Manager capability catalog may remain as a shared logical allowlist source for tests and CLI enforcement, but it will no longer describe a model-output envelope.

Existing `SlackManagerApplicationService`, resource projections, CLI routes, Web routes, inbox records, Session mappings, and outbox records remain because they are part of the supported control plane or recovery model. There is no attempt to interpret a late old envelope as a management request.

## Risks / Trade-offs

- [Credential exposure through a child environment, exception, or command output] -> Carry the two scoped bearers only in the one-shot poll grant, keep them in the Runner's in-memory execution wrapper, retrieve them through the private per-execution broker only for the Manager `mo` child, and register both with the Runner and Server redactors. Tests inspect prompts, base environments, generic shell commands, Pi/OpenCode child environments, transcripts, logs, outbox payloads, terminal events, and HTTP diagnostics.
- [A broad or direct API command bypasses the CLI allowlist] -> Enforce the logical capability scope in both CLI command dispatch and Server authentication/service admission; reject before target lookup with side effects or mutation.
- [Authorization changes after a turn begins] -> Revalidate lease, Enrollment, actor, Project, Agent, and Connection ownership on every invocation; never rely only on turn-start facts and never retry with a broader credential.
- [Duplicate Sessions, mutations, replies, or reactions under at-least-once delivery] -> Derive all inbox, Session input, execution, reply, and liveness identities from immutable origin and operation ids; use conditional mapping updates and the existing outbox uniqueness and promotion rules.
- [A missing runtime or process restart loses an in-flight capability] -> Treat the old lease as invalid, recover from durable inbox/Session/execution/outbox facts, and issue a fresh lease for the replacement execution.
- [A fast completion leaves the message with only the receipt reaction] -> Make terminal convergence independent of progress delivery and explicitly remove receipt/working state before adding the terminal reaction.
- [The breaking deployment encounters in-flight old Manager jobs] -> Quiesce Manager ingress during cutover, drain or mark existing executions through the old path before enabling the new path, and deliberately treat late old output as ordinary Agent output rather than executing it.
- [A Manager Agent sends no reply] -> Preserve the ordinary Agent contract: close terminal reaction liveness and record the outcome without creating fallback text. Operational monitoring should distinguish no-reply completion from delivery failure.
- [Capability validation is inconsistent across Server instances] -> Use a shared short-lived lease store or shared validation key and fail closed when the issuer or validator cannot establish either scoped lease; never fall back to a durable plaintext token.
- [Manager reply is routed as a Connection or crosses Sessions] -> Authenticate the dedicated reply lease, derive the synthetic Manager project and Manager owner from its enrollment binding, compare the supplied conversation/thread with the immutable origin, and require the matching durable inbox and Session mapping before promoting or inserting the exact execution outbox row.
- [Owner claim or transfer output contains a sensitive one-time value] -> Confirm the allowed operation's existing result contract, keep the value out of model-visible diagnostics and logs, and restrict any user-facing delivery to the already-authorized target instruction path.

## Migration Plan

1. Add the non-durable `managerExecutionGrant` poll-response field, the two scoped lease validators, the Manager CLI mode, and the dedicated Manager reply route additively to the Server, Runner, and CLI contracts. Add Runner capability/version gating so a Manager execution is dispatched only to a Runner that supports the grant, private broker/`mo` launcher for both Pi and OpenCode, and output redaction. Existing non-Manager dispatches continue to use their current contracts.
2. Implement shared Session routing and liveness convergence while retaining the old Manager parser behind no new call sites. Add focused Server, Runner, CLI, and `mohist-slack` integration tests for replay, replacement Session, credential boundaries, current-state authorization, reply ownership, loop prevention, and all terminal outcomes.
3. Deploy the compatible Runner and CLI support, then quiesce new Manager ingress briefly. Drain old Manager executions, or let durable recovery classify unresolved ones as unknown without executing a new mutation. Do not replay an uncertain state-changing operation automatically.
4. Deploy the Server change and new built-in Manager instructions. New messages use natural-language turns, the ordinary Agent reply action, the CLI capability allowlist, and shared reaction liveness. Existing DM mappings and Session records remain the recovery source of truth; they are not bulk-rewritten.
5. Remove the old parser, tool-result follow-up, terminal renderer, and execution-fence registrations after the new path is active. Do not store or migrate any capability plaintext. If a database table exists only for the retired execution fence, leave it unused during the rollback window and remove it in a later cleanup migration so the first rollout remains reversible.
6. For rollback, quiesce new Manager ingress, restore the prior Server/Runner/CLI bundle, and resume from durable inbox and outbox state. Retain the old fence schema during that window. Do not resend completed or unknown management mutations; inspect the authoritative resource state before any retry. Capability values from the new deployment are invalidated by lease shutdown or expiry and are never migrated to the old protocol.

## Open Questions

- What capability TTL and clock-skew allowance are appropriate for the longest supported Manager CLI operation, and should an execution be renewed only by starting a new turn?
- Is the deployment topology single-server, or must the short-lived lease store and revocation state be shared across Server instances?
- Which existing owner claim/transfer command is in the final Manager allowlist, and how should its one-time code be delivered without violating the protected-value redaction rules?
- Can every supported Slack adapter version provide idempotent reaction add/remove behavior needed for terminal convergence, or is an adapter capability gate required before enabling Manager liveness?
