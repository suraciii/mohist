## Context

The current Manager path is separate from ordinary Slack Agent execution. `SlackManagerIngressService` authenticates a DM and calls `SlackManagerConversationService`, which launches or follows up a Session with Manager-specific prompt text. After the turn, `SlackManagerToolTurnProcessor` parses `assistantText` for a `mohistManagerTool` JSON envelope, `SlackManagerToolExecutor` performs the mutation, and the processor synthesizes another Session input so the Agent can describe the result. `SlackTerminalDeliveryHandler` can also author a Slack message from Runtime output, while ingress emits an acceptance message directly.

That creates a second command, recovery, reply, and liveness protocol beside the ordinary Slack path. The preceding Slack execution-context work already provides the reusable boundary: durable Slack provenance, a versioned `AgentSlackExecutionContext` with a reply anchor and collaboration Skill, `AgentSessionGrain` follow-ups, Runner source/context validation, the `mo slack message send` reply action, and `SlackStatusProjection`.

This change applies those boundaries to the built-in `mohist-slack` Agent. The Server remains authoritative for workspace enrollment, Manager actor identity, Session routing, target authorization, command results, Slack delivery intents, and liveness. The Runner only carries the execution contract and invokes the approved command bridge. The Manager capability credential is ephemeral execution material, not Agent or Session state.

## Goals / Non-Goals

**Goals:**

- Admit Manager DMs as one ordinary Slack-origin Agent Session per durable DM mapping, with normal initial launch, follow-up, recovery, idempotency, and bound-thread-root behavior.
- Carry a non-secret Manager execution marker through initial, follow-up, and recovered dispatches so Server can issue a fresh capability grant for each execution attempt.
- Expose exactly `list`, `view`, `create`, `claim-owner`, `edit`, `enable`, `disable`, `transfer-owner`, and `diagnostics` through the existing `mo` command surface.
- Reuse the application services that back the CLI/Web paths and return structured authoritative results, including validation, conflict, not-found, unavailable, idempotent, and next-action states.
- Reauthorize the current actor, enrollment, workspace, and target on every management call, and revalidate the current Slack anchor on every reply call. Keep secrets, credential reads, permanent deletion, binding removal, arbitrary API access, and direct database access outside both capabilities.
- Keep the capability credential out of prompts, Instructions, Skills, Slack facts, transcripts, durable AgentJob/Session state, command results, and ordinary logs. Deliver it only to the execution-scoped command bridge and discard it at expiry or terminal completion.
- Make the Agent the only conversational author through `mo slack message send`; use reactions for received, working, and terminal liveness, with silence accepted as a valid turn outcome.
- Suppress enrolled Manager Bot and eligible managed Agent Bot messages before actor authorization, claim handling, Session routing, or durable work admission.

**Non-Goals:**

- Adding a Slack slash command, Manager-specific natural-language grammar, or another model-output DSL.
- Changing protected CLI/Web credential entry, Slack setup, credential storage, or the existing one-time owner workflow.
- Granting the Manager access to Slack tokens, App-level tokens, signing secrets, credential values, irreversible deletion, binding removal, arbitrary HTTP, SQL, or unrestricted management APIs.
- Making model compliance deterministic. The Skill directs the Agent to use the command and reply actions; it does not make assistant prose authoritative.
- Replacing the ordinary Agent Session, Runner, Slack outbox, or application-service abstractions with Manager-only equivalents.

## Decisions

### 1. Route Manager DMs through the ordinary Session lifecycle

`SlackManagerIngressService` will retain its early managed-Bot classification, enrollment lookup, claim consumption, and provider-inbox deduplication, but accepted non-claim text will use the same launch/follow-up boundaries as an ordinary Slack Connection.

- The first accepted message uses `AgentLauncher.LaunchConnectionAsync` for `mohist-slack`, with the enrollment id as the stable Manager Slack connection identity, the current workspace/member/conversation/message facts in `ConnectionLaunchOrigin`, and a pre-minted stable Session/Input/Turn identity.
- The initial Session input is stored with `ProviderKind = slack`, an effective root of `ThreadTs ?? MessageTs`, the authenticated member, enrollment, and message provenance, and an explicit Slack execution source. Manager-specific prompt wrapping and the phrase "server-authorized manager tool protocol" are removed.
- The DM mapping and provider-inbox route remain durable lookup indexes. Session provenance and the bound root are the execution authority used to reconstruct a follow-up or recovery; a missing or conflicting mapping never causes a new non-Slack execution.
- A later message calls `AcceptFollowupAsync` with the natural-language text, ordinary idempotency key, Slack provenance, and the existing Session. `AgentSessionFollowupDispatcher` selects the representative durable input, preserves the initial root, constructs a fresh Slack execution context, and dispatches the queued turn. It must not create a Manager-specific follow-up input.
- Claim and owner-authentication boundary messages are consumed by ingress. They do not create a SessionInput or turn.

