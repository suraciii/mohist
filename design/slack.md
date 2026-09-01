# Slack

The Slack integration connects an already configured Mohist Agent to a Slack
Workspace under an independent identity. Slack is an interaction surface;
Mohist remains authoritative for Agents, work, Sessions, and results.

Product behavior is defined in [`../docs/slack.md`](../docs/slack.md) and not
repeated here. See [`agent-api.md`](agent-api.md) for the unified invocation
boundary. This document records only component boundaries and the decisions
that must remain true.

The integration has two layers that share authoritative Server state but not
responsibilities: the **data plane**, where a Connection provides one Bot with
a local Socket Mode channel, and the **control plane**, where a
Workspace-level Mohist App installs and operates each Agent App.

## Core Decisions

Each entry states one decision; the body below carries the rules.

- **Agent first.** A Connection is only another ingress for an independent
  Agent; Slack is never a prerequisite for execution.
- **One Agent, one App, one Bot, one Workspace.** The visible identity tells
  users which Agent they invoke; a shared Bot never guesses.
- **Server owns the control plane.** `mohist-slack` handles only the Socket
  wire protocol; App creation, installation, and credential verification are
  recoverable product facts.
- **Separate adapter process.** A static Go binary owns Socket Mode as a
  failure and dependency boundary, off the Runner's Node runtime.
- **Adapter persistence: none.** A process boundary is not a state boundary.
- **Ingress, conversation mapping, and outbound delivery live in Server**, in
  the same backup boundary as Sessions. No dual authority, no cross-process
  unknown outcomes.
- **One outbox, one sender.** Only an adapter holding a valid Socket lease may
  claim; one sender per App at a time prevents duplicate delivery.
- **No second execution definition.** A Connection stores no Instructions,
  Runtime, Model, or Skills of its own.
- **Access control is separate from capability.** A Connection decides who may
  invoke; it neither reduces nor expands execution capability.
- **Native conversation mapping.** A channel root mention starts a Session and
  thread replies continue it; a DM is one continuing Session.
- **Never drop accepted input.** Slack may redeliver; Mohist deduplicates and
  retains accepted input. Capacity is never recovered by dropping messages.
- **Socket Mode only.** Each App has independent Bot and App-level tokens;
  self-hosting needs no public ingress and no Mohist-hosted control plane.
- **Credentials through protected local input.** A local deployment has no
  HTTPS OAuth callback; never request a Slack login session.
- **Web is a fallback surface** for configuration, diagnostics, and takeover,
  never a required workstation.
- **The Mohist App is an ordinary Agent.** It manages resources through the
  ordinary command surface under an operator-bound capability credential: one
  installation semantics, two entry points.
- **Effects only through explicit commands** — the reply action and the `mo`
  CLI. Server never parses model output into commands; model text is
  reasoning, not protocol.
- **Installation DSL: `mo slack setup` / `mo slack install-agent <agent>`.**
  `setup-agent` conflicts with Agent Readiness setup; `create` falsely claims
  creation when the user is installing an Agent into Slack.
- **Conversational creation asks at most for name and daily responsibility**,
  creates a real Agent with defaults, then guides Slack installation. A Mohist
  App DM is already an authorization boundary; no draft approval state.
- **Server chooses the reply target** and injects a reply anchor with the
  input; the model never guesses a thread from memory.
- **Recovery preserves the current reply target.** Before dispatching a
  replacement execution, the retry receipt durably records the current Slack
  input's reply anchor. The failed input remains execution lineage, but cannot
  become the replacement reply destination. The first receipt for a failed
  Session Turn fixes that anchor; concurrent ingress and pending recovery reuse
  the winner instead of overwriting it.
- **Signed action buttons, no slash commands.** Buttons reuse one verified
  mechanism and the same operations as CLI and Web; slash commands are a
  third grammar that forces manifest changes and reinstalls.
- **Liveness projects only real state-machine facts.** Timers are cleanup
  backstops, never narrative sources.
- **New input on an active Session defaults to Steer**; only explicit Stop is
  Interrupt. Ordinary messages must not abort long work.
- **Collaboration rules ship as a built-in Skill**: no empty acknowledgement,
  callback after delegation, silence by default, self-contained replies, no
  guessed reply location.
- **Slack carries liveness and final replies; the Web Session timeline carries
  process detail.** Separate signals, separate homes.

## System Boundary

