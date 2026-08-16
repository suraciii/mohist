---
status: implemented
---

# Slack

The Slack integration connects an already configured Mohist Agent to a Slack
Workspace under an independent identity. Slack is an interaction surface;
Mohist remains authoritative for Agents, work, Sessions, and results.

Product behavior, including connection prerequisites, Setup steps, access
policy, thread behavior, reply presentation, and lifecycle failures, is defined
in [`../docs/slack.md`](../docs/slack.md) and is not repeated here. See
[`agent-api.md`](agent-api.md) for the unified invocation boundary. This
document records only component boundaries and decisions that must remain true.

The integration has two layers: the **data plane**, where a Connection provides
one Bot with a local Socket Mode runtime channel, and the **control plane**,
where a Workspace-level Mohist App installs and operates each Agent App. They
share authoritative Server state but not responsibilities.

## Core Decisions

| Topic | Decision | Reason |
|---|---|---|
| Agent and Slack relationship | An Agent works independently first; a Connection is only another ingress for that Agent | Slack cannot be a prerequisite for Agent execution |
| Slack identity | One Agent has one independent App / Bot in one Workspace | Users know which Agent they invoke from the visible identity; a shared Bot never guesses |
| installation authority | Server owns Slack control-plane state; `mohist-slack` handles only Socket wire protocol | App creation, Slack installation, and credential verification are recoverable product facts and belong in Server |
| separate `mohist-slack` process | yes, as a toolchain choice | Slack's primary client is Node and shares the Runner TypeScript toolchain; .NET would require a separate Socket Mode and event implementation |
| adapter persistence | none | a process boundary is not a state boundary; Server is already the sole authority |
| ingress, conversation mapping, and outbound delivery | owned by Server | placing them in the same backup boundary as Sessions removes dual authority and cross-process unknown outcomes |
| outbound delivery channel | one outbox; only an adapter executor holding a valid Socket lease may claim | each App has one sender at a time, preventing duplicate delivery |
| Agent configuration | Connection stores no second Instructions, Runtime, Model, or Skills | there is one execution definition |
| access control | Connection decides who may invoke; it neither reduces nor expands the Agent's configured execution permissions | invocation scope and execution capability are separate decisions |
| conversation mapping | a channel root mention starts a Session and thread replies continue it; normal DM messages always continue one Session as a continuous conversation | each Slack context keeps its native conversation convention |
| reliability | Slack may redeliver; Mohist deduplicates and retains accepted input | capacity cannot be recovered by dropping old accepted messages |
| operating mode | local Socket Mode only; Mohist App and every Agent App each have independent Bot and App-level tokens | self-hosting needs no public ingress and no Mohist-hosted control plane |
| Agent App credential source | for a fully local deployment, CLI accepts the installed Bot token and manually generated App-level token through protected input | Slack OAuth callback requires HTTPS and public App-management responses do not return a usable App-level token; never request a login Session |
| Web role | fallback surface for configuration, diagnostics, and takeover | Web is not a required workstation for using a Slack Agent |
| Mohist App form | Mohist App binds the built-in Mohist Agent named `mohist-slack`, with preset Instructions and a Slack management Skill; it uses normal Agent execution and performs management through the ordinary agent command surface under an operator-bound capability credential, delegating to the same application services as the CLI | capability exists once in Server; CLI and conversation are two entry points, not two installation semantics; authority travels as a credential, never as parsed model text |
| Agent effects | an Agent changes Slack or Mohist resources only through explicit command calls — the reply action and the ordinary `mo` CLI surface; Server never parses model output into commands or messages | model text is reasoning; parsing it as a protocol is fragile, leaks internal shapes to users, and duplicates authority outside the API boundary |
| installation DSL | `mo slack setup` installs the Workspace-level Mohist App; `mo slack install-agent <agent>` installs an existing Agent | `setup-agent` conflicts with Agent Readiness setup, while `create` falsely says an Agent or Connection is being created when the user is installing an Agent into Slack |
| conversational Agent creation | Mohist App may ask only for name and daily responsibility, creates a real Agent with defaults, then guides Slack installation | a Mohist App DM is already an authorization boundary and needs no draft approval state |
| reply location | Server chooses the delivery target and injects a reply anchor with the input: thread root, triggering message, or DM | the model does not guess a thread from memory; the anchor is a system fact |
| interruption | new input for the same active Session defaults to Steer, joining the current Turn or waiting in queue; only explicit Stop is Interrupt | this matches SessionInput / AgentTurn and prevents ordinary messages from aborting long work |
| collaboration rules | inject a built-in Skill: no empty acknowledgement, mention the delegator after completing delegated work, silence by default, self-contained replies, and no guessed reply location | behavior remains inspectable and evolvable rather than hard-coded in adapter or renderer |
| process transparency | Slack carries liveness and final replies; Open in Mohist links to the Web Session timeline for complete progress | a quiet channel and an owner-visible timeline are separate signals with separate homes |

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
                                        Agent + team + app + bot + access / desired state
