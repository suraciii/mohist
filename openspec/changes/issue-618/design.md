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

`SlackTerminalDeliveryHandler` will treat Manager terminal events like ordinary Slack Agent terminal events: it finalizes liveness only. It will not inspect `AssistantText`, render a fallback, or enqueue a Manager-specific follow-up. `mo slack message send` remains the only source of reply text. The reply route must validate the supplied anchor and retain the existing outbox ownership and deduplication rules, so an Agent reply can promote or merge with progress without creating a second logical delivery.

The alternative of keeping Server terminal rendering would make natural-language output and reply-action output compete, recreating duplicate replies and authorization drift. The alternative of putting destination fields in the user prompt would make routing model-controlled; the execution context keeps routing authoritative and hidden from reply content.

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

A `ManagerExecutionCapabilityIssuer` will mint a cryptographically random opaque bearer value for every execution. Its non-secret lease record contains the capability id, scope, immutable Slack origin, actor, Enrollment, Session, dispatch or execution identity, issue time, expiry, and active state. The server stores only a hash of the bearer value and the binding metadata in a short-lived, revocable runtime store; it does not persist the plaintext value. Completion, cancellation, replacement, or recovery invalidates the old lease. A new follow-up or recovered execution always receives a new value.

The lease is transported in an ephemeral Server-to-Runner execution field that is deliberately separate from `AgentJobInput`, `WorkDispatch.with`, the Slack execution context, and all durable Session or job state. The Runner holds it only for the active execution and injects it at the child-process boundary used by the Manager CLI. The base Runner environment, model prompt, collaboration Skill, command arguments, command transcript, task log, exceptions, and terminal result do not contain the value. CLI and Runner output handling adds the active value to the redaction set before output can reach the model, outbox, or logs.

The CLI presents the capability only when making a Manager-mode request. Server validation checks the lease, expiry, execution and origin binding, requested logical capability, current actor and Enrollment state, and current target authorization before calling an application service. It never broadens the target, retries with an operator credential, or falls back to a less restrictive authorization path. If the lease store or validation boundary is unavailable, the operation fails closed.

A signed self-contained token was considered because it avoids a lease lookup, but it makes immediate invalidation and same-execution replay control harder. A durable token table was rejected because credentials must not become durable application data and cleanup would create an additional recovery obligation.

### 5. Keep existing application services authoritative for CLI results

The Manager CLI capability adapter will translate a logical command into the existing CLI request path or a shared service command handler, then return the actual service projection. The management application services remain responsible for validation, current Enrollment state, Project and Agent lookup, Connection workspace ownership, and mutation results. The Manager-specific `SlackManagerToolExecutor` behavior will be moved into or reused by the CLI capability adapter only where it represents an existing supported operation; it will not remain as a terminal-output protocol.

Inspection results, readiness states, mutation results, validation failures, authorization failures, unavailable-service results, and next actions are returned without inference. In particular, a successful request does not cause the Server to claim that a resource is ready unless the authoritative service result says so.

Duplicating those operations in a new Manager service was rejected because it would recreate the current drift between CLI, Web, and Slack behavior. Directly exposing the broad management API was rejected because target selection and destructive operations would no longer be constrained by an explicit Agent capability catalog.

### 6. Drive Manager liveness through the shared durable Slack projection

On accepted ingress, the Server will enqueue one receipt reaction through `SlackStatusProjection` using a dispatch identity derived from the immutable origin. Queueing or starting the Agent execution transitions the same logical execution to replaceable progress: receipt is removed before the working projection is shown, and repeated progress events replace or deduplicate the existing projection.

Terminal delivery derives its liveness key from the logical Manager execution, not from a transient retry or recovered runtime. For `completed`, it removes working state and adds one success reaction. For `failed`, `cancelled`, and `unknown`, it removes working state and adds one attention reaction. Terminal convergence must also add the terminal reaction when progress was never successfully emitted, so an execution cannot remain in receipt state merely because it completed quickly.

Outbox uniqueness, stable dispatch references, and durable recovery facts make receipt, progress, reaction removal, reply promotion, and terminal delivery idempotent across duplicate ingress, redelivery, process restart, Runner recovery, and uncertain Slack delivery. The Manager-specific owner remains available for Manager DM outbox rows, but the projection and adapter semantics are shared with ordinary Agent Connections.

A Server-authored terminal message was considered as a liveness fallback, but it conflicts with the requirement that silence is a valid Agent outcome and would reintroduce duplicate reply ownership. Incomplete progress should be repaired by reaction convergence, not by inventing user-visible text.

### 7. Remove the retired protocol as a breaking change

The built-in instruction asset will be replaced. The parser, Manager tool-result follow-up processor, Server-side terminal tool executor, execution fence, and Manager-specific terminal text path will be removed or unregistered once no other caller depends on them. The Manager capability catalog may remain as a shared logical allowlist source for tests and CLI enforcement, but it will no longer describe a model-output envelope.