```text diagram
            +--------------+        +---------------------+
            | Slack member |        | Slack control plane |
            +-------+------+        |      (Server)       |
                    |               +----------+----------+
                    |                          |
                    vmessage / action          vmanages
           +-----------------+   +--------------------------+
           | Slack App / Bot |   | SlackWorkspaceEnrollment |
           +--------+--------+   +-------------+------------+
                    |                          |
                    vSocket Mode               vmanages
            +--------------+       +----------------------+
            | mohist-slack |       | ManagedSlackAgentApp |
            +-------+------+       +-----------+----------+
                    |                          |
                    v                          vreferences
           +----------------+         +-----------------+
           | Server ingress |         | AgentConnection |
           +--------+-------+         +-----------------+
                    |
                    v
         +---------------------+
         | Connection boundary |
         +----------+----------+
         +----------+-----------+
         v                      v
   +-----------+  +--------------------------+
   | Agent API |  | provider inbox / mapping |
   +-----+-----+  |         / outbox         |
         |        +--------------------------+
         |
         v
+-----------------------+
| Agent / Job / Session |
+-----------+-----------+
         |
         v
    +--------+
    | Runner |
    +--------+
```

- **Slack** owns member identity, channels and message interaction, and event
  and reply transport. Not: Agent configuration, execution, work results.
- **`mohist-slack`** owns translation between Socket Mode and normalized
  ingress / delivery intent, plus short leases granted by Server. Not:
  persisted state, thread ownership, Agent execution, work-state arbitration,
  App creation or installation.
- **Server Connection boundary (data plane)** owns provider identity and
  access decisions, durable ingress, conversation mapping, pending delivery,
  and Agent API calls. Not: Slack wire payloads, Agent execution, result
  arbitration.
- **Server Slack control plane** owns Workspace enrollment, external App
  lifecycle and authorization, manifests, credential references, and audit.
  Not: Agent execution, thread ownership, the wire protocol.
- **Agent API** owns unified start, continue, observe, and stop. Not: Slack
  mentions, threads, member directory, provider rate limits.
- **Runner** owns execution from the resolved Agent definition. Not: Slack
  identity, access policy, thread routing.

One `mohist-slack` process per Server carries the Socket connections for the
Mohist App and every Agent App; each App keeps independent credentials. Once
an App is ready, the adapter obtains a short lease and runtime credentials,
then establishes or restores its Socket.

### Mohist App Conversational Form

The control plane appears in Slack as the **Mohist App**, implemented by the
built-in Agent `mohist-slack`: a Server-reserved name ensured by `mo slack
setup`, outside the Project namespace, not subject to ordinary archival or
deletion. Every management operation targets existing resources (Agent,
AgentConnection, SlackWorkspaceEnrollment, ManagedSlackAgentApp); it creates
neither a second management model nor a second execution path. `mohist-slack`
is also the adapter process name; the shared name couples nothing.

The Mohist App uses the same data plane as Agent Apps, but its access decision
is fixed to a Mohist operator authorized to manage the target resource, never
a Connection's Owner/Allowlist/Anyone policy. High-risk actions such as
permanently deleting a Slack App are unavailable in conversation.

Every Mohist App DM is a normal Agent Session and Turn with the same reply
action, outbox, and liveness projection. The built-in Agent runs management
operations as ordinary tool calls from its Skill and composes the reply from
their results. Server never parses model output for management requests,
synthesizes follow-up inputs on the Agent's behalf, or renders model text
into Slack messages.

Management authority is bound to the Session origin, not to model text. When a
Manager Session launches, Server recovers the operator from the Session's
immutable Slack origin, verifies management rights, and issues a capability
credential scoped to that operator and Enrollment. The credential is injected
into the execution environment and never enters Instructions, prompts,
transcripts, durable rows, or logs. The management surface reauthorizes the
operator against the target resource on every call and delegates to the same
application services as the CLI. The credential excludes secret-bearing steps
and irreversible lifecycle operations: credential submission stays in the
local CLI; permanent delete stays in Web or CLI with explicit confirmation.

Owner claim remains a Server-consumed boundary operation at ingress; it is
never forwarded to the Agent.

### Why the Adapter Is Stateless

The separate process exists for language-ecosystem reasons, not state
ownership. An adapter that persisted thread mappings or pending delivery would
add a second recovery model, a second backup object, and states such as
"Server says sent, adapter says unsent."

Ingress acknowledgement:

```text diagram
+-------+   +--------------+             +--------+
| Slack |   | mohist-slack |             | Server |
+---+---+   +-------+------+             +----+---+
    |               |                         |
    |provider event |                         |
    +-------------->|                         |
    |               |                         |
    |               |   normalized envelope   |
    |               +------------------------>|
    |               |                         |
    |               |definite accept / reject |
    |               |<------------------------+
    |               |                         |
    |  acknowledge  |                         |
    |<--------------+                         |
    |               |                         |
+---+---+   +-------+------+             +----+---+
| Slack |   | mohist-slack |             | Server |
+-------+   +--------------+             +--------+
```

