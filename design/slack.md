---
status: implemented
---

# Slack

The Slack integration connects an already configured Mohist Agent to a Slack
Workspace under an independent identity. Slack is an interaction surface;
Mohist remains authoritative for Agents, work, Sessions, and results.

Product behavior — prerequisites, setup steps, access policy, thread behavior,
reply presentation, lifecycle failures — is defined in
[`../docs/slack.md`](../docs/slack.md) and not repeated here. See
[`agent-api.md`](agent-api.md) for the unified invocation boundary. This
document records only component boundaries and the decisions that must remain
true.

The integration has two layers: the **data plane**, where a Connection provides
one Bot with a local Socket Mode channel, and the **control plane**, where a
Workspace-level Mohist App installs and operates each Agent App. They share
authoritative Server state but not responsibilities.

## Core Decisions

| Topic | Decision | Reason |
|---|---|---|
| Agent and Slack relationship | An Agent works independently first; a Connection is only another ingress for it | Slack cannot be a prerequisite for Agent execution |
| Slack identity | One Agent has one independent App / Bot in one Workspace | the visible identity tells users which Agent they invoke; a shared Bot never guesses |
| Installation authority | Server owns Slack control-plane state; `mohist-slack` handles only the Socket wire protocol | App creation, installation, and credential verification are recoverable product facts and belong in Server |
| Separate `mohist-slack` process | Yes, as a toolchain choice | Slack's primary client is Node and shares the Runner TypeScript toolchain |
| Adapter persistence | None | a process boundary is not a state boundary; Server is the sole authority |
| Ingress, conversation mapping, outbound delivery | Owned by Server, in the same backup boundary as Sessions | removes dual authority and cross-process unknown outcomes |
| Outbound delivery | One outbox; only an adapter holding a valid Socket lease may claim | one sender per App at a time prevents duplicate delivery |
| Agent configuration | A Connection stores no second Instructions, Runtime, Model, or Skills | there is one execution definition |
| Access control | A Connection decides who may invoke; it neither reduces nor expands execution capability | invocation scope and execution capability are separate decisions |
| Conversation mapping | A channel root mention starts a Session and thread replies continue it; a DM is one continuing Session | each Slack context keeps its native conversation convention |
| Reliability | Slack may redeliver; Mohist deduplicates and retains accepted input | capacity is never recovered by dropping accepted messages |
| Operating mode | Local Socket Mode only; each App has independent Bot and App-level tokens | self-hosting needs no public ingress and no Mohist-hosted control plane |
| Agent App credential source | CLI accepts the installed Bot token and the manually generated App-level token through protected input | a local deployment has no HTTPS OAuth callback, and App-management responses do not return a usable App-level token; never request a login session |
| Web role | Fallback surface for configuration, diagnostics, and takeover | Web is not a required workstation for using a Slack Agent |
| Mohist App form | The Mohist App binds the built-in Agent `mohist-slack` with preset Instructions and a Slack management Skill; it performs management through the ordinary agent command surface under an operator-bound capability credential, delegating to the same application services as the CLI | capability exists once in Server; CLI and conversation are two entry points, not two installation semantics; authority travels as a credential, never as parsed model text |
| Agent effects | An Agent changes Slack or Mohist resources only through explicit command calls — the reply action and the ordinary `mo` CLI surface; Server never parses model output into commands or messages | model text is reasoning; parsing it as a protocol is fragile, leaks internal shapes to users, and duplicates authority outside the API boundary |
| Installation DSL | `mo slack setup` installs the Workspace-level Mohist App; `mo slack install-agent <agent>` installs an existing Agent | `setup-agent` conflicts with Agent Readiness setup; `create` falsely claims a resource is created when the user is installing an Agent into Slack |
| Conversational Agent creation | The Mohist App asks at most for name and daily responsibility, creates a real Agent with defaults, then guides Slack installation | a Mohist App DM is already an authorization boundary and needs no draft approval state |
| Reply location | Server chooses the delivery target and injects a reply anchor with the input: thread root, triggering message, or DM | the model does not guess a thread from memory; the anchor is a system fact |
| Structured control in Slack | Signed action buttons with Server-verified payloads, plus Server-consumed boundary messages such as claim; no Slack-native slash commands | buttons reuse one verified mechanism and the same operations as CLI and Web; slash commands are a third grammar that forces manifest changes and reinstalls while doing nothing messages plus buttons cannot |
| Liveness honesty | Reactions and status messages project only real state-machine facts; timers are cleanup backstops, never sources of narrative | Buzz's welcome-kickoff failure mode: announcing ignorance on a deadline writes the wrong story in permanent ink — facts decide, timers are a last-resort backstop |
| Interruption | New input for the same active Session defaults to Steer, joining the current Turn or waiting in queue; only explicit Stop is Interrupt | matches SessionInput / AgentTurn semantics and prevents ordinary messages from aborting long work |
| Collaboration rules | Injected as a built-in Skill: no empty acknowledgement, callback after delegation, silence by default, self-contained replies, no guessed reply location | behavior stays inspectable and evolvable rather than hard-coded in adapter or renderer |
| Process transparency | Slack carries liveness and final replies; Open in Mohist links to the Web Session timeline | a quiet channel and an owner-visible timeline are separate signals with separate homes |

