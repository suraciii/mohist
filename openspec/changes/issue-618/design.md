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
- Reauthorize the current actor, enrollment, workspace, and target on every command call. Keep secrets, credential reads, permanent deletion, binding removal, arbitrary API access, and direct database access outside the capability.
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

Add a Server-owned `ManagerExecutionCapabilityIssuer` and a command-side `ManagerCapabilityValidator`. A grant contains an opaque random credential plus server-side claims for:

- immutable workspace and conversation origin;
- enrollment/Manager connection identity;
- authenticated Slack actor;
- current Agent Session;
- current dispatch/attempt reference; and
- issued-at and short expiry values.

The Server issues a new grant before every initial, follow-up, and recovered execution. The grant is held only in an expiring capability store or execution lease and is represented in durable state only by non-secret origin and dispatch references. The credential value itself is never written to EF/Orleans Session or AgentJob state. A restart invalidates unclaimed in-memory grants; recovery resolves the durable origin and mints a new grant.

The grant is passed as a transient, typed control-plane field to the Runner command bridge, outside the AgentJob `with` payload and outside `FollowupParams` fields that become Runtime input. The Runner keeps it in memory and passes it only to the execution-scoped `mo` bridge. The bridge uses the credential for the child CLI/API call and clears it when the command returns; it is not inherited by general shell commands, placed in process arguments, or exposed in the Runtime environment used to compose prompts. Direct calls to the normal management API without the grant are not Manager capability calls.

The grant is validated before every operation for signature/integrity, expiry, origin, actor, enrollment, Session, and dispatch binding. Validation is followed by a fresh `ManagerActorAccessDecider` check and target authorization. Revoking the actor, disabling/removing the enrollment, changing Manager availability, or changing target ownership therefore invalidates a still-live grant immediately. The validator returns a bounded authorization/unavailable result and never calls an application service on a failed check.

A self-contained bearer token with no active-attempt check was considered but rejected because a token could remain usable after the Server forgets an execution. Persisting token rows was rejected because it expands secret retention and recovery scope. The selected short-lived grant plus non-secret attempt reference preserves fail-closed restart behavior without creating durable Manager credential state.

### 4. Use one narrow command bridge and centralize authoritative results

The Agent will invoke the existing `mo` command surface. The CLI command parser remains responsible for normal argument parsing and output formatting, while a Manager execution bridge supplies the execution credential and constrains the server request to the logical allowlist. The bridge will reject unknown command names and requests that attempt to select an arbitrary endpoint, HTTP method, database operation, or credential-bearing route.

The command implementation will expose these logical operations:

- `list`: workspace Manager status;
- `view`: Agent or Connection inspection;
- `create`: existing Manager create/mount semantics;
- `edit`: supported routine Connection settings only;
- `enable` and `disable`: routine Connection lifecycle state;
- `claim-owner` and `transfer-owner`: existing one-time owner workflows, returning only the safe instruction and expiry needed to continue;
- `diagnostics`: authoritative status and next action.

A structured `ManagerCommandResult` will preserve the delegated service outcome instead of converting it to optimistic prose. The CLI and Manager bridge will share the same result mapping for current state, idempotent success, validation failure, conflict, not-found, and unavailable outcomes. The service layer will own target lookup and mutation; the bridge will not access EF, grains, Slack credentials, or private HTTP handlers directly. Existing CLI/Web endpoint handlers should be refactored to call that service where they currently contain operation-specific logic, so the Manager cannot drift from protected interfaces.

The current `SlackManagerToolAuthorization` rules can supply the target-scope policy, but authorization must move before service invocation and be fed by the current grant claims rather than assistant text. `SlackManagerToolExecutor` should be reduced to shared application operations or removed after the bridge is in place.

Directly adding more methods to `SlackManagerToolExecutor` was considered but rejected because it preserves a Server-only protocol and its handcrafted result strings. Exposing all existing CLI HTTP routes was rejected because route shape is not a capability boundary and would accidentally include secrets, deletion, and arbitrary management access.

### 5. Make reply authorship and liveness separate, shared projections

The updated collaboration Skill and Manager instructions will describe natural-language management through command calls and conversational replies through `mo slack message send`. The reply action reads the Server-provided anchor and uses the existing Slack outbox path. Its internal request metadata will bind the send to the current Manager grant/Session/dispatch so the Server can validate the supplied conversation and thread and coalesce duplicate sends into one final delivery intent.

`SlackManagerIngressService` will stop enqueueing "accepted" or other conversational messages. `SlackManagerToolTurnProcessor` will not send a separate owner instruction or synthesized result message; an owner command result is returned inside the current turn, and the Agent decides whether to send it through the reply action.

Manager liveness will use stable outbox dispatch references derived from the source message and turn. Ingress projects the canonical Received reaction, then the Working reaction when the turn is queued/executing. For Manager turns these states are reaction-only: no Server-authored "Working..." or acknowledgement message is created. Terminal handling removes Working when present and adds exactly one Completed reaction for `completed`, or one Attention reaction for `failed`, `cancelled`, and `unknown`. `SlackStatusProjection.FinalizeLivenessAsync` will be made idempotent even when a progress row is absent, so an accepted turn cannot finish with only Received or with Working left open.

The Agent reply outbox remains the only final text source. It may update or create the final delivery intent through `SlackOutboxStore.EnqueueAgentReplyAsync`; stable dispatch references and existing merge/reconciliation rules enforce at most one final text reply for an input. No assistant text, command result, terminal fact, or missing reply is promoted into Slack text by Server. A silent successful or failed turn is valid silence with only its liveness reaction.

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

1. Add the command capability service, grant issuer/validator, bridge transport, structured results, Manager-origin dispatch marker, and focused tests. Extend CLI/Runner control contracts without exposing the credential in the generic `with` payload.
2. Deploy the upgraded Runner/CLI bridge to all Runners eligible for `mohist-slack`. The bridge must reject Manager calls without a valid transient grant and must advertise a capability used by Server dispatch routing. Validate that the normal non-Slack envelope and existing protected CLI/Web credential paths are unchanged.
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
