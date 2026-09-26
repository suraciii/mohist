# Slack

The Slack integration connects an already configured Mohist Agent to a Slack
Workspace under an independent identity. Slack is an interaction surface;
Mohist remains authoritative for Agents, work, Sessions, and results.

Product behavior is defined in [`../specs/integrations/slack/enrollment/spec.md`](spec.md) and not
repeated here. See [`agent-api.md`](../../../interfaces/agent-api/design.md) for the unified invocation
boundary. This document records only component boundaries and the decisions
that must remain true.

[Connections](../connections/design.md) define binding and member policy;
[interaction](../interaction/design.md) defines Session routing;
[delivery](../delivery/design.md) defines reply identity and reconciliation.

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
[`architecture.md`](../../../../design/architecture.md#durable-application-process-manager)).

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