## System Boundary

```text diagram
Slack member
    | message / action
    v
Slack App / Bot
    |
    v  Socket Mode, opened outbound by the local service
mohist-slack -> Server ingress -> Connection boundary -> Agent API -> Agent / Job / Session -> Runner
                                      |
                                      +-> provider inbox / conversation mapping / outbound outbox

Slack control plane in Server, independent of the wire adapter
    | manages
    v
SlackWorkspaceEnrollment -> manages -> ManagedSlackAgentApp, one per managed Agent App
                                              | references
                                              v
                                        AgentConnection
```

| Component | Owns | Does not own |
|---|---|---|
| Slack | member identity, channels and message interaction, event and reply transport | Agent configuration, execution, work results |
| `mohist-slack` | translation between Slack Socket Mode protocol and normalized ingress / delivery intent; short leases granted by Server | persisted state, thread ownership, Agent execution, work-state arbitration, App creation / installation |
| Server Connection boundary (data plane) | provider identity and access decisions, durable ingress, conversation mapping, pending delivery, Agent API calls | Slack wire payloads, Agent execution, result arbitration |
| Server Slack control plane | Workspace enrollment, external App lifecycle and authorization, manifests, credential references, audit | Agent execution, thread ownership, wire protocol |
| Agent API | unified start, continue, observe, and stop operations | Slack mentions, threads, member directory, provider rate limits |
| Runner | execution from the resolved Mohist Agent definition | Slack identity, access policy, thread routing |

One `mohist-slack` process per Server carries the Socket connections for the
Mohist App and every Agent App. A shared process does not imply a shared Bot
identity: each App keeps independent credentials. Once an App is ready, the
adapter obtains a short lease and runtime credentials, then establishes or
restores its Socket.

### Mohist App Conversational Form

The control plane appears in Slack as the **Mohist App**, implemented by the
built-in Agent `mohist-slack`: a Server-reserved name ensured by `mo slack
setup`, outside the Project namespace, and not subject to ordinary archival or
deletion. Every management operation targets existing resources: Agent,
AgentConnection, SlackWorkspaceEnrollment, or ManagedSlackAgentApp. It creates
neither a second management model nor a second execution path.

`mohist-slack` is also the adapter process name. These are distinct objects
with one name; the shared name creates no implementation coupling.

The Mohist App uses the same data plane as Agent Apps — adapter, ingress,
outbox — but its access decision is fixed to a Mohist operator authorized to
manage the target resource; it does not use a Connection's Owner, Allowlist, or
Anyone policy. High-risk actions such as permanently deleting a Slack App are
unavailable in conversation.

Every Mohist App DM becomes a normal Agent Session and Turn, with the same
reply action, outbox, and liveness projection as every Agent Connection. To
read or change resources, the built-in Agent runs management operations as
ordinary tool calls from its Skill, the same way it sends replies through the
reply action. Command results return as tool output in the same Turn, and the
Agent composes the reply from them. Server never parses model output for
management requests, never synthesizes follow-up inputs on the Agent's behalf,
and never renders model text into a Slack message. This mirrors Buzz, where the
agent's interface to the platform is the same CLI humans use and effects exist
only as command calls.

Management authority is bound to the Session origin, not to model text. When a
Manager Session launches, Server recovers the operator from the Session's
immutable Slack origin, verifies that the operator may manage the Workspace's
Manager resources, and issues a manager capability credential scoped to that
operator and Enrollment — the role Buzz's `BUZZ_AUTH_TAG` plays. The credential
is injected into the Agent execution environment and never enters Instructions,
prompts, transcripts, durable rows, or logs. The Agent-facing management
surface rejects calls without it and reauthorizes the recovered operator
against the target resource on every call, delegating to the same application
services as the CLI. The credential excludes secret-bearing steps and
irreversible lifecycle operations: credential submission stays in the local
CLI, and permanent delete stays in Web or CLI with explicit confirmation. The
Agent can therefore announce only what a confirmed command result states.

Owner claim remains a Server-consumed boundary operation at ingress — like
Buzz's harness-consumed owner commands, it is never forwarded to the Agent.

### Why the Adapter Is Stateless