Existing `SlackManagerApplicationService`, resource projections, CLI routes, Web routes, inbox records, Session mappings, and outbox records remain because they are part of the supported control plane or recovery model. There is no attempt to interpret a late old envelope as a management request.

## Risks / Trade-offs

- [Credential exposure through a child environment, exception, or command output] -> Inject the value only at the Manager CLI process boundary, never into the model-facing base environment or command arguments; register it with the Runner and Server redactors and add tests that inspect prompts, transcripts, logs, outbox payloads, and terminal events.
- [A broad or direct API command bypasses the CLI allowlist] -> Enforce the logical capability scope in both CLI command dispatch and Server authentication/service admission; reject before target lookup with side effects or mutation.
- [Authorization changes after a turn begins] -> Revalidate lease, Enrollment, actor, Project, Agent, and Connection ownership on every invocation; never rely only on turn-start facts and never retry with a broader credential.
- [Duplicate Sessions, mutations, replies, or reactions under at-least-once delivery] -> Derive all inbox, Session input, execution, reply, and liveness identities from immutable origin and operation ids; use conditional mapping updates and the existing outbox uniqueness and promotion rules.
- [A missing runtime or process restart loses an in-flight capability] -> Treat the old lease as invalid, recover from durable inbox/Session/execution/outbox facts, and issue a fresh lease for the replacement execution.
- [A fast completion leaves the message with only the receipt reaction] -> Make terminal convergence independent of progress delivery and explicitly remove receipt/working state before adding the terminal reaction.
- [The breaking deployment encounters in-flight old Manager jobs] -> Quiesce Manager ingress during cutover, drain or mark existing executions through the old path before enabling the new path, and deliberately treat late old output as ordinary Agent output rather than executing it.
- [A Manager Agent sends no reply] -> Preserve the ordinary Agent contract: close terminal reaction liveness and record the outcome without creating fallback text. Operational monitoring should distinguish no-reply completion from delivery failure.
- [Capability validation is inconsistent across Server instances] -> Use a shared short-lived lease store or shared validation key and fail closed when the issuer or validator cannot establish the lease; never fall back to a durable plaintext token.
- [Owner claim or transfer output contains a sensitive one-time value] -> Confirm the allowed operation's existing result contract, keep the value out of model-visible diagnostics and logs, and restrict any user-facing delivery to the already-authorized target instruction path.

## Migration Plan

1. Add the ephemeral capability field and Manager CLI mode additively to the Server, Runner, and CLI contracts. Add Runner capability/version gating so a Manager execution is dispatched only to a Runner that can consume the ephemeral field and enforce output redaction. Existing non-Manager dispatches continue to use their current contracts.
2. Implement shared Session routing and liveness convergence while retaining the old Manager parser behind no new call sites. Add focused Server, Runner, CLI, and `mohist-slack` integration tests for replay, replacement Session, credential boundaries, current-state authorization, reply ownership, loop prevention, and all terminal outcomes.
3. Deploy the compatible Runner and CLI support, then quiesce new Manager ingress briefly. Drain old Manager executions, or let durable recovery classify unresolved ones as unknown without executing a new mutation. Do not replay an uncertain state-changing operation automatically.
4. Deploy the Server change and new built-in Manager instructions. New messages use natural-language turns, the ordinary Agent reply action, the CLI capability allowlist, and shared reaction liveness. Existing DM mappings and Session records remain the recovery source of truth; they are not bulk-rewritten.
5. Remove the old parser, tool-result follow-up, terminal renderer, and execution-fence registrations after the new path is active. Do not store or migrate any capability plaintext. If a database table exists only for the retired execution fence, leave it unused during the rollback window and remove it in a later cleanup migration so the first rollout remains reversible.
6. For rollback, quiesce new Manager ingress, restore the prior Server/Runner/CLI bundle, and resume from durable inbox and outbox state. Retain the old fence schema during that window. Do not resend completed or unknown management mutations; inspect the authoritative resource state before any retry. Capability values from the new deployment are invalidated by lease shutdown or expiry and are never migrated to the old protocol.

## Open Questions

- What capability TTL and clock-skew allowance are appropriate for the longest supported Manager CLI operation, and should an execution be renewed only by starting a new turn?
- Is the deployment topology single-server, or must the short-lived lease store and revocation state be shared across Server instances?
- What exact process boundary can inject a secret into `mo` for both Pi and OpenCode on supported Linux and Windows runners without exposing it to generic model shell commands?
- Should the anchored `mo slack message send` route continue using the ordinary Runner/operator credential exclusively, or should it accept a separately scoped, non-management delivery capability issued with the Manager execution?
- Which existing owner claim/transfer command is in the final Manager allowlist, and how should its one-time code be delivered without violating the protected-value redaction rules?
- Can every supported Slack adapter version provide idempotent reaction add/remove behavior needed for terminal convergence, or is an adapter capability gate required before enabling Manager liveness?