An unknown result means no acknowledgement; Slack redelivers under the same
identity. A Slack acknowledgement means only that Mohist durably took
responsibility for the provider event, not that user input became
SessionInput.

- **Ingress:** the adapter submits a normalized envelope with stable provider
  identity; Server decides quickly to ignore, reject, or durably accept. The
  adapter never waits for thread history, attachments, or Agent API before
  acknowledging.
- **Outbound:** Server stores a bounded delivery intent, never a wire payload.
  The adapter claims one item, renders and sends it, and reports the result.
  An unconfirmable result is recorded and displayed by Server; nothing
  suspended remains in the adapter.
- **Restart:** the adapter reconstructs nothing. After reconnecting it claims
  unconverged deliveries. It never caches events while Server is down: a cache
  cannot turn a message into accepted input, only into another recovery model.
  Slack's redelivery window is the fallback; beyond it, the user resends.

The adapter limits transient concurrency only. Capacity checks happen in
Server: provider-inbox capacity before acknowledging ingress, Session-input
capacity on admission, a bounded outbound outbox. Replaceable unsent progress
may coalesce; final results, explicit failures, and user actions are never
dropped silently. If they cannot fit, the Connection becomes Degraded
(Backpressured) and stops accepting new Slack input.

## Connection in the Domain

AgentConnection belongs to the Agent domain: its binding, access policy, and
lifecycle are durable product behavior. Provider inbox, conversation mapping,
and pending delivery are integration records owned by Server infrastructure,
not business facts of AgentConnection or AgentSession. Only the Socket, the
current request, and the active send call are transient adapter state. A
Connection references an Agent without copying its execution definition.

### Staged Binding for `install-agent`

A Connection exists before Agent App creation, so the installation record has
a stable durable target before the first uncertain external write. External
identity is added only after installed credentials pass verification:

- `AgentId + WorkspaceTeamId` are immutable after Connection creation.
- `AppId + BotUserId` change atomically exactly once, from both empty to both
  non-empty; Team, App, and Bot are immutable afterward.
- Partial binding, team rebinding, and a second App/Bot binding are forbidden.
- One Project/Agent/team has at most one non-deleted Connection.

One application boundary enforces staged creation and identity completion for
every caller; generic Connection edits cannot mutate identity fields.

A Connection expresses four independent facts: external installation progress,
operator desired state (Enabled/Disabled), Slack connection health, and Agent
Readiness. `Connected` cannot replace them: a Connection may be connected
while its Agent is `needs-setup`, or the Agent `ready` while Slack is offline.
Views may expose all four, but a summary highlights one current state and
exactly one next action.

## Slack Control Plane

Two aggregates in Server's Slack integration context own durable product facts
about external Apps, with independent reasons to change and fail: Enrollment
is one Workspace's ability to provision and operate Apps; AgentApp is one
external App and its irreversible side effects; Connection is whether one
Agent identity may be invoked. A Configuration-token outage must not disable
an installed Bot; deleting a Connection must not erase an external App record;
a Socket failure must not rewrite Agent configuration.

### SlackWorkspaceEnrollment

One Workspace-level aggregate; by default its key has **no Project**: one
Workspace Mohist App is a Server-installation control plane that multiple
Projects may reference. Project isolation requires a product-spec change
first. Within one Server installation, an active `team_id` resolves to at
most one Enrollment; two active records would create competing provisioning
authorities.

It owns: stable `team_id`, Mohist App external identity and lifecycle, the
capability to manage Agent Apps with last-verification facts, credential
references (never plaintext), and audit facts. It owns neither Agents,
Connections, nor Agent Apps, and does not turn Slack members into Mohist
administrators.

### ManagedSlackAgentApp

One aggregate per managed Agent App. `install-agent` is an application
operation, not an aggregate: it coordinates App creation, installation
authorization, and Mohist binding, while the aggregate stores only the App's
own external facts. AgentApp references its target Connection but is not its
child, and the two never change in one transaction: cross-aggregate binding
advances as `AgentApp commits fact -> durable handler -> idempotent Connection
command`. One external `app_id` belongs to at most one AgentApp in its
Workspace; one AgentApp references only one Connection.