Using the existing `ConnectionLaunchOrigin` and Session grain was chosen over a new `ManagerSession` aggregate because the required identities and recovery semantics already exist there. Keeping only the DM map as authority was rejected because it cannot independently prove the bound thread root, accepted input, or recovery state.

The Session metadata will also carry a non-secret Manager-origin marker (for example, `slack-manager`) distinct from the generic Slack source. This marker is control-plane data used to resolve Manager capability requirements; it is never included in Agent Instructions, user text, Slack system facts, or the collaboration Skill.

### 2. Reuse the versioned Slack context and keep Manager authority out of it

Manager initial launches, follow-ups, and recovered attempts will use `SlackExecutionContextFactory` and the existing v1 `AgentSlackExecutionContext`. The context contains the complete reply anchor and published collaboration Skill already required for Slack work:

- workspace, conversation, bound thread root, triggering message, initiating member, enrollment/connection identity, Session, and dispatch reference;
- the immutable Skill name, version, instructions, and content digest.

The Manager-origin marker is carried as a separate typed dispatch fact alongside `ExecutionSource = slack`. It is not added to the model-visible reply anchor. This keeps the existing Slack contract stable and prevents the Manager authorization model from leaking into ordinary Slack prompts.

For initial launches, the Server builds the context from the trusted `ConnectionLaunchOrigin` and pre-minted durable identities. For follow-ups and recovery, it resolves the workspace, enrollment, actor, conversation, and root from durable Session/input provenance, then creates a new dispatch reference and a new capability attempt. The Runner continues to reject incomplete Slack context and never relabels Manager work as non-Slack work.

Adding a Manager flag to the prompt or collaboration Skill was considered but rejected because it would make authorization facts model-visible and would require a new Skill version for a server-only concern. Reconstructing Manager context in the Runner was rejected because routing and origin authority belong to the Server.

### 3. Issue an ephemeral capability grant per execution attempt

Add a Server-owned `ManagerExecutionCapabilityIssuer` and capability-side validators. A grant contains an opaque random credential plus server-side claims for:

- immutable workspace and conversation origin;
- enrollment/Manager connection identity;
- authenticated Slack actor;
- current Agent Session;
- current dispatch/attempt reference; and
- issued-at and short expiry values.

The Server issues fresh route-scoped grants before every initial, follow-up, and recovered execution. The grants are held only in an expiring capability store or execution lease and are represented in durable state only by non-secret origin and dispatch references. Credential values are never written to EF/Orleans Session or AgentJob state. A restart invalidates unclaimed in-memory grants; recovery resolves the durable origin and mints a new grant pair.

Each route-scoped grant is passed as a transient, typed control-plane field to its matching Runner bridge, outside the AgentJob `with` payload and outside `FollowupParams` fields that become Runtime input. The Runner keeps each credential in memory and passes it only to the execution-scoped `mo` bridge for that audience. Neither credential is inherited by general shell commands, placed in process arguments, or exposed in the Runtime environment used to compose prompts. Direct calls to the normal management API without the management grant, or to the reply path without the reply grant, are not Manager capability calls.

The issuer creates route-scoped transient grants from the same execution attempt: a `manager-management` grant for management calls and a `manager-slack-reply` grant for replies. A turn that can use both routes receives both distinct opaque credentials; each is delivered only to its matching bridge, and a recovered attempt receives a fresh pair. Neither grant is a wildcard `mo` credential, and neither bridge forwards a request to the other. Each grant is validated before every call for signature/integrity, expiry, origin, actor, enrollment, Session, dispatch binding, and its exact audience. Validation is followed by a fresh `ManagerActorAccessDecider` check and, for management calls, target authorization; for reply calls it revalidates the current Manager enrollment, actor, Session, dispatch attempt, and injected Slack anchor. Revoking the actor, disabling/removing the enrollment, changing Manager availability, changing target ownership, or changing the current Session/dispatch therefore invalidates a still-live grant immediately. A failed check returns a bounded authorization/unavailable result and never calls an application or outbox service.

A self-contained bearer token with no active-attempt check was considered but rejected because a token could remain usable after the Server forgets an execution. Persisting token rows was rejected because it expands secret retention and recovery scope. The selected short-lived grant plus non-secret attempt reference preserves fail-closed restart behavior without creating durable Manager credential state.