The separate process exists for language-ecosystem reasons, not state
ownership. An adapter that persisted thread mappings and pending delivery would
add a second recovery model, a second backup object, and states such as "Server
says sent, adapter says unsent." Therefore:

- **Ingress:** the adapter converts a Slack event into a normalized envelope
  with stable provider identity and submits it to the Connection boundary.
  Server decides quickly to ignore, reject, or durably accept into the provider
  inbox. The adapter acknowledges Slack only after a definite result and never
  waits for thread history, attachments, or Agent API. When Server is
  unavailable or the result is unknown, it does not acknowledge, so Slack
  redelivers under the same identity. A Slack acknowledgement means only that
  Mohist durably took responsibility for the provider event — not that user
  input became SessionInput.
- **Outbound:** Server stores a bounded delivery intent, never a Slack wire
  payload. The adapter claims one item, renders and sends it, and reports the
  result. An unconfirmable result is recorded and displayed by Server; no
  suspended state remains in the adapter.
- **Restart:** the adapter reconstructs no local state. After reconnecting it
  claims deliveries that have not converged. It never caches events while
  Server is down: an adapter cache cannot turn a message into accepted input,
  only into another recovery model. Slack's own redelivery window is the
  fallback; beyond it, the user resends.

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
current request, and the active send call are transient adapter state.

A Connection references an Agent without copying or modifying its execution
definition. Connecting Slack adds no provider fields to the Mohist Agent.

### Staged Binding for `install-agent`

A Connection exists before Agent App creation, so the installation record has a
stable durable target before the first uncertain external write. Otherwise an
unknown create result could leave a Slack App with no Mohist identity to resume
or reconcile. External identity is added only after installed credentials pass
verification:

- `AgentId + WorkspaceTeamId` are immutable after Connection creation.
- `AppId + BotUserId` change atomically exactly once, from both empty to both
  non-empty. Team, App, and Bot are immutable afterward.
- Partial binding, team rebinding, and a second App/Bot binding are forbidden.
- One Project/Agent/team has at most one non-deleted Connection.

One application boundary enforces staged creation and identity completion for
every caller — CLI, Web, and conversation share the same binding semantics.
Generic Connection edits cannot mutate identity fields.

A Connection expresses four independent facts: external installation progress,
operator desired state (Enabled/Disabled), Slack connection health, and Agent
Readiness. `Connected` cannot replace them: a Connection may be connected while
its Agent Needs setup, or its Agent may be Ready while Slack is offline.
Product views may expose all four, but a summary highlights one current state
and exactly one next action.

## Slack Control Plane

The control plane consists of two aggregates in Server's Slack integration
context. They own durable product facts about external Apps and follow
independent reasons to change and fail: Enrollment is one Workspace's ability
to provision and operate Apps, AgentApp is one external App and its
irreversible side effects, Connection is whether one Agent identity may be
invoked. A Configuration-token outage must not disable an installed Bot;
deleting a Connection must not erase an external App record; a Socket failure
must not rewrite Agent configuration.

### SlackWorkspaceEnrollment

A Workspace-level aggregate. By default its key has **no Project**: one
Workspace Mohist App is a Server-installation control plane that multiple
Projects may reference. Project isolation requires a product-spec change first;
the table shape must not decide accidentally. Within one Server installation,
an active `team_id` resolves to at most one Enrollment — two active records
would create competing provisioning authorities.

It owns: stable `team_id`, Mohist App external identity and lifecycle, the
capability to manage Agent Apps with last-verification facts, credential
references (never plaintext), and audit facts. It does not own Agents,
Connections, or Agent Apps, and does not turn Slack members into Mohist
administrators.

### ManagedSlackAgentApp

One aggregate per managed Agent App. `install-agent` is an application
operation, not an aggregate: it coordinates App creation, installation
authorization, and Mohist binding, while the aggregate stores only the Agent
App's own external facts.

AgentApp references its target Connection but is not a Connection child, and
the two never change in one transaction. Connection stays authoritative for
Agent/Workspace/provider identity, access policy, and enable/disable; AgentApp
is authoritative for the external App lifecycle. One external `app_id` belongs
to at most one AgentApp in its Workspace, and one AgentApp references only one
Connection.

It owns: `enrollment_id`, stable Agent App ID and external `app_id`, desired
and applied manifest version with verified scopes, App create/delete and
installation facts, operation fence, unknown outcome, error classification,
and audit.