```

| Component | Owns | Does not own |
|---|---|---|
| Slack | member identity, channels and message interaction, event and reply transport | Agent configuration, execution, or work results |
| `mohist-slack` | translation between Slack Socket Mode protocol and normalized ingress / delivery intent; short leases granted by Server | persisted state, thread ownership decisions, Agent execution, work-state arbitration, or App creation / installation |
| Server Connection boundary, data plane | provider identity and access decisions, durable ingress, conversation mapping, pending delivery, and Agent API calls | Slack SDK / wire payloads, Agent execution, or result arbitration |
| Server Slack control plane | Workspace enrollment, external lifecycle and authorization for Mohist App and Agent Apps, manifests, credential references, and audit | Agent execution, thread ownership, or wire protocol |
| Agent API | unified start, continue, observe, and stop operations | Slack mentions, threads, member directory, or provider rate limits |
| Runner | execution from the resolved Mohist Agent definition | Slack identity, access policy, or thread routing |

Each Mohist Server runs one `mohist-slack` process that carries Socket
connections for the Workspace Mohist App and every Agent App. Each App still
uses independent credentials; a shared process does not imply a shared Bot
identity. The Server control plane never transfers state authority to the
adapter. Once an App is ready, the adapter obtains a short lease and runtime
credentials and then establishes or restores its Socket.

### Mohist App Conversational Form

The control plane appears in Slack as the **Mohist App**. Its implementation is
the built-in Mohist Agent named `mohist-slack`: a Server-reserved name ensured by
`mo slack setup`, outside the Project namespace available to users, and not
subject to ordinary archival or deletion. It has preset Instructions and a
Slack management Skill and runs through normal Agent execution. Every management
operation targets existing resources: Agent, AgentConnection,
SlackWorkspaceEnrollment, or ManagedSlackAgentApp. It creates neither a second
management model nor a second execution path.

`mohist-slack` is also the adapter process name. These are distinct objects with
one name: a local Slack protocol service and the management Agent behind Mohist
App. Their shared integration name creates no implementation coupling.

Mohist App uses the same data plane as Agent Apps: adapter, ingress, and outbox.
Its access decision, however, is fixed to a Mohist operator authorized to manage
the target resource; it does not use a normal Connection's Owner, Allowlist, or
Anyone policy. High-risk actions such as permanently deleting a Slack App are
unavailable in conversation and require explicit confirmation in Web or CLI.
Conversational Agent creation uses defaults to create a real Agent directly.
The DM operator who can drive Mohist App is already the authorization boundary,
so no draft approval state is added.

Every Mohist App DM first becomes a normal Agent Session and Turn, and the
Session uses the same reply action, outbox, and liveness projection as every
Agent Connection. To read or change resources, the built-in Agent runs
management operations as ordinary tool calls from its Slack management Skill,
the same way it sends replies through the reply action. Command results return
as tool output in the same Turn, and the Agent composes the natural-language
reply from them. Server never parses model output for management requests,
never synthesizes follow-up inputs on the Agent's behalf, and never renders
model text into a Slack message. This mirrors Buzz, where the agent's
interface to the platform is the same CLI humans use and effects exist only as
command calls.

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

The separate process exists for language ecosystem reasons, not state
ownership. If the adapter also persisted thread mappings and pending delivery,
the product would gain a second recovery model, a second backup object, and
states such as 'Server says sent, adapter says unsent' that require
reconciliation. Therefore:

- **Ingress:** the adapter converts a Slack event into a normalized envelope
  with stable provider identity and submits it to the Connection boundary.
  Server quickly decides to ignore, reject, or durably accept it into the
  provider inbox. The adapter acknowledges Slack only after a definite result;
  it does not wait for thread history, attachment download, or Agent API.
  When Server is unavailable or the result is unknown, it does not acknowledge,
  so Slack redelivers under the same identity. A Slack acknowledgement means
  only that Mohist durably took responsibility for the provider event, not that
  user input became SessionInput. Bot acceptance messaging communicates that
  later decision.
- **Outbound:** Server stores a bounded delivery intent, never a Slack wire
  payload. The adapter claims one item, renders and sends it, and reports the
  result. If the result cannot be confirmed, Server records and displays that
  state; no suspended state remains in the adapter.
- **Restart:** the adapter reconstructs no local state. After reconnecting, it
  claims Server deliveries that have not converged.

The adapter does not cache events when Server is unavailable. Both processes
belong to one self-hosted installation and trust domain but may restart
independently. An adapter cache cannot turn a message into accepted Agent input;
it only introduces another recovery model. Slack's own redelivery window is the
fallback. After that window, the user must resend, as documented in the product
guide.

The adapter limits transient concurrency only. It owns no durable queue or
product-level backpressure. Before acknowledging an ingress event, Server checks
provider-inbox capacity; later, Agent API Session-input capacity accepts or
rejects user input. The outbound outbox is likewise bounded. Replaceable unsent
progress may coalesce to the latest state. Final results, explicit failures, and
messages requiring user action must never be dropped silently. If these cannot
fit, Connection becomes Degraded (Backpressured) and stops accepting new Slack
input.

## Connection in the Domain

AgentConnection belongs to the Agent domain. Its binding, access policy, and
lifecycle are durable product behavior. Provider inbox, Slack conversation
mapping, and pending-delivery records are integration records owned by Server
infrastructure, not business facts of AgentConnection or AgentSession. Only the
Socket connection, current request, and active send call are transient protocol
state that the adapter may hold.

A Connection references an Agent without copying or modifying its execution
definition. Connecting Slack adds no provider fields to the Mohist Agent.

### Staged Binding for `install-agent`

The `install-agent` path requires a Connection before Agent App creation so the
installation record has a stable durable target before the first uncertain
external write. Otherwise an unknown create result could leave a Slack App with
no Mohist identity to resume or reconcile. External App/Bot identity is added
only after installed credentials pass verification:

- `AgentId + WorkspaceTeamId` are immutable after Connection creation.
- `AppId + BotUserId` may change atomically exactly once from both empty to both
  non-empty. Team, App, and Bot are immutable afterward.
- Partial binding, team rebinding, and a second App/Bot binding are forbidden.
- One Project/Agent/team has at most one non-deleted Connection.

One application boundary enforces staged creation and identity completion for
every caller. Staged creation reserves the Workspace team without pretending
that Slack identity already exists. Identity completion writes the App/Bot pair
atomically and idempotently. Generic Connection edits cannot mutate these
identity fields. Keeping this rule in one boundary prevents CLI, Web, and
conversational installation from acquiring different binding semantics.

A Connection expresses four independent facts: whether external installation
is complete (Setup progress), whether the operator wants Enabled or Disabled
(Desired state), whether Slack is currently healthy (Connection health), and
whether the bound Agent has an executable configuration (Agent Readiness).
`Connected` cannot replace these four facts. A Connection may be connected
while its Agent Needs setup, or its Agent may be Ready while Slack is offline.

Product views may expose all four facts, but they must not become four competing
overall states. A Connection summary highlights one current state and exactly
one next action.

## Slack Control Plane

The control plane consists of two independent aggregates in the Server Slack
integration supporting context. They do not belong to the Agent domain or to
`mohist-slack`. They own durable product facts about external Apps. The data
plane, provider inbox, mapping, and outbox remains integration state in Server
infrastructure and is not mixed with them.

The split follows independent reasons to change and fail. Enrollment represents
one Workspace's ability to provision and operate Apps; AgentApp represents one
external App and its irreversible side effects; Connection represents whether
one Agent identity may be invoked. A Configuration-token outage must not disable
an installed Bot, deleting a Connection must not erase an external App record,
and a Socket failure must not rewrite Agent configuration. Combining these
authorities would couple all three failures into one lifecycle.

### SlackWorkspaceEnrollment

This is a Workspace-level aggregate. By default its key has **no Project**: one
Workspace Mohist App is a Server-installation control plane that multiple
Projects may reference. If the product explicitly requires Project isolation,
the product specification must first change this to one enrollment per Project;
the table shape must not decide accidentally.

Within one Server installation, an active `team_id` resolves to at most one
Enrollment. Two active records for one Workspace would create competing
provisioning authorities and make manifest, credential, and capacity decisions
ambiguous.

It owns:

- stable `team_id`, Mohist App external identity, and enrollment lifecycle;
- Mohist App capability to manage Agent Apps, the last verification facts, and
  plan/capacity diagnostics;
- Mohist App credential references, never plaintext, as specified by Security;
- audit facts triggered by Mohist management operators.

It does **not** own Agents, Connections, or Agent Apps and does not turn Slack
members into Mohist administrators.

### ManagedSlackAgentApp

Each managed Agent App has one aggregate. `install-agent` is an application
operation, not an aggregate name. It coordinates App creation, Workspace
installation authorization, and Mohist binding; the aggregate stores only the
Agent App's own external facts.

`ManagedSlackAgentApp` references the target `AgentConnectionId` but is not a
Connection child, and the two cannot change in one transaction. Connection
remains authoritative for Agent/Workspace/provider identity, access policy, and
enable/disable lifecycle. AgentApp is authoritative for the Slack external App
lifecycle and management facts.

One external `app_id` belongs to at most one AgentApp in its Workspace, and one
AgentApp references only one Connection. Otherwise an external create/delete
result could be applied to two lifecycle records or two Agent identities.

It owns:

- `enrollment_id`, stable Agent App ID, and external `app_id`;
- desired and applied manifest version, canonical hash, and verified scopes;
- App create/delete, installation/approval, and Socket configuration facts;
- operation fence, unknown outcome, error classification, and audit.

Do **not** add a durable process-manager aggregate here. Slack create/delete is
an external side effect of AgentApp itself, so its fence stays in AgentApp. The
architecture rule for a process manager is that it stores pending commands but
not business facts; see
[`architecture.md`](architecture.md#durable-application-process-manager).
AgentApp must store business facts and therefore cannot be that process manager.
Binding across AgentApp and Connection advances as:

```text diagram
AgentApp commits fact -> durable handler -> idempotent Connection command
```

It never uses a cross-aggregate transaction.

### Four-Axis State and One Next Action

AgentApp state must **not** be one giant enum. It has at least four axes and one
derived next action:

1. **App lifecycle:** `not-created` / `creating` / `create-unknown` / `created`
   / `deleting` / `delete-unknown` / `deleted`.
2. **Authorization:** `not-started` / `awaiting-user` / `pending-admin` /
   `authorized` / `expired-or-cancelled` / `revoked`.
3. **Manifest:** `desired` / `applied` / `drift-known`.
4. **Socket readiness:** Bot token and App-level token are persisted, both
   identities are verified, and the adapter lease is alive. Missing either
   runtime credential forbids Ready.

Unknown states, `create-unknown` and `delete-unknown`, may be left only through
reconciliation or explicit human arbitration. A process restart must **not**
repeat create/delete automatically. A definite failure may create a new attempt
on the same AgentApp, never a new Connection/Bot target. Cancelled installation,
expired authorization, and pending approval all resume the same AgentApp
instead of creating a new Bot.

### Credential Ownership

Credentials are addressed by their actual owner. Connection neither owns nor
copies Agent App runtime credentials:

- Mohist App runtime credentials belong at the Enrollment address.
  `ManagerCredentialRef` is an opaque persisted enrollment reference. CLI or an
  HTTP caller cannot supply any address component. Bot token and App-level token
  use distinct `SecretKind` values under one owner reference and must not be
  merged into an untyped secret. `mo slack setup` is the only normal provision,
  repair, and rotation entry point. Server finds the persisted reference through
  the active enrollment's Workspace team. Repeated setup resumes one record and
  never creates another Mohist App.
- Agent App client/signing secret, App-level token (`xapp-`), and Bot token
  (`xoxb-`) belong at the AgentApp address.
- Connection obtains data-plane credentials only through an active AgentApp
  binding.

Removing a Connection does not delete its Slack App by default. Addressing App
credentials by Connection would therefore couple two independent lifecycles and
could destroy credentials while the external App remains managed. Runtime and
provisioning credentials must be addressed by Enrollment or AgentApp ownership;
Connection receives only the temporary authority needed for an active data-plane
binding.

A secret-provision endpoint accepts only operator-authenticated loopback
requests. Its body may identify the target installation and carry only the
credentials required by that step. The caller cannot submit a credential ref,
secret kind, or secret address. Credentials come from hidden CLI input or a
protected, user-owned, non-symbolic-link file. HTTP responses, status, errors,
logs, audit DTOs, and documentation examples contain no credentials. Server
returns only non-sensitive Workspace identity and provisioned confirmation.
Status exposes Bot/App-level provisioning and verification separately. Mohist
App may become Ready only after both are valid and Socket hello is confirmed.

Credential submission must converge in this order: verify Bot and Workspace
first; write an App-level token only as an unverified candidate at its owner's
secret address; a validation lease may accept no business traffic; bind and
grant a runtime lease only after Socket App identity verification. Failures
across secret store and database must be recoverable. The system must never
reach 'Connection bound and usable, token not persisted.' Repeating the same
verified credential set returns the same result without rebinding. A candidate
for a different App/team/Bot is deleted and remains unusable.

### App Provisioning Credentials (Slack Configuration Token)

App management uses one **Workspace-level Configuration access/refresh token
pair** as its provisioning credential. It creates and updates manifests and
queries external lifecycle for Mohist App and all Agent Apps. This is separate
from Mohist App runtime credentials, including the Bot token received after
Slack installation. The first authorizes creating and maintaining Apps; the
second authorizes messaging as the Bot. Their addressing, rotation, and
invalidation are independent. They must never be mixed or derived from each
other.

- **Ownership and addressing:** the Configuration pair is an external
  credential scoped by provider identity and Workspace. Store it at the
  Enrollment address, alongside Mohist App credential references as its
  provisioning part. The database stores only references and metadata:
  provisioned time, source, and rotation generation. Serialization, audit,
  errors, and logs never contain plaintext.
- **One provisioning path:** setup guides the user to create a Configuration
  access token and refresh token in Slack App management, then submit the pair
  once through protected input. Input is hidden, revocable, and replaceable.
  Do not assume an official Slack CLI or other tool exists in the environment;
  product documentation and CLI guidance never mention one.
- **Rotation:** when the access token expires, Server calls tooling-token
  rotation with the refresh token and retries the original request. Atomically
  replace the old pair with the returned access/refresh pair and provider
  `team_id`. Rotation is reactive and transparent, with no background timer.
  The API returns a new one-time refresh token. If the network result is
  unknown, do not retry blindly; mark `credential-rotation-unknown` and require
  the user to provide a new pair. Enter Degraded only when the refresh token is
  also invalid. Rotation failure reduces App-management capability but does not
  interrupt the Socket data plane of installed Apps.
- **Invalidation:** Slack revocation or expiry appears as authentication failure
  on an App-management call. Mohist App capability in Enrollment becomes
  Degraded, with one next action: rerun `mo slack setup` to provide App
  provisioning credentials. Existing AgentApp and Connection data planes, which
  use Bot tokens, remain unaffected. Creation, resumed installation, and
  manifest repair remain blocked until reprovisioning. Do not amplify external
  failure with automatic retries.
- **Audit:** every external write using provisioning credentials, including
  create/update manifest and token rotation, records actor, object, and result,
  never the token.
- **Local authorization boundary:** Manifest API returns App identity, client
  credentials, and installation link, but the installer still confirms
  authorization on a Slack page. A Slack OAuth callback requires HTTPS; a local
  headless deployment has no public callback. CLI therefore accepts the Bot
  token shown after installation through hidden input or a protected file.
  Never use a user's Slack login Session, Slack CLI credentials, or browser
  automation to bypass this boundary.
- **Socket-token boundary:** the user creates an App-level token in App settings
  with only `connections:write`. Mohist records readiness only after verifying
  that it can open a Socket for the expected App.

The Server Slack control plane reaches Slack HTTPS through exactly four narrow
capability ports. This separates credential rotation, external App mutation,
Bot identity, and caller admission so no operation receives authority it does
not need:

- `SlackConfigurationCredentialPort` rotates a Configuration pair and returns
  the new pair, `team_id`, and expiry. It performs no App management.
- `SlackAppManagementPort` validates, creates, updates, exports, and deletes
  manifests. It returns App identity, client credentials, installation link,
  and definite/unknown outcomes. It neither installs an App nor opens a Socket.
- `SlackBotIdentityVerificationPort` uses a candidate Bot token to return
  verified team/Bot/scopes facts and sends no user message.
- `SlackMemberIdentityPort` uses a verified Bot token with `users.info` and
  `conversations.info` to return sender membership and Bot channel membership.
  Only Allowlist/Anyone admission calls it; owner/DM fast paths do not.

These ports isolate Slack SDK and HTTP shapes from domain and application
services. Socket
`apps.connections.open`, hello, events, interactions, and message delivery
belong only to `mohist-slack`; Server does not implement a second temporary
WebSocket client to verify `xapp-`.

### `setup` / `install-agent` Orchestration

CLI and Mohist App do not implement installation separately. Both call the same
Server application service. On each call, the service reads current aggregate
facts, performs at most one unconfirmed external write, and returns complete
progress with one next action.

Before creating an external App, the first `mo slack setup` obtains the
provider-confirmed Workspace `team_id` from successful Configuration token
rotation and uses it as the idempotency key. An existing enrollment resumes by
persisted identity or explicit `--workspace-team`; rotation occurs only near
token expiry or when the user reprovisions:

1. Accept the Configuration access/refresh pair through protected input, rotate
   once to verify it, atomically persist the returned pair and `team_id`, and
   create or resume `SlackWorkspaceEnrollment`. Do not create an App when the
   rotation result is unknown.
2. Generate the canonical Mohist App manifest and call validate/create through
   the App-management port. If create outcome is unknown, persist the fence and
   stop; never resend.
3. Persist `app_id`, client/signing secret, and installation link before asking
   the user to confirm Slack installation.
4. Accept Bot/App-level tokens through protected input. Verify Workspace and Bot
   first, then write candidate secrets at the Enrollment address. Credentials
   become verified and Enrollment becomes Ready only after the adapter reports
   the expected App's first Socket hello under a validation lease. Delete a
   mismatched candidate.
5. Ensure the built-in `mohist-slack` Agent and its management actor binding,
   code name `ManagerActor`, exist. A setup rerun repairs only drift, missing
   credentials, or connection and never creates a second Mohist App. Explicitly
   supplying valid credentials for a Ready record rotates them.

The idempotency key for `mo slack install-agent <agent>` is
`(enrollment_id, AgentId)`:

1. Resolve and authorize reading the existing Mohist Agent. If the Workspace
   already has a non-deleted Connection, return and resume it. Otherwise first
   create a Connection with fixed team and empty App/Bot identity, then create
   its `ManagedSlackAgentApp`.
2. Generate the canonical Agent App manifest, validate it, and perform one
   create. Durably save returned `app_id`, client/signing secret, manifest hash,
   and operation fence before exposing any user-visible link.
3. Return the installation link. After user confirmation or administrator
   approval, the same command accepts Bot/App-level tokens. Server verifies
   team/Bot through control-plane `auth.test` and Bot identity lookup, then
   stores candidate secrets at the AgentApp address while keeping them
   unverified and leaving Connection unbound.
4. The adapter obtains one validation lease, calls `apps.connections.open` with
   the candidate App-level token, and reports Socket `hello.app_id`. Only after
   Server verifies the expected App does it mark credentials verified. On
   mismatch, delete the candidate and leave Connection unbound. AgentApp then
   commits a bindable fact, and a durable handler idempotently fills Connection
   App/Bot identity. Installation projects Ready only after the adapter first
   obtains a runtime lease.

A rerun repairs drift, missing or invalid credentials, and connection without
creating another Connection or Agent App. If persisted credentials no longer
verify, return to the credential step. Explicitly reprovisioning valid
credentials on a Ready record rotates them, but they must still resolve to the
same team/App/Bot identity or the operation is rejected.

The conversational 'install Agent' operation performs only non-secret steps of
this same flow and returns the same progress. At an installation-confirmation or
credential step, it provides the link and the local continuation command
`mo slack install-agent <agent>`. Chat text is never a secret-input channel.

### Canonical Manifests

The Mohist App manifest enables Socket Mode, the App Home messages tab,
`message.im`, and interactivity. Bot scopes are only `chat:write`, `im:history`,
and `users:read`, which are required for management DMs. The Agent App manifest
enables Socket Mode, the App Home messages tab, `app_mention`, `message.im`, and
interactivity. Bot scopes are fixed as `app_mentions:read`, `channels:history`,
`channels:read`, `chat:write`, `groups:history`, `groups:read`, `im:history`,
`reactions:read`, `reactions:write`, and `users:read`.

`channels:read` and `groups:read` support Allowlist/Anyone admission by checking
Bot channel membership through `conversations.info`. Sender membership uses
`users.info`, already covered by `users:read`. DM fast paths and owner checks do
not call `conversations.info`, so neither `im:read` nor `mpim:read` is requested.
Group DM is unsupported in the first version, so `mpim:history` is also absent.
Interactivity returns through Socket Mode and has no Request URL.

Canonical serialization precedes hashing over manifest version, product
capability version, and identity snapshot. Drift exists only when
version/capability/identity or canonical content changes. Slack round-tripping
an omitted Boolean as `true` compares under true-or-omitted semantics and must
not create false drift.

### Socket Leases and Adapter Discovery

Server grants two kinds of short lease. A validation lease allows the adapter
only to open one Socket with a candidate App-level token and report
`hello.app_id`; it cannot accept ingress or claim outbox work. A runtime lease is
available only to a credential-verified active Mohist App or a
credential-verified Agent App whose Connection is Enabled. `mohist-slack`
discovers targets and renews through operator-authenticated loopback transport.
Only a lease response may contain a secret; status, list, and view DTOs never do.
When the adapter disconnects or a lease expires, Server stops granting it
delivery intents. A new adapter may take over only after the old lease expires.
Mohist App runtime ingress routes to the constrained management actor instead
of a normal Agent Connection access policy.

At issue time, a lease pins its credential generation through a SHA-256
fingerprint, not plaintext or a reversible derivative. A validation lease pins
the candidate App-level token; a runtime lease pins the verified pair. After a
candidate is reprovisioned or a verified pair rotates, renew and hello on the
old lease fail closed: renew is rejected, hello returns stale, and hello from an
old token must not reject or delete the new candidate. The holder reacquires to
receive the new credentials.

Before writing the lease store, acquire resolves the secret and rechecks target
state. Resolution failure, missing candidate, or a target no longer leasable
grants no lease and does not displace the existing holder; a failed path leaves
no inert active lease. In the promote crash window, where the candidate was
cleared before Verified was persisted, the target remains AwaitingSocket and
validation acquire fails cleanly. The operator can reprovision a candidate and
the same hello flow converges.

Every Socket envelope carries and validates `api_app_id + team_id` before
resolving Enrollment or Connection. An unknown App/team is acknowledged and
rejected, never routed by Bot name. The acknowledgement still means only Server
durable acceptance and never waits for Agent execution.

## Session Boundary

Buzz reuses an Agent Session by channel because its channel is the continuous
collaboration boundary. Slack organizes one conversation as a thread. Mohist
therefore uses `Agent + thread` as the Session boundary instead of sharing one
context forever across a channel.

Two rules follow:

- One thread may contain multiple AgentSessions, one per Agent, each with its
  own mapping and context. Mentioning a new Agent for the first time in an
  existing thread neither switches nor contaminates the original Agent Session.
- Do not start work when ownership is ambiguous. If one message mentions
  multiple Mohist Bots, or a thread is bound to multiple Bots and an unmentioned
  reply arrives, ask the user to choose instead of guessing.

DM is the exception. Slack DM users do not organize work by thread; treating
each message as new work would split a continuous thought into separate jobs.
Server stores one current AgentSession per Connection DM conversation and every
normal message continues it. The first version provides no 'new task' operation
inside DM. Parallel work uses separate channel threads, each with an independent
Session.

Different Mohist Servers do not share thread routing. The first version does not
promise coordination among Bots managed by different Servers in one Workspace.

## Reliability Contract

Slack-to-adapter transport is externally at-least-once; the system cannot claim
end-to-end exactly-once. Mohist instead ensures that duplicate events do not
repeat domain effects, confirmed replies are not resent, and uncertainty is
visible when results cannot be confirmed.

- Deduplication occurs in Server. The same Slack message identity that became
  input always resolves to the same SessionInput.
- Provider inbox and SessionInput deduplicate separately. Redelivery after a
  lost request result neither accepts the event nor input twice.
- Input accepted by Mohist is a SessionInput and cannot be deleted through
  drop-oldest or similar policy. Reject new input when capacity is exhausted.
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

Control-plane create/delete is likewise an at-least-once external side effect.
A repeated attempt does not repeat App creation/deletion. An unknown result is
exposed as unknown and converges only through reconciliation or human
arbitration under Four-Axis State.

### State Projection and Message Identity

Server is the sole judge of AgentSession and AgentTurn state. Slack provider and
the `mohist-slack` adapter project only Server-confirmed state and results. They
cannot infer work success from a Slack API response or read Runner output to
decide state.

An accepted Slack input uses stable `SlackMessageIdentity` as input identity and
derives one `DispatchRef` for the whole work item. For each `DispatchRef`, Server
allows only:

- At most one replaceable progress projection. After creation, persist provider
  message identity: target conversation and provider message timestamp.
  Working, latest stage, and terminal updates target that same identity and do
  not create a second progress message.
- At most one terminal result projection, deduplicated by a stable terminal
  delivery key. If a progress message exists, Completed, Needs attention, or
  Failed replaces it in place. Otherwise send one final answer in the same
  thread.
- Fast work may omit progress and project only Received reaction plus one final
  answer. If the platform cannot react on the user's message, project Received
  reaction on the unique progress message instead.

Default reactions are `Received=👀`, `Working=⏳`, `Completed=✅`, and
exception state `⚠️`. Reactions are liveness signals, not Session/Turn facts;
missing, late, or successful reaction mutation does not change Server state. A
reaction mutation carries stable provider identity for its target. State
arbitration never enters provider or adapter.

### Reply Body Belongs to Agent; Liveness Belongs to Server

Reply presentation has two separately owned layers:

- **Server owns liveness projection.** `SlackStatusProjection` maintains
  Received/Working/Completed reaction mutations and one replaceable progress
  message with a progress `DispatchRef`, independently of the Agent. Projection
  starts as soon as input is accepted. Whether the Agent sends a reply does not
  affect liveness closeout. Reaction mutation is best-effort: a bounded, logged
  failure never blocks or fails a Turn — as in Buzz, reactions are cosmetic
  liveness, never work state.
- **Agent owns reply body.** During a Turn, the Agent actively sends what it
  wants to say through a Mohist **reply action**, an intent API to Server carrying
  body and reply anchor. The reply action enters the same outbox and reuses
  redaction, duplicate protection, anchor validation, and Delivery uncertain
  reconciliation. Agent never connects directly to Slack. Prefer replacing the
  progress message for that `DispatchRef` with the final answer to avoid a
  second message.

Server never extracts assistant text from Runner Turn output. Turn output is a
Runner fact used to judge Session/Turn state; it is not a reply source, a
command channel, or a projection input. The Agent-originated reply action is
the only reply source. Terminal handling owns liveness closeout for every
session kind, including Manager sessions: every accepted input's liveness
reaches a terminal projection (Completed reaction, attention reaction, or one
explicit failure notice) on every outcome — completion, failure, cancellation,
Agent crash, or Server restart. This is the durable counterpart of Buzz's
drop-guard reaction cleanup: the outbox persists the closeout intent, so no
exit path leaves liveness open.

- **Silence is valid.** If a Turn ends with no reply action, the Agent chose
  silence. Liveness closes normally by completing the progress message and
  Completed reaction. No reply body is produced and Server invents no status
  summary.
- **Agent reports failure.** On execution failure or required human action, the
  Agent sends reason and next step through reply action. Only an Agent process
  crash or complete non-response permits a system-authored fallback explicitly
  labeled as a system failure.

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

- **Explicit destination:** `--conversation` is required and `--reply-to`, the
  thread anchor, is optional. Agent reads both from injected `SlackReplyAnchor`
  instead of choosing a historical message from memory. Sending elsewhere, such
  as another channel or a new thread, states that intent explicitly. This does
  not conflict with no guessed reply location, which governs replying to the
  current input.
- **Body through `--text`:** accept a string or `-` for stdin so shell escaping
  does not consume newlines. Agent writes standard Markdown. The CLI/outbox
  renderer converts it to Slack mrkdwn, including bold `**x**` to `*x*`, italic
  `_x_`, links such as `<url|text>`, inline code, fenced code, lists from `-` to
  `•`, and quotes. Unsupported tables and headings degrade to readable plain
  text or bold without error. Do not convert inside code spans.
- **Mentions in body:** CLI resolves `@displayname` to a valid Slack mention such
  as `<@U123>`. Ambiguity or an invisible target returns an actionable error and
  does not send silently. There is no separate `--mention` argument.
- **Images:** `--file <path>` uploads a local image through Slack `files.upload`
  with preview and repeatability. `--image <url>` references a public URL in a
  Block Kit image block. An Agent may explicitly send a useful screenshot or
  artifact. This does not conflict with the product rule against automatically
  copying artifacts into Slack; that rule governs system-generated clutter.
- **Success means outbox acknowledgement:** send synchronously commits to the
  outbox and returns confirmation that reliable delivery was accepted, not that
  Slack already displayed it. The outbox handles eventual Slack failure as
  Delivery uncertain without reporting it back into Agent execution.
- **Multiple sends:** within one Turn, Server coalesces sends into one final
  answer for that `DispatchRef`, preferring in-place progress replacement. The
  invariant remains at most one final answer per input.
- **Unsupported in the first version:** broadcast across channels and distinct
  message types. `mo slack message` is a command group, not an isolated command;
  future `message get` and `message thread` may let an Agent fetch context. Only
  send is implemented first.

### Delivery Intent, Claim/Ack, and Unknown Results

Server persists one `DeliveryIntent` for every logical projection. An intent
contains at least Connection, `DispatchRef`, target conversation/thread,
projection kind, stable deduplication key, current provider message identity if
known, and a safely replayable content reference. Projection kinds are
replaceable progress, terminal result / explicit failure, user action, and
reaction mutation. Tool calls and Runner logs are not user messages.

The lifecycle is:

1. **Pending:** Server persisted the projection intent and has not called the
   provider.
2. **Claimed:** one adapter holds a short lease on the intent. Claim is not
   delivery success, and a second adapter cannot project the same intent.
3. **Delivered/Acked:** provider returned definite success and Server persisted
   provider message identity or confirmed the mutation. Duplicate ack is
   idempotent.
4. **Retryable:** provider definitely rejected for a retryable reason. Return to
   the same intent; do not create another progress or final answer.
5. **Uncertain:** request timeout, connection loss, or an unparseable response
   leaves provider side effects unknown. Never resend blindly. Reconcile by
   stable identity first; retry the original intent only after confirming no
   side effect occurred.
6. **Dead-letter/Needs attention:** after a definite non-retryable failure or
   human intervention, retain the original intent, reason, and actionable next
   step. Do not rewrite an AgentTurn's confirmed result as provider failure.

`chat.update`, reaction add/remove, and progress creation all follow the same
claim/ack/uncertain semantics. If provider message identity for an existing
status message is missing or update outcome is unknown, reconcile before
updating. When update is confirmed impossible, Server may append exactly one
final answer in the same thread for that `DispatchRef`. The fallback has its
own stable terminal delivery key, so retry, reconnect, and duplicate ingress do
not append a second final answer.

### Capability Boundaries

The integration separates four capabilities because each has a different
authority and failure mode:

- **Ingress translation** belongs to `mohist-slack`. It normalizes Slack wire
  events and acknowledges only a definite Server decision. It does not authorize
  the caller, choose a Session, or invoke an Agent directly.
- **Admission and execution arbitration** belong to the Server Connection
  boundary and Agent API. They authorize stable Slack identities, deduplicate
  input, choose start or Follow-up semantics, and expose canonical Session/Turn
  state. A provider response cannot alter those facts.
- **Reply intent and liveness projection** are separate Server capabilities.
  The Agent authors reply body through the reply action. Server derives only
  liveness from canonical execution state. This separation makes silence valid
  and prevents status rendering from inventing Agent speech.
- **Provider projection** starts from a durable `DeliveryIntent`. The adapter
  translates it to Slack post, update, upload, or reaction operations under one
  Socket lease. Stable provider identity, claim/ack, reconciliation, and the
  single terminal fallback belong to this boundary. It cannot reinterpret work
  state or choose different reply content.

Markdown conversion, segmentation, attachments, and Slack control-syntax
escaping are provider rendering concerns after Server redaction. They cannot
change reply authorship, target identity, deduplication, or the rule that one
input has at most one final answer. Manager and Agent Connections use the same
boundaries; the Manager adds only the capability credential and its
Agent-facing management surface.

When Connection is Disabled, adapter/Server still acknowledges the Slack event
as handled at the transport layer, records audit, and discards it. Transport
acknowledgement is not Connection acceptance: the boundary must not create or
accept `SessionInput`, `AgentJob`, or any `DeliveryIntent`, and cannot replay the
event after re-enable. Already accepted Agent work remains under Mohist
arbitration, but the adapter cannot claim or send new Slack replies while
Disabled. After re-enable, project only still-relevant current or final state,
never stale Working progress from the disabled interval. Remove binding and
Permanent delete remain distinct, explicitly confirmed lifecycle operations.
The first retains Agent App management facts; the second requires no active
binding, a second confirmation, and audit. Neither deletes the Mohist Agent or
AgentSession.

## Security Boundary

- All Slack ingress uses Socket Mode; no public ingress endpoint is required or
  opened. A proxy may be configured explicitly toward Slack, but adapter
  transport to the loopback Server must not use that proxy.
- `mohist-slack` is a privileged local component deployed in the Mohist Server
  trust domain. It receives only enough authority to call fixed Connections,
  read results, and return messages.
- The Stop-request signing key and a Connection BotToken share one self-hosted
  installation trust domain. The adapter may hold the key and use it to forward
  a request, but Server revalidates signature, target Connection, actor, and
  executing Turn before deciding to stop. Neither is a Slack user credential,
  and neither may cross a trust boundary.
- Server encrypts App and Bot credentials, Mohist App credentials, and Agent App
  client/signing secrets and addresses each by its owner under Credential
  Ownership. They never enter Agent Instructions, transcript, logs,
  client-visible state, durable rows, DTOs, or audit serialization. CLI releases
  its transient secret buffer immediately after submission; it never enters
  shell history, command arguments, or process environment.
- Member authorization uses stable Slack Workspace identity, never display name,
  avatar, or message text.
- Permission to invoke in a channel effectively borrows the Agent's configured
  execution capability, including repository writes, tools, and credentials.
  Access policy is therefore an authorization decision, not a convenience
  toggle. Imported thread history is untrusted input whose maximum impact is
  bounded by Agent configuration.
- Audit records Workspace, conversation, and member identity for every call,
  but these identities do not become Mohist administrators. Mohist App
  installer, Agent owner, Connection owner, and ordinary caller are four
  distinct roles.

The first version provides no public App Marketplace, multi-tenant hosting,
billing, or cross-organization identity federation. Those requirements change
installation, authorization, and operations and belong to another product
phase.

## Non-Goals

- A Slack Bot does not run Agent Runtime or own another Agent configuration.
- The adapter holds no state requiring backup or recovery.
- Mohist App neither replies on behalf of an Agent nor becomes a shared
  execution identity for multiple Agents.
- Slack does not reproduce the Agent editor, Workflow board, or full diagnostic
  workstation.
- A shared Bot never guesses a target Agent from natural language.
- The first version excludes Slack-native Agent Messages, Agent Home, and
  streaming replies.
- Local Socket Mode is not zero-step automation. The installer confirms Slack
  installation, waits for administrator approval when Workspace policy requires
  it, and supplies installation result and App-level token through protected
  local input. Never request the user's Slack login Session or require Slack CLI.
- This document does not fix API routes, storage fields, locks and lease
  durations, Slack SDK version, or exact retry timing.

## Status

The data-plane and control-plane boundaries are implemented. Server owns
Enrollment, managed Agent App, Connection, inbox, conversation mapping, outbox,
and lease facts. The stateless adapter owns only Socket protocol translation and
provider calls. `mo slack setup`, `mo slack install-agent`, and Mohist App
conversation enter the same resumable control-plane operations. Canonical
manifests, protected local credential entry, staged binding, identity-checked
Socket leases, access admission, thread/DM Session mapping, Stop, and stable
delivery projection are available. The local setup path has no OAuth callback or
plaintext-token control path.

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

Public App Marketplace, multi-tenant hosting, coordination across Mohist
Servers, Slack-native Agent ingress, App Home, and complete scale and operations
experience remain future phases. Future capabilities must still enter through
Agent API and the existing Connection boundary. The adapter must never parse
Runner logs, override Agent configuration, or write the Mohist database
directly.