### 4. Keep management capability and reply action disjoint

The Runner exposes two typed, execution-scoped routes over the existing `mo` surface. `ManagerManagementBridge` accepts a `ManagerManagementRequest` whose authoritative operation and argument mapping are defined in `specs/manager-command-capability/spec.md`; it accepts exactly the nine management operations and no reply action. `ManagerSlackReplyBridge` accepts only the exact `mo slack message send` action and never dispatches its request through `ManagerManagementBridge`. The normal CLI registration remains available for protected human/Agent use, but a Manager execution cannot use the generic CLI HTTP route as a capability shortcut.

The command capability spec is the single source of truth for the management request envelope and argument mapping. In summary, `list` and `diagnostics` take no model-supplied arguments and use the grant's Workspace; `view` takes a Project, target kind, and target id; `create` takes a Project, an existing Agent id or Agent name, an optional access policy, and a responsibility only when the name requires creating the Agent; `edit` takes a Project, Connection id, and one supported access policy; `enable`, `disable`, `claim-owner`, and `transfer-owner` take a Project and Connection id. Workspace, enrollment, actor, and owner identity come from the grant, not request arguments. The bridge rejects extra properties, route or HTTP selectors, database operations, shell commands, credential-bearing paths, and `mo slack message send` when presented to the management route.

`ManagerSlackReplyBridge` accepts the existing reply body mapping (`text`, `imageUrl`, or one file represented by `fileName` and `fileContentBase64`) for the exact `mo slack message send` action. The command's `conversation` and `reply-to` values are untrusted assertions used for CLI compatibility: the bridge compares them with the injected `AgentSlackExecutionContext.ReplyAnchor` and rejects missing or mismatched values, then calls the outbox service with the injected values. It never accepts a model-supplied Project, Connection, Workspace, Session, or dispatch identity. The effective route is fixed to `SlackDeliveryOwnerIds.ManagerProjectId`; `ConnectionId` is the grant's enrollment id; `WorkspaceTeamId`, `ConversationId`, and `ThreadTs` come from the validated anchor; and the outbox `OwnerKind` is `SlackDeliveryOwnerKinds.Manager`. The bridge validates the `manager-slack-reply` audience, current grant claims, active actor/enrollment, Session, and execution dispatch before enqueueing.

Reply idempotency is input-scoped rather than conversation-scoped. The bridge derives `manager-reply:{SessionId}:{TriggeringMessageId}` from the validated anchor and uses it as the final delivery intent's stable dispatch key across retries and recovered attempts; the grant's current attempt reference is still validated on each call but is not allowed to create a second answer for the same input. A repeated request with the same key and payload returns the existing intent. A different payload for an already-submitted key returns an idempotency conflict and creates no second intent. This prevents sequential turns in one DM from sharing a reply row while preventing duplicate sends for one input.

A structured `ManagerCommandResult` preserves delegated service outcomes instead of converting them to optimistic prose. The CLI and Manager management bridge share result mapping for current state, idempotent success, validation failure, conflict, not-found, and unavailable outcomes. The service layer owns target lookup and mutation; neither bridge accesses EF, grains, Slack credentials, or private HTTP handlers directly. Existing CLI/Web endpoint handlers should call those shared services where they currently contain operation-specific logic, so the Manager cannot drift from protected interfaces.

The current `SlackManagerToolAuthorization` rules can supply target-scope policy, but authorization must move before service invocation and be fed by current grant claims rather than assistant text. `SlackManagerToolExecutor` should be reduced to shared application operations or removed after the management bridge is in place. Directly adding a reply exception to that executor was rejected because it would make the exact management allowlist unenforceable. Exposing all existing CLI HTTP routes was rejected because route shape is not a capability boundary and would accidentally include secrets, deletion, and arbitrary management access.

### 5. Make reply authorship and liveness separate, shared projections

The updated collaboration Skill and Manager instructions will describe natural-language management through the management command calls and conversational replies through `mo slack message send`. The reply action is a separate `ManagerSlackReplyBridge`, not an allowlist exception. It reads the Server-provided anchor and uses the existing Slack outbox path only after validating the `manager-slack-reply` grant audience, current actor/enrollment, Session, dispatch attempt, and equality of any command-supplied destination assertions. The bridge fixes the Manager owner (`ProjectId = SlackDeliveryOwnerIds.ManagerProjectId`, `OwnerKind = manager`, `ConnectionId = enrollment id`) and derives the stable per-input dispatch key from Session plus triggering message, so sequential turns do not merge and duplicate sends do not create a second final intent.