No durable process-manager aggregate here: Slack create/delete is an external
side effect of AgentApp itself, so its fence stays in AgentApp. A process
manager stores pending commands but not business facts (see
[`architecture.md`](architecture.md#durable-application-process-manager));
AgentApp stores business facts and cannot be one. Cross-aggregate binding
advances as `AgentApp commits fact -> durable handler -> idempotent Connection
command`, never as a cross-aggregate transaction.

### Four-Axis State and One Next Action

AgentApp state is not one enum. It has four axes and one derived next action:

1. **App lifecycle:** `not-created` / `creating` / `create-unknown` / `created`
   / `deleting` / `delete-unknown` / `deleted`.
2. **Authorization:** `not-started` / `awaiting-user` / `pending-admin` /
   `authorized` / `expired-or-cancelled` / `revoked`.
3. **Manifest:** `desired` / `applied` / `drift-known`.
4. **Socket readiness:** both runtime credentials persisted, both identities
   verified, adapter lease alive. Missing either credential forbids Ready.

The unknown states may be left only through reconciliation or explicit human
arbitration. A process restart never repeats create/delete automatically. A
definite failure starts a new attempt on the same AgentApp, never a new
Connection or Bot target. Cancelled installation, expired authorization, and
pending approval all resume the same AgentApp.

### Credential Ownership

Credentials are addressed by their actual owner; a Connection neither owns nor
copies Agent App runtime credentials:

- Mohist App runtime credentials live at the Enrollment address, as an opaque
  persisted reference no caller can construct. Bot token and App-level token
  are distinct secret kinds under one owner reference. `mo slack setup` is the
  only normal provision, repair, and rotation entry point; repeated setup
  resumes one record and never creates a second Mohist App.
- Agent App client/signing secret, App-level token (`xapp-`), and Bot token
  (`xoxb-`) live at the AgentApp address.
- A Connection obtains data-plane credentials only through an active AgentApp
  binding.

Removing a Connection does not delete its Slack App by default, so credentials
addressed by Connection would couple two independent lifecycles and could
destroy credentials while the external App remains managed.

The secret-provision endpoint accepts only operator-authenticated loopback
requests. Its body identifies the target installation and carries only the
credentials that step requires; the caller cannot supply a credential address.
Credentials come from hidden CLI input or a protected, user-owned file.
Responses, status, errors, logs, audit, and documentation examples contain no
credentials. Status exposes Bot and App-level provisioning and verification
separately. The Mohist App becomes Ready only after both are valid and Socket
hello is confirmed.

Credential submission converges in a fixed order: verify Bot and Workspace
first; write an App-level token only as an unverified candidate; a validation
lease accepts no business traffic; bind and grant a runtime lease only after
Socket App-identity verification. The system must never reach "Connection bound
and usable, token not persisted." Repeating the same verified credential set
returns the same result without rebinding. A candidate for a different
App/team/Bot is deleted and remains unusable.

### App Provisioning Credentials (Configuration Token)

App management uses one Workspace-level Configuration access/refresh pair,
stored at the Enrollment address as its provisioning part. It is separate from
Mohist App runtime credentials: the first authorizes creating and maintaining
Apps, the second authorizes messaging as the Bot. Their addressing, rotation,
and invalidation are independent and never mixed or derived.

- **One provisioning path.** Setup guides the user to create the pair in
  Slack's App management page and submit it once through protected input.
  Product documentation and CLI guidance never assume a Slack CLI exists.
- **Rotation.** On access-token expiry, Server rotates with the refresh token
  and atomically replaces the pair and provider `team_id`. Rotation is reactive
  and transparent — no background timer. An unknown rotation result is marked
  `credential-rotation-unknown` and requires a new pair from the user; blind
  retry is forbidden. Degraded begins only when the refresh token is also
  invalid. Rotation failure reduces App-management capability but never
  interrupts the Socket data plane of installed Apps.
- **Invalidation.** Slack revocation appears as authentication failure on an
  App-management call: Enrollment capability becomes Degraded with one next
  action (rerun `mo slack setup`), while existing AgentApp and Connection data
  planes keep working. External failure is never amplified with automatic
  retries.
- **Audit.** Every external write records actor, object, and result — never the
  token.

The control plane reaches Slack HTTPS through four narrow capability ports —
credential rotation, manifest management, Bot identity verification, and member
identity lookup — so no operation receives authority it does not need. Only
Allowlist/Anyone admission calls the member-identity port; owner and DM fast
paths do not. These ports isolate Slack SDK and HTTP shapes from domain
services. Socket operations — `apps.connections.open`, hello, events,
interactions, delivery — belong only to `mohist-slack`; Server implements no
second WebSocket client to verify `xapp-`.

### `setup` / `install-agent` Orchestration

CLI and Mohist App do not implement installation separately; both call the same
Server application service. On each call the service reads current aggregate
facts, performs at most one unconfirmed external write, and returns complete
progress with one next action. The ordering invariants:

- The first `mo slack setup` obtains the provider-confirmed `team_id` from a
  successful Configuration-token rotation and uses it as the idempotency key.
  No App is created while a rotation result is unknown.
- Returned `app_id`, client credentials, and installation link are persisted
  before any user-visible link is exposed. An unknown create outcome persists
  the operation fence and stops; it is never resent.
- Runtime credentials are verified as Bot identity first, persisted as
  unverified candidates second, and marked verified only after the adapter
  reports the expected App's first Socket hello under a validation lease. On
  mismatch the candidate is deleted and the Connection stays unbound.
- AgentApp then commits a bindable fact, and a durable handler idempotently
  fills Connection App/Bot identity. Installation projects Ready only after the
  adapter first obtains a runtime lease.
- A rerun repairs drift, missing or invalid credentials, and connection without
  creating another Connection or Agent App. Explicitly reprovisioning valid
  credentials on a Ready record rotates them, but they must resolve to the same
  team/App/Bot identity.

The idempotency key for `install-agent` is `(enrollment_id, AgentId)`. The
conversational install-Agent operation performs only the non-secret steps of
this same flow and returns the same progress; at a secret step it provides the
link and the local continuation command `mo slack install-agent <agent>`. Chat
text is never a secret-input channel.

### Canonical Manifests

Manifests are canonical, versioned, and drift-detected: hashing covers manifest
version, product capability version, and identity snapshot, and Slack's
true-or-omitted Boolean round-tripping must not create false drift. The exact
scope set is canonical in code; the product document lists each permission with
its reason. Interactivity returns through Socket Mode and has no Request URL.

### Socket Leases and Adapter Discovery

Server grants two short leases. A **validation lease** allows one Socket with a
candidate App-level token to report `hello.app_id`; it accepts no ingress and
claims no outbox work. A **runtime lease** is available only to a
credential-verified active App whose Connection is Enabled. `mohist-slack`
discovers targets and renews through operator-authenticated loopback transport.
Only a lease response may contain a secret; status, list, and view DTOs never
do.

When the adapter disconnects or a lease expires, Server stops granting it
delivery intents; a new adapter takes over only after the old lease expires.
Mohist App runtime ingress routes to the management actor, not to a Connection
access policy.

A lease pins its credential generation by fingerprint at issue time. After a
candidate is reprovisioned or a verified pair rotates, the old lease fails
closed: renew is rejected, hello returns stale, and a hello from an old token
must not reject or delete the new candidate. A failed acquisition leaves no
inert active lease and does not displace the existing holder.

Every Socket envelope validates `api_app_id + team_id` before resolving
Enrollment or Connection. An unknown App/team is acknowledged and rejected,
never routed by Bot name. The acknowledgement still means only Server durable
acceptance and never waits for Agent execution.

## Session Boundary

Buzz reuses an Agent Session by channel because its channel is the continuous
collaboration boundary. Slack organizes one conversation as a thread, so Mohist
uses `Agent + thread` as the Session boundary instead of sharing one context
forever across a channel.

Three rules follow:

- One thread may contain multiple AgentSessions, one per Agent. Mentioning a
  new Agent for the first time neither switches nor contaminates the original
  Session.
- Do not start work when ownership is ambiguous. If one message mentions
  multiple Mohist Bots, or a thread is bound to multiple Bots and an
  unmentioned reply arrives, ask the user to choose instead of guessing. The
  chooser is an interactive Slack selection, not a free-text reply to parse.
- Do not start work when the target Agent cannot run it. When admission finds
  the Agent not Ready or its Connection unavailable, Server posts one
  Server-authored guidance message — safe summary for the caller, specific gap
  only for Owner and operators — without creating a Session, Turn, or queued
  input. This is Buzz's setup-mode nudge: the platform answers for an agent
  that cannot answer for itself, deduplicated per triggering message.

DM is the exception. Slack DM users do not organize work by thread; treating
each message as new work would split a continuous thought into separate jobs.
Server stores one current AgentSession per Connection DM conversation and every
normal message continues it. The first version provides no "new task" operation
inside DM. Parallel work uses separate channel threads, each with an
independent Session.

Different Mohist Servers do not share thread routing.

## Reliability Contract

Slack-to-adapter transport is externally at-least-once; the system cannot claim
end-to-end exactly-once. Mohist instead ensures that duplicate events do not
repeat domain effects, confirmed replies are not resent, and uncertainty is
visible when results cannot be confirmed.

- Deduplication occurs in Server. The same Slack message identity that became
  input always resolves to the same SessionInput.
- Provider inbox and SessionInput deduplicate separately. Redelivery after a
  lost request result accepts neither the event nor the input twice.
- Accepted input is a SessionInput and cannot be deleted through drop-oldest or
  similar policy. Reject new input when capacity is exhausted.
- The outbound outbox is bounded. Replaceable intermediate progress may
  coalesce. Final results, explicit failures, and user actions cannot disappear
  silently.
- Slack delivery failure does not change AgentJob or AgentTurn results. Server
  alone judges execution.
- Provider messages authored by a Mohist Bot — the Mohist App Bot or any Agent
  App Bot — are rejected at admission before any durable record. They never
  enter the provider inbox and never become SessionInput, so an Agent cannot
  trigger itself or another Agent through its own replies.
- A long outage may exceed Slack event retention, so full replay is not
  promised. After recovery, display that a gap may exist.

Control-plane create/delete is likewise at-least-once. A repeated attempt does
not repeat App creation/deletion. An unknown result is exposed as unknown and
converges only through reconciliation or human arbitration under Four-Axis
State.

### State Projection and Message Identity

Server is the sole judge of AgentSession and AgentTurn state. Slack provider
and adapter project only Server-confirmed state; they cannot infer work success
from a Slack API response or read Runner output to decide state.

An accepted Slack input uses stable message identity as input identity and
derives one dispatch reference for the whole work item. For each dispatch
reference, Server allows:

- At most one replaceable progress projection, persisted with its provider
  message identity so later updates target the same message.
- At most one terminal result projection, deduplicated by a stable terminal
  delivery key. If a progress message exists, the terminal projection replaces
  it in place; otherwise one final answer is sent in the same thread.
- Fast work may omit progress and project only the Received reaction plus one
  final answer. If the platform cannot react on the user's message, the
  Received reaction is projected on the progress message instead.

Default reactions are `Received=👀`, `Working=⏳`, `Completed=✅`, exception
`⚠️`. Reactions are liveness signals, not Session/Turn facts; a missing, late,
or successful reaction mutation never changes Server state. State arbitration
never enters provider or adapter.

### Reply Body Belongs to Agent; Liveness Belongs to Server

- **Server owns liveness projection**: the reaction mutations and one
  replaceable progress message, independently of the Agent. Projection starts
  at acceptance, and whether the Agent replies does not affect liveness
  closeout. Reaction mutation is best-effort: a bounded, logged failure never
  blocks or fails a Turn — as in Buzz, reactions are cosmetic liveness, never
  work state.
- **Agent owns reply body.** During a Turn, the Agent actively sends what it
  wants to say through a Mohist **reply action**: an intent API to Server
  carrying body and reply anchor. The reply action enters the same outbox and
  reuses redaction, duplicate protection, anchor validation, and Delivery
  uncertain reconciliation. The Agent never connects directly to Slack. The
  final answer preferably replaces the progress message in place.

Server never extracts assistant text from Runner Turn output. Turn output is a
Runner fact used to judge Session/Turn state; it is not a reply source, a
command channel, or a projection input. The reply action is the only reply
source. Terminal handling owns liveness closeout for every session kind,
including Manager sessions: every accepted input's liveness reaches a terminal
projection (Completed reaction, attention reaction, or one explicit failure
notice) on every outcome — completion, failure, cancellation, Agent crash, or
Server restart. This is the durable counterpart of Buzz's drop-guard reaction
cleanup: the outbox persists the closeout intent, so no exit path leaves
liveness open.

- **Silence is valid.** If a Turn ends with no reply action, the Agent chose
  silence. Liveness closes normally; Server invents no status summary.
- **Agent reports failure.** On execution failure or required human action, the
  Agent sends reason and next step through the reply action. Only an Agent
  process crash or complete non-response permits a system-authored fallback
  explicitly labeled as a system failure.

Detecting that a Turn ended without publishing belongs to the Runtime as an
advisory reminder to the model — Buzz's reply guard, bounded and explicitly
silence-licensing — never to Server, which neither synthesizes the missing
reply nor treats silence as failure.

### Reply Action CLI: `mo slack message send`

The reply action command surface is `mo slack message send`, a general CLI for
people and Agents rather than an Agent-only black box. Its explicit destination
matches Buzz `buzz messages send`:

```text literal
mo slack message send --conversation <id> --text "<body>"
mo slack message send --conversation <id> --reply-to <ts> --text "<body>"        # reply in thread
printf 'long body\n\nmultiple paragraphs\n' | mo slack message send --conversation <id> --text -
mo slack message send --conversation <id> --text "see this screenshot" --file ./screenshot.png
mo slack message send --conversation <id> --text "architecture diagram" --image https://example.com/diagram.png
```

- **Explicit destination.** `--conversation` is required; `--reply-to` is the
  optional thread anchor. The Agent reads both from the injected reply anchor
  instead of choosing from memory. Sending elsewhere states that intent
  explicitly.
- **Body through `--text`.** A string, or `-` for stdin so shell escaping does
  not consume newlines. The Agent writes standard Markdown; the renderer
  converts it to Slack mrkdwn and degrades unsupported tables and headings to
  readable text without error.
- **Mentions in body.** The CLI resolves `@displayname` to a valid Slack
  mention. Ambiguity or an invisible target returns an actionable error and
  sends nothing silently.
- **Images.** `--file` uploads a local image; `--image` references a public
  URL. An Agent may explicitly send a useful screenshot or artifact; the
  product rule against copying artifacts governs system-generated clutter, not
  explicit sends.
- **Success means outbox acknowledgement.** Send synchronously commits to the
  outbox and confirms that reliable delivery was accepted, not that Slack
  displayed it. Eventual Slack failure surfaces as Delivery uncertain without
  reporting back into Agent execution.
- **Multiple sends coalesce.** Within one Turn, Server coalesces sends into one
  final answer for that dispatch reference. The invariant remains at most one
  final answer per input.
- **First version scope.** No broadcast across channels and no distinct message
  types. `mo slack message` is a command group; future `message get` and
  `message thread` may let an Agent fetch context. Only send is implemented
  first.

### Signed Action Buttons

Slack interactivity reaches Server as `block_actions` provider interactions.
Every interactive control Mohist renders carries a Server-signed payload bound
to Connection, conversation, actor Slack identity, target resource, and expiry.
Server revalidates signature, freshness, and actor authorization on every
click; a stale, foreign, or expired action is rejected with a visible notice.
An accepted click enters the durable provider inbox like any other input and
delegates to the same application service the equivalent CLI or Web call uses.
Buttons are shortcuts to existing operations, never a second command grammar:
they carry no free text, and their effects are exactly the effects of the
operation they name.

Current actions: Stop a queued or running Turn, Retry from a failure notice,
and Agent selection for a multi-Bot mention. Routing Mohist approval gates into
Slack as actionable notifications requires a notification routing policy and
belongs to a future phase.

### Delivery Intent, Claim/Ack, and Unknown Results

Server persists one delivery intent for every logical projection. An intent
carries at least the Connection, the dispatch reference, the target
conversation/thread, the projection kind, a stable deduplication key, the
current provider message identity if known, and a safely replayable content
reference. Projection kinds are replaceable progress, terminal result /
explicit failure, user action, and reaction mutation. Tool calls and Runner
logs are not user messages.

Lifecycle:

1. **Pending** — persisted, not yet called the provider.
2. **Claimed** — one adapter holds a short lease. Claim is not delivery
   success, and a second adapter cannot project the same intent.
3. **Delivered/Acked** — definite provider success, with provider identity
   persisted. Duplicate ack is idempotent.
4. **Retryable** — definite retryable rejection. Return to the same intent;
   never create another progress or final answer.
5. **Uncertain** — timeout, connection loss, or unparseable response. Never
   resend blindly: reconcile by stable identity first, and retry the original
   intent only after confirming no side effect occurred.
6. **Dead-letter/Needs attention** — definite non-retryable failure or human
   intervention. Retain the intent, reason, and actionable next step. A
   confirmed AgentTurn result is never rewritten as provider failure.

`chat.update`, reaction mutation, and progress creation follow the same
claim/ack/uncertain semantics. If provider message identity for an existing
status message is missing or an update outcome is unknown, reconcile before
updating. When update is confirmed impossible, Server may append exactly one
final answer in the same thread, under its own stable terminal delivery key, so
retry, reconnect, and duplicate ingress never append a second final answer.

### Capability Boundaries

The integration separates four capabilities because each has a different
authority and failure mode:

- **Ingress translation** belongs to `mohist-slack`. It normalizes Slack wire
  events and acknowledges only a definite Server decision. It does not
  authorize the caller, choose a Session, or invoke an Agent.
- **Admission and execution arbitration** belong to the Server Connection
  boundary and Agent API. They authorize stable Slack identities, deduplicate
  input, choose start or follow-up semantics, and expose canonical Session/Turn
  state. A provider response cannot alter those facts.
- **Reply intent and liveness projection** are separate Server capabilities.
  The Agent authors reply body through the reply action; Server derives only
  liveness from canonical execution state. This separation makes silence valid
  and prevents status rendering from inventing Agent speech.
- **Provider projection** starts from a durable delivery intent. The adapter
  translates it to Slack post, update, upload, or reaction operations under one
  Socket lease. It cannot reinterpret work state or choose different reply
  content.

Markdown conversion, segmentation, attachments, and Slack control-syntax
escaping are provider rendering concerns after Server redaction. They cannot
change reply authorship, target identity, deduplication, or the rule that one
input has at most one final answer. Manager and Agent Connections use the same
boundaries; the Manager adds only the capability credential and its
Agent-facing management surface.

When a Connection is Disabled, the adapter still acknowledges the Slack event
at the transport layer, records audit, and discards it. Transport
acknowledgement is not acceptance: the boundary creates no SessionInput,
AgentJob, or delivery intent, and cannot replay the event after re-enable.
Already accepted work remains under Mohist arbitration, but no new Slack
replies are claimed or sent while Disabled. After re-enable, project only
still-relevant current or final state, never stale progress from the disabled
interval. Remove binding and permanent delete remain distinct, explicitly
confirmed lifecycle operations; neither deletes the Mohist Agent or
AgentSession.

## Security Boundary

- All Slack ingress uses Socket Mode; no public ingress endpoint is required or
  opened. A proxy may be configured explicitly toward Slack, but adapter
  transport to the loopback Server must not use that proxy.
- `mohist-slack` is a privileged local component in the Mohist Server trust
  domain. It receives only enough authority to call fixed Connections, read
  results, and return messages.
- The action-signing key and Connection Bot tokens share one self-hosted
  installation trust domain. The adapter may hold the key to forward a request,
  but Server revalidates signature, target Connection, actor, and executing
  Turn before acting. Neither is a Slack user credential, and neither may cross
  a trust boundary.
- Server encrypts App and Bot credentials, Mohist App credentials, and Agent
  App client/signing secrets, addressed by owner under Credential Ownership.
  They never enter Agent Instructions, transcripts, logs, client-visible state,
  durable rows, DTOs, or audit serialization. CLI releases its transient secret
  buffer immediately after submission; it never enters shell history, command
  arguments, or process environment.
- Member authorization uses stable Slack Workspace identity, never display
  name, avatar, or message text.
- Permission to invoke in a channel effectively borrows the Agent's configured
  execution capability, including repository writes, tools, and credentials.
  Access policy is an authorization decision, not a convenience toggle.
  Imported thread history is untrusted input whose maximum impact is bounded by
  Agent configuration.
- Audit records Workspace, conversation, and member identity for every call,
  but these identities never become Mohist administrators. Mohist App
  installer, Agent owner, Connection owner, and ordinary caller are four
  distinct roles.

The first version provides no public marketplace, multi-tenant hosting,
billing, or cross-organization identity federation. Those change installation,
authorization, and operations and belong to another product phase.

## Non-Goals

- A Slack Bot does not run an Agent Runtime or own another Agent configuration.
- The adapter holds no state requiring backup or recovery.
- The Mohist App neither replies on behalf of an Agent nor becomes a shared
  execution identity.
- Slack does not reproduce the Agent editor, Workflow board, or full diagnostic
  workstation.
- A shared Bot never guesses a target Agent from natural language.
- The first version excludes Slack-native Agent messages, Agent Home, and
  streaming replies.
- Local Socket Mode is not zero-step automation. The installer confirms Slack
  installation, waits for administrator approval when Workspace policy requires
  it, and supplies the installation result and App-level token through
  protected local input. Never request the user's Slack login session or
  require Slack CLI.
- This document does not fix API routes, storage fields, lock and lease
  durations, Slack SDK versions, or exact retry timing.

## Status

The data-plane and control-plane boundaries are implemented. Server owns
Enrollment, managed Agent App, Connection, inbox, conversation mapping, outbox,
and lease facts; the stateless adapter owns only Socket protocol translation
and provider calls. `mo slack setup`, `mo slack install-agent`, and the Mohist
App conversation enter the same resumable control-plane operations. Canonical
manifests, protected local credential entry, staged binding, identity-checked
Socket leases, access admission, thread/DM Session mapping, Stop, and stable
delivery projection are available. The local setup path has no OAuth callback
or plaintext-token control path.

App-management calls reactively rotate an expired Configuration credential
without changing the installed Bot data plane. The Agent-authored reply action
owns reply content, and terminal handling owns only delivery liveness. This
separation permits intentional silence and keeps the adapter from interpreting
Agent text as control data.

Manager sessions use these same boundaries: the operator-bound capability
credential and the ordinary command surface replace server-side parsing of
model output, and the standard liveness projection replaces acknowledgement
messages. The interim model-output management protocol is deleted rather than
preserved for compatibility. Its conversation-side delivery is a current gap:
the running build still acknowledges Manager requests with a text message and
executes management through that protocol.

Slack-native slash commands, message shortcuts, streaming replies, and
Slack-native Agent ingress remain excluded. Approval-gate notifications routed
into Slack, public marketplace, multi-tenant hosting, coordination across
Mohist Servers, App Home, and complete scale and operations experience remain
future phases. Future capabilities must still enter through Agent API and the
existing Connection boundary. The adapter must never parse Runner logs,
override Agent configuration, or write the Mohist database directly.