It owns: `enrollment_id`, stable Agent App ID and external `app_id`, desired
and applied manifest version with verified scopes, App create/delete and
installation facts, operation fence, unknown outcome, error classification,
and audit. Slack create/delete is an external side effect of AgentApp itself,
so its fence stays in AgentApp; no separate process-manager aggregate (see
[`architecture.md`](architecture.md#durable-application-process-manager)).

### Four-Axis State and One Next Action

AgentApp state is not one enum; it is four axes plus one derived next action:

```text literal
App lifecycle:  not-created -> creating -> created -> deleting -> deleted
                uncertain exits: create-unknown, delete-unknown
Authorization:  not-started -> awaiting-user -> pending-admin -> authorized
                terminal exits: expired-or-cancelled, revoked
Manifest:       desired / applied / drift-known
Socket ready:   both credentials persisted, both identities verified,
                adapter lease alive; missing either credential forbids ready
```

An unknown state is left only through reconciliation or explicit human
arbitration; a process restart never repeats create/delete automatically. A
definite failure starts a new attempt on the same AgentApp, never a new
Connection or Bot target. Cancelled installation, expired authorization, and
pending approval all resume the same AgentApp.

### Credential Ownership

Credentials are addressed by their actual owner; a Connection neither owns nor
copies Agent App runtime credentials:

- Mohist App runtime credentials live at the Enrollment address as an opaque
  persisted reference. Bot token and App-level token are distinct secret kinds
  under one owner reference. `mo slack setup` is the only normal provision,
  repair, and rotation entry point; repeated setup resumes one record.
- Agent App client/signing secret, App-level token (`xapp-`), and Bot token
  (`xoxb-`) live at the AgentApp address.
- A Connection obtains data-plane credentials only through an active AgentApp
  binding.

Removing a Connection does not delete its Slack App by default, so credentials
addressed by Connection would couple two independent lifecycles.

The secret-provision endpoint accepts only operator-authenticated loopback
requests; the caller cannot supply a credential address. Credentials come from
hidden CLI input or a protected, user-owned file. Responses, status, errors,
logs, audit, and documentation examples contain no credentials. Status exposes
Bot and App-level provisioning and verification separately. The Mohist App
becomes `ready` only after both are valid and Socket hello is confirmed.

Credential submission invariants:

- Bot and Workspace verification succeeds before any App-level token write.
- An App-level token is written only as an unverified candidate; a validation
  lease accepts no business traffic.
- Binding and a runtime lease are granted only after Socket App-identity
  verification: never "Connection bound and usable, token not persisted."
- Repeating the same verified credential set returns the same result without
  rebinding.
- A candidate for a different App/team/Bot is deleted and remains unusable.

### App Provisioning Credentials (Configuration Token)

App management uses one Workspace-level Configuration access/refresh pair,
stored at the Enrollment address. It is separate from Mohist App runtime
credentials: the first authorizes creating and maintaining Apps, the second
messaging as the Bot. The two are never mixed or derived.

- **One provisioning path.** Setup guides the user to create the pair in
  Slack's App management page and submit it once through protected input;
  documentation never assumes a Slack CLI exists.
- **Rotation is reactive and transparent.** On access-token expiry, Server
  rotates with the refresh token and atomically replaces the pair and provider
  `team_id`. An unknown rotation result is marked `credential-rotation-unknown`
  and requires a new pair from the user; blind retry is forbidden. Degraded
  begins only when the refresh token is also invalid. Rotation failure never
  interrupts the Socket data plane of installed Apps.
- **Invalidation** appears as authentication failure on an App-management
  call: Enrollment capability becomes Degraded with one next action (rerun
  `mo slack setup`), while existing data planes keep working. External failure
  is never amplified with automatic retries.
- **Audit.** Every external write records actor, object, and result, never
  the token.

The control plane reaches Slack HTTPS through four narrow capability ports:
credential rotation, manifest management, Bot identity verification, and
member identity lookup. Only Allowlist/Anyone admission calls the
member-identity port. Socket operations belong only to `mohist-slack`; Server
implements no second WebSocket client to verify `xapp-`.

### `setup` / `install-agent` Orchestration

CLI and Mohist App call the same Server application service. On each call the
service reads current aggregate facts, performs at most one unconfirmed
external write, and returns complete progress with one next action. Ordering
invariants:

- The first `mo slack setup` obtains the provider-confirmed `team_id` from a
  successful Configuration-token rotation and uses it as the idempotency key.
  No App is created while a rotation result is unknown.
- Returned `app_id`, client credentials, and installation link are persisted
  before any user-visible link is exposed. An unknown create outcome persists
  the operation fence and stops; it is never resent.
- A runtime credential is verified only after the adapter reports the expected
  App's first Socket hello under a validation lease; on mismatch the candidate
  is deleted and the Connection stays unbound.
- AgentApp then commits a bindable fact, and a durable handler idempotently
  fills Connection App/Bot identity. Installation projects `ready` only after
  the adapter first obtains a runtime lease.
- A rerun repairs drift, missing or invalid credentials, and connection
  without creating another Connection or AgentApp. Reprovisioning valid
  credentials on a `ready` record rotates them, but they must resolve to the
  same team/App/Bot identity.

The `install-agent` idempotency key is `(enrollment_id, AgentId)`. The
conversational operation performs only the non-secret steps and returns the
same progress; at a secret step it provides the link and the local
continuation command. Chat text is never a secret-input channel.

### Canonical Manifests

Manifests are canonical, versioned, and drift-detected: hashing covers
manifest version, product capability version, and identity snapshot, and
Slack's true-or-omitted Boolean round-tripping must not create false drift.
The exact scope set is canonical in code; the product document lists each
permission with its reason. Interactivity returns through Socket Mode and has
no Request URL.

### Socket Leases and Adapter Discovery

Server grants two short leases. A **validation lease** allows one Socket with
a candidate App-level token to report `hello.app_id`; it accepts no ingress
and claims no outbox work. A **runtime lease** is available only to a
credential-verified active App whose Connection is Enabled. `mohist-slack`
discovers targets and renews through operator-authenticated loopback
transport. Only a lease response may contain a secret.

When the adapter disconnects or a lease expires, Server stops granting it
delivery intents; a new adapter takes over only after the old lease expires.
Mohist App runtime ingress routes to the management actor, not to a Connection
access policy.

A lease pins its credential generation by fingerprint at issue time. After a
candidate is reprovisioned or a verified pair rotates, the old lease fails
closed: renew is rejected, hello returns stale, and a hello from an old token
must not reject or delete the new candidate. A failed acquisition leaves no
inert active lease and does not displace the existing holder.

Every Socket envelope validates `api_app_id + team_id` against its target
before admission. An unknown App/team is acknowledged and rejected, never
routed by Bot name.

## Session Boundary

Slack organizes one conversation as a thread, so Mohist uses `Agent + thread`
as the Session boundary instead of sharing one context forever across a
channel. Three rules:

- One thread may contain multiple AgentSessions, one per Agent. Mentioning a
  new Agent neither switches nor contaminates the original Session.
- Do not start work when ownership is ambiguous. A multi-Bot mention, or an
  unmentioned reply in a multi-bound thread, gets an interactive chooser
  instead of a guess.
- Do not start work when the target Agent cannot run it. Server posts one
  guidance message — safe summary for the caller, specific gap only for Owner
  and operators — without creating a Session, Turn, or queued input,
  deduplicated per triggering message.

DM is the exception: Slack DM users do not organize work by thread. Server
stores one current AgentSession per Connection DM conversation; every normal
message continues it. `new task <prompt>` is the explicit opt-in to an
independent AgentJob and Session; infrastructure recovery must never require
that grammar. Parallel work can use separate channel threads.

DM continuation is fail-closed:

```text diagram
                              +----------------+
                              | new DM message |
                              +--------+-------+
                                       |
                                       v
                              +----------------+
                              | binding state? |
                              +--------+-------+
       +---------------------+---------+----------+-------------------+
       vlaunch pending       vretry-safe terminal vidle + missing     vactive / unknown / stale
+-------------------+   +---------------+   +-----------------+  +--------------+
| ordered follow-up |   | durable retry |   | replace Session |  | never replay |
+-------------------+   +---------------+   +-----------------+  +--------------+
```

The inbox route is the crash-recovery fence: a retry must resolve its durable
replacement before route migration, the conditional route update prevents a
concurrent redelivery from overwriting a newer Session, and the follow-up's
stable Slack idempotency key prevents duplicate SessionInput records after the
migration. Different Mohist Servers never share thread routing.

## Reliability Contract

Slack-to-adapter transport is externally at-least-once; the system cannot
claim end-to-end exactly-once.

- Deduplication occurs in Server: the same Slack message identity that became
  input always resolves to the same SessionInput.
- Provider inbox and SessionInput deduplicate separately; redelivery after a
  lost request result accepts neither twice.
- Accepted input cannot be deleted through drop-oldest or similar policy;
  reject new input when capacity is exhausted.
- The outbound outbox is bounded; the replaceable Session card may coalesce;
  final results, explicit failures, and user actions never disappear silently.
- Slack delivery failure never changes AgentJob or AgentTurn results.
- Messages authored by a Mohist Bot are rejected at admission before any
  durable record: an Agent can never trigger itself or another Agent through
  its own replies.
- A long outage may exceed Slack event retention. After recovery, display that
  a gap may exist.

Control-plane create/delete is likewise at-least-once: a repeated attempt does
not repeat App creation/deletion, and an unknown result converges only through
reconciliation or human arbitration under Four-Axis State.

### State Projection and Message Identity

Server is the sole judge of AgentSession and AgentTurn state; provider and
adapter project only Server-confirmed state and cannot infer success from a
Slack API response or Runner output.

An accepted input uses stable message identity as input identity and derives
one dispatch reference for the work item. Per dispatch reference, Server
allows:

- At most one replaceable Session-card projection, persisted with its provider
  message identity. Its text is a stable Session reference; its blocks carry
  navigation and state-bound controls. It never carries Agent-authored text.
- At most one terminal Agent reply, deduplicated by a stable Turn delivery key.
  It is a separate message in the same thread and never depends on the Session
  card's Pending, Claimed, Delivered, or Delivery uncertain state.
- Fast work may omit the Session card and project only the Received reaction
  plus one final answer. If the platform cannot react on the user's message,
  the Received fallback remains a Server-authored receipt.

Default reactions: `Received=👀`, `Working=⏳`, `Completed=✅`, exception `⚠️`.
Reactions are liveness signals, not Session/Turn facts; a missing, late, or
successful reaction mutation never changes Server state.

### Reply Body Belongs to the Agent; Liveness Belongs to Server

- **Server owns liveness and the Session card.** Reaction mutation is
  best-effort: a bounded, logged failure never blocks or fails a Turn. The
  Session card contains only a stable Session reference, navigation, and
  state-bound controls; it is not a textual execution status. Once queued, it
  is never promoted into or overwritten by a terminal reply or failure notice.
- **The Agent owns the reply body.** During a Turn it sends what it wants to
  say through the **reply action**: an intent API to Server carrying body and
  reply anchor. The reply action enters the same outbox and reuses redaction,
  duplicate protection, anchor validation, and uncertain-result
  reconciliation. The Agent never connects directly to Slack. The final answer
  is independent from the Server-authored Session card.

Server never extracts assistant text from Runner Turn output; the reply action
is the only reply source. Terminal handling owns reaction closeout for every
session kind: every accepted input reaches Completed or attention on every
outcome — completion, failure, cancellation, Agent crash, or Server restart.
The outbox persists the closeout intent, while the neutral Session card remains
a valid observation entry instead of a stale `Working...` claim.

A system-authored fallback for Agent crash or complete non-response is a
separate outbox message with its own stable dispatch reference. A retry action
may be attached to that fallback, but the fallback never reuses the Session
card's provider message identity and never replaces its navigation surface.

- **Silence is valid.** A Turn that ends with no reply action closes liveness
  normally; Server invents no status summary.
- **The Agent reports failure.** On execution failure or required human
  action, the Agent sends reason and next step through the reply action. Only
  an Agent process crash or complete non-response permits a system-authored
  fallback explicitly labeled as a system failure.
- Detecting a Turn that ended without publishing belongs to the Runtime as an
  advisory reminder to the model, never to Server, which neither synthesizes
  the missing reply nor treats silence as failure.

### Reply Action CLI: `mo slack message send`

The reply action command surface is `mo slack message send`. A Connection
Agent reply is anchored-only: the CLI requires the complete reply anchor
before it contacts the Server.

```text literal
mo slack message send --workspace <workspace-id> --conversation <id> --reply-to <ts> --connection <connection-id> --session <session-id> --triggering-message <message-id> --dispatch-ref <ref> --text "<body>"
printf 'long body\n\nmultiple paragraphs\n' | mo slack message send --workspace <workspace-id> --conversation <id> --reply-to <ts> --connection <connection-id> --session <session-id> --triggering-message <message-id> --dispatch-ref <ref> --text -
mo slack message send --workspace <workspace-id> --conversation <id> --reply-to <ts> --connection <connection-id> --session <session-id> --triggering-message <message-id> --dispatch-ref <ref> --text "see this screenshot" --file ./screenshot.png
mo slack message send --workspace <workspace-id> --conversation <id> --reply-to <ts> --connection <connection-id> --session <session-id> --triggering-message <message-id> --dispatch-ref <ref> --text "architecture diagram" --image https://example.com/diagram.png
```

- **Explicit destination.** All anchor flags are required; an anchor-less or
  partial send is refused by the CLI with no HTTP request and the missing
  fields listed. The Agent reads these values from the injected reply anchor
  instead of choosing a destination from memory.
- **Explicit ownership and dispatch identity.** Server validates the complete
  anchor against durable Session provenance and its Turn before it scopes
  terminal reply selection and coalescing to that logical Turn. Distinct sends
  are accepted only while the Turn is active; an identical retry after
  terminal completion still returns the committed delivery intent.
- **Manager separation.** Manager-mode sends use the dedicated Manager reply
  route and its credential/origin contract, separate from this anchor
  requirement.
- **Body through `--text`.** A string, or `-` for stdin so shell escaping does
  not consume newlines. The Agent writes standard Markdown; the renderer
  converts it to Slack mrkdwn and degrades unsupported tables and headings to
  readable text without error.
- **Mentions in body.** The CLI resolves `@displayname` to a valid Slack
  mention; ambiguity or an invisible target returns an actionable error and
  sends nothing.
- **Images.** `--file` uploads a local image; `--image` references a public
  URL. An Agent may explicitly send a useful screenshot or artifact.
- **Success means outbox acknowledgement.** Send synchronously commits to the
  outbox, confirming that reliable delivery was accepted, not that Slack
  displayed it. Eventual Slack failure surfaces as Delivery uncertain without
  reporting back into Agent execution. A different payload cannot mutate a
  claimed or delivery-uncertain intent and is rejected instead of
  acknowledged.
- **Multiple sends coalesce.** Within one Turn, Server coalesces sends into
  one final answer for that dispatch reference. An extension that would exceed
  Slack's single-message limit is rejected rather than duplicated through
  overflow posts. The invariant remains at most one final answer per input.
- **Scope.** No broadcast across channels and no distinct message types.
  `mo slack message` is a command group; only `send` is implemented.

### Signed Action Buttons

Slack interactivity reaches Server as `block_actions` provider interactions.
Every interactive control carries a Server-signed payload bound to Connection,
conversation, actor Slack identity, target resource, and expiry. Server
revalidates signature, freshness, and actor authorization on every click; a
stale, foreign, or expired action is rejected with a visible notice. An
accepted click enters the durable provider inbox like any other input and
delegates to the same application service the equivalent CLI or Web call uses.
Buttons are shortcuts to existing operations, never a second command grammar:
they carry no free text, and their effects are exactly the effects of the
operation they name.

Current actions: Stop a queued or running Turn, Retry from a failure notice,
and Agent selection for a multi-Bot mention. The chooser preserves the
original message facts and starts exactly one execution under the selected
Connection's owning Project. Mohist approval gates are not Slack actions;
routing them into Slack requires a notification routing policy.

### Delivery Intent, Claim/Ack, and Unknown Results

Server persists one delivery intent per logical projection, carrying the
Connection, dispatch reference, target conversation/thread, projection kind, a
stable deduplication key, the current provider message identity if known, and
a replayable content reference. Projection kinds: replaceable Session card,
terminal Agent reply / explicit failure, user action, reaction mutation. Tool
calls and Runner logs are not user messages.

```text diagram
                         +---+
                         | * |
                         +-+-+
                           |
                           v
                      +---------+
                      | Pending |<---------------------------+
                      +----+----+                            |
                           |                                 |
                           v                                 |
                      +---------+                            |
                      | Claimed |                            |
                      +----+----+                            |
   +---------------+-------+-------+----------------+        |
   v               v               v                v        |
+-----------+   +-----------+   +-----------+   +-------------+ |
| Delivered |   | Retryable +---| Uncertain |---| Dead-letter |-+
+-----+-----+   +-----------+   +-----------+   +-------------+
     |
     v
   +---+
   | * |
   +---+
```

- **Claimed**: one adapter holds a short lease. Claim is not delivery success,
  and a second adapter cannot project the same intent.
- **Delivered**: definite provider success; provider identity persisted;
  duplicate ack is idempotent.
- **Retryable**: definite retryable rejection. Return to the same intent;
  never create another progress or final answer.
- **Uncertain**: timeout, connection loss, or unparseable response. Never
  resend blindly: reconcile by stable identity first, and retry the original
  intent only after confirming no side effect occurred.
- **Dead-letter**: definite non-retryable failure or human intervention.
  Retain the intent, reason, and actionable next step. A confirmed AgentTurn
  result is never rewritten as provider failure.

`chat.update`, reaction mutation, and progress creation follow the same
claim/ack/uncertain semantics. When an update is confirmed impossible, Server
may append exactly one final answer in the same thread, under its own stable
terminal delivery key, so retry, reconnect, and duplicate ingress never append
a second final answer.

### Capability Boundaries

The integration separates four capabilities because each has a different
authority and failure mode:

- **Ingress translation** belongs to `mohist-slack`. It normalizes wire events
  and acknowledges only a definite Server decision. It does not authorize the
  caller, choose a Session, or invoke an Agent.
- **Admission and execution arbitration** belong to the Server Connection
  boundary and Agent API: authorize stable Slack identities, deduplicate
  input, choose start or follow-up semantics, expose canonical Session/Turn
  state. A provider response cannot alter those facts.
- **Reply intent and liveness projection** are separate Server capabilities.
  The Agent authors reply body through the reply action; Server derives only
  liveness from canonical execution state. The separation makes silence valid
  and prevents status rendering from inventing Agent speech.
- **Provider projection** starts from a durable delivery intent. The adapter
  translates it to Slack post, update, upload, or reaction operations under
  one Socket lease. It cannot reinterpret work state or choose different reply
  content.

Markdown conversion, segmentation, attachments, and Slack control-syntax
escaping are provider rendering concerns after Server redaction. The adapter
never parses Runner logs, overrides Agent configuration, or writes the Mohist
database directly. Manager and Agent Connections use the same boundaries; the
Manager adds only the capability credential and its Agent-facing management
surface.

When a Connection is Disabled, the adapter still acknowledges the Slack event
at the transport layer, records audit, and discards it: no SessionInput,
AgentJob, or delivery intent is created, and the event cannot be replayed
after re-enable. Already accepted work remains under Mohist arbitration, but
no new Slack replies are claimed or sent while Disabled. After re-enable,
project only still-relevant current or final state, never stale progress from
the disabled interval. Remove binding and permanent delete remain distinct,
explicitly confirmed lifecycle operations; neither deletes the Mohist Agent or
AgentSession.

## Security Boundary

- All Slack ingress uses Socket Mode; no public ingress endpoint is required
  or opened. A proxy may be configured explicitly for Slack HTTPS and
  WebSocket traffic, but adapter transport to the loopback Server must not use
  that proxy.
- `mohist-slack` is a privileged local component in the Mohist Server trust
  domain. It receives only enough authority to call fixed Connections, read
  results, and return messages.
- The action-signing key and Connection Bot tokens share one self-hosted
  installation trust domain. The adapter may hold the key to forward a
  request, but Server revalidates signature, target Connection, actor, and
  executing Turn before acting. Neither is a Slack user credential, and
  neither may cross a trust boundary.
- Server encrypts App and Bot credentials, Mohist App credentials, and Agent
  App client/signing secrets, addressed by owner under Credential Ownership.
  They never enter Agent Instructions, transcripts, logs, client-visible
  state, durable rows, DTOs, or audit serialization. CLI releases its
  transient secret buffer immediately after submission; it never enters shell
  history, command arguments, or process environment.
- Member authorization uses stable Slack Workspace identity, never display
  name, avatar, or message text.
- Permission to invoke in a channel effectively borrows the Agent's configured
  execution capability, including repository writes, tools, and credentials.
  Access policy is an authorization decision, not a convenience toggle.
  Imported thread history is untrusted input whose maximum impact is bounded
  by Agent configuration.
- Audit records Workspace, conversation, and member identity for every call,
  but these identities never become Mohist administrators. Mohist App
  installer, Agent owner, Connection owner, and ordinary caller are four
  distinct roles.

## Non-Goals

- A Slack Bot does not run an Agent Runtime or own another Agent
  configuration.
- The adapter holds no state requiring backup or recovery.
- The Mohist App neither replies on behalf of an Agent nor becomes a shared
  execution identity.
- Slack does not reproduce the Agent editor, Workflow board, or full
  diagnostic workstation.
- A shared Bot never guesses a target Agent from natural language.
- The first version excludes Slack-native Agent messages, Agent Home, and
  streaming replies.
- No Slack-native slash commands or message shortcuts, no approval-gate
  notifications routed into Slack, and no coordination across Mohist Servers.
- No public marketplace, multi-tenant hosting, billing, or
  cross-organization identity federation, and no complete scale and
  operations experience.
- Local Socket Mode is not zero-step automation. The installer confirms Slack
  installation, waits for administrator approval when Workspace policy
  requires it, and supplies the installation result and App-level token
  through protected local input. Never request the user's Slack login session
  or require Slack CLI.
- This document does not fix API routes, storage fields, lock and lease
  durations, Slack SDK versions, or exact retry timing.

Any added capability must still enter through Agent API and the existing
Connection boundary.

## Status

Multi-Bot interactive selection is delivered, including the chooser,
cross-Project selected-Connection attribution, and single-execution recovery.
Pending choices expire after five minutes and only finished records are reaped
under the existing Slack event retention window.

The data-plane and control-plane boundaries are implemented. Server owns
Enrollment, managed Agent App, Connection, inbox, conversation mapping,
outbox, and lease facts; the stateless adapter owns only Socket protocol
translation and provider calls. `mo slack setup`, `mo slack install-agent`,
and the Mohist App conversation enter the same resumable control-plane
operations. Canonical manifests, protected local credential entry, staged
binding, identity-checked Socket leases, access admission, thread/DM Session
mapping, Stop, and stable delivery projection are available. The local setup
path has no OAuth callback or plaintext-token control path.

App-management calls reactively rotate an expired Configuration credential
without changing the installed Bot data plane. The Agent-authored reply action
owns reply content, and terminal handling owns only delivery liveness.

Manager sessions use these same boundaries: the operator-bound capability
credential and the ordinary command surface replace server-side parsing of
model output, and the standard liveness projection replaces acknowledgement
messages. The interim model-output management protocol is deleted rather than
preserved for compatibility. One gap remains: the running build still
acknowledges Manager requests with a text message and executes management
through that retired protocol.