`SlackManagerIngressService` will stop enqueueing "accepted" or other conversational messages. `SlackManagerToolTurnProcessor` will not send a separate owner instruction or synthesized result message; an owner command result is returned inside the current turn, and the Agent decides whether to send it through the reply action.

Manager liveness will use stable outbox dispatch references derived from the source message and turn. Ingress projects the canonical Received reaction, then the Working reaction when the turn is queued/executing. For Manager turns these states are reaction-only: no Server-authored "Working..." or acknowledgement message is created. Terminal handling removes Working when present and adds exactly one Completed reaction for `completed`, or one Attention reaction for `failed`, `cancelled`, and `unknown`. `SlackStatusProjection.FinalizeLivenessAsync` will be made idempotent even when a progress row is absent, so an accepted turn cannot finish with only Received or with Working left open.

The Agent reply outbox remains the only final text source. The Manager reply bridge calls a Manager-aware `SlackOutboxStore` entry point with the validated anchor and stable input dispatch key; it must not fall back to the generic conversation lookup that currently creates `OwnerKind = connection` rows. Repeated identical submissions reuse the Manager-owned intent, while conflicting submissions for one input fail closed. No assistant text, command result, terminal fact, or missing reply is promoted into Slack text by Server. A silent successful or failed turn is valid silence with only its liveness reaction.

Keeping the existing terminal-delivery Manager branch was considered but rejected because it requires parsing Runtime output and makes Server the conversational author. Replacing reactions with status messages was rejected because it violates the Manager liveness contract and creates duplicate acknowledgement/final-answer races.

### 6. Remove the model-output protocol and its recovery path

Delete the management-protocol behavior from `SlackManagerToolInvocation`, `SlackManagerToolTurnProcessor`, and the Manager-specific branch in `SlackTerminalDeliveryHandler`. The terminal handler will resolve Manager Session/source facts and call the common liveness finalizer; it will not call `Parse`, `ExecuteAsync` based on `assistantText`, `Render`, or `AcceptFollowupAsync` to synthesize a result turn. The old `SlackManagerToolExecutionFence` is no longer needed for behavior and can be retained as an unused schema during rollout before being removed in a later cleanup migration.

Replace the built-in `mohist-slack.instructions.md` JSON-only contract with ordinary command guidance, safe-operation rules, and the existing collaboration Skill. A legacy JSON object in assistant output becomes ordinary Runtime output: it causes no mutation, no synthetic input, and no Slack reply. Existing transcript text is not rewritten.

This deletion is intentionally not a compatibility parser. Keeping a parser "just in case" would preserve the ambiguous second protocol and allow arbitrary model text to mutate state.

### 7. Suppress managed Bot ingress before all Manager work

Keep `SlackManagedBotAdmissionService` as the first sender classification after message identity validation and before actor authentication, claim handling, inbox admission, Session routing, or liveness projection. A matching enrolled Manager Bot or eligible managed Agent App Bot returns a definite ignored result with no Inbox, SessionInput, AgentJob, Session, follow-up, reply, reaction, or progress record.

Only verified managed identity matches are suppressed. Missing, conflicting, or unregistered Bot identity retains existing non-managed ingress behavior; the receiving App id alone is not sufficient. This reuses the existing admission service rather than introducing a text-based bot filter.

## Risks / Trade-offs

- [Risk] A capability token can leak through a child-process environment, command output, exception, or diagnostic. -> Mitigation: keep it outside `work.with` and prompts, deliver it only through the Manager bridge, avoid process arguments and general shell inheritance, redact known capability values before logging, and add tests that inspect dispatch payloads, command results, terminal facts, and logs.
- [Risk] A Manager DM may have stale or incomplete provenance after an older launch or partial crash. -> Mitigation: resolve the durable Session/input root and representative message before dispatch, fail closed when the required Slack context is missing, and never fall through to non-Slack execution or a new parallel Session.
- [Risk] A valid grant could outlive a revoked actor or target authorization. -> Mitigation: reauthenticate and reauthorize on every call after validating the grant; grant expiry is an additional bound, not the authorization decision.
- [Risk] CLI/Web and Manager operations could diverge in validation or reported state. -> Mitigation: centralize operation services and structured result mapping, and test each allowlisted operation against the same service used by the protected CLI/Web route.
- [Risk] Duplicate Slack delivery or repeated reply actions could produce duplicate final answers or terminal reactions. -> Mitigation: use source-message/turn dispatch references, durable inbox idempotency, outbox merge/reconciliation, and idempotent reaction keys; test redelivery, restart, and adapter rebinding.
- [Risk] Existing Manager Sessions contain the old JSON instruction snapshot. -> Mitigation: identify Manager-origin Sessions by the non-secret origin marker and overlay the new built-in Manager instruction/Skill at follow-up and recovery dispatch; do not execute or parse legacy JSON output. New launches persist the new instruction version.
- [Risk] Reaction projection can fail after input acceptance and leave liveness incomplete. -> Mitigation: persist reactions as outbox intents, make terminal finalization retryable and idempotent, and add reconciliation coverage for completed, failed, cancelled, unknown, and recovered turns even when no Working intent exists.
- [Risk] A managed Bot identity can be incorrectly classified during enrollment changes. -> Mitigation: require the enrollment-backed identity match, preserve the non-managed path for absent/conflicting identities, and assert that ignored redelivery is side-effect free.
- [Risk] Removing the old protocol makes a mixed-version rollback unsafe. -> Mitigation: deploy the command bridge and new Manager assets before enabling new Manager execution, drain or disable Manager work during rollback, and keep the old fence schema available until the rollout is complete.

## Migration Plan

This change adds no durable credential table. The existing Manager Session, Inbox, DM mapping, Slack provenance, and AgentJob records remain the recovery source; only non-secret Manager-origin/dispatch markers may be appended to those contracts. The old execution-fence table may remain unused for one release and be dropped separately after rollback is no longer required.

1. Add the command capability service, the disjoint management and reply bridge contracts, grant issuer/validators with typed audiences, structured results, Manager-origin dispatch marker, Manager-owned reply routing, and focused tests. Extend CLI/Runner control contracts without exposing the credential in the generic `with` payload.
2. Deploy the upgraded Runner/CLI bridge to all Runners eligible for `mohist-slack`. The management bridge must reject every operation outside the nine-operation mapping, and the reply bridge must accept only `mo slack message send` with a current `manager-slack-reply` grant and the injected Manager anchor. Validate that the normal non-Slack envelope and existing protected CLI/Web credential paths are unchanged.
3. Deploy the new Manager instructions/Skill and Server ingress/session changes. New Manager launches emit ordinary Slack Session inputs, use the v1 Slack context, mint a grant per attempt, and project reactions. Existing Manager Sessions are recognized by their durable marker and receive the current Manager execution definition on follow-up/recovery; no legacy JSON output is executed.
4. Remove the old parser, synthesized follow-up, Server-authored Manager response, and terminal-delivery Manager branch. Treat any old `mohistManagerTool` text as inert Runtime output. Verify that claims remain ingress-consumed and that managed Bot events remain ignored before Inbox admission.
5. Run a reconciliation window covering pending Inbox entries, queued/executing Manager turns, recovered AgentJobs, and outbox rows. For every accepted input, confirm Received/Working convergence and one terminal reaction. Reissue grants for recovered attempts; never copy a prior grant into recovery state.
6. After the window, remove registrations and tests for `SlackManagerToolTurnProcessor`, `SlackManagerToolInvocation`, and the old fence behavior. Drop the unused fence table in a later schema cleanup if operational rollback no longer needs it.

Rollback must be coordinated across Server, Runner, and CLI. First stop admitting new Manager work and let active turns expire or reach terminal liveness, then disable strict Manager capability routing and wait for transient grants to expire. Only after no new-format Manager dispatch is in flight may the prior release be restored. The durable Session and DM mapping records can remain; they contain no capability secret. A partial rollback that restores the old Server while new Manager instruction/dispatch contracts are active is unsupported because it can recreate the removed model-output protocol or ignore the new command bridge.

## Open Questions

- The exact local transport used by the Runner-to-CLI bridge (protected inherited file descriptor, local socket, or equivalent process-scoped channel) must be selected during implementation; the invariant is that the Manager credential is unavailable to general shell processes and never appears in command arguments or persisted payloads.
- If Mohist is deployed with multiple active Server instances, the ephemeral grant store must use an existing non-durable shared capability mechanism or route each execution and its command calls to the same Server instance. The storage choice must not become a durable credential table.
- The exact TTL and maximum command duration for a Manager grant should be aligned with Runner execution deadlines and clock-skew tolerance; it must be short enough to bound replay and long enough for a normal turn.
- The old `SlackManagerToolExecutionFences` table can be removed in the same release only if the deployment rollback window has closed; otherwise it remains inert until the follow-up cleanup migration.
