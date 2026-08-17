# Slack

Slack is one Mohist interaction interface, alongside the Web UI, CLI, and CI.
This document defines the Slack integration.

Mohist appears in a Slack workspace as two kinds of App:

- The **Mohist App** is the management entry point. Users talk to it to connect
  the workspace, install and adjust Agents, view status, and create Agents.
- An **Agent App** is an execution entry point. Each connected Agent has its
  own Slack App and Bot identity. Users give it work in channels and direct
  messages, and results return to the same conversation.

Management and execution never share an identity. The Mohist App never sends on
behalf of an Agent.

An **Agent Connection** presents one configured Mohist Agent in Slack under one
Bot identity. Slack delivers a mention or DM to that Agent, and the result
returns to the same conversation. Slack does not run a model, store a second
copy of the Agent definition, or decide work state. Removing a Connection
leaves the Agent available from every other interface.

A Connection is not a notification. A notification pushes a change one way. A
Connection starts work, continues a session, stops execution, and returns the
result.

### Management Plane Trust

The current management plane trusts the deployment boundary: any caller that
can reach it can request management operations. Expose it only on a trusted
local or administration network. Slack installation proves control of the Slack
workspace and App; it does not grant Mohist management permission. Per-caller
authentication, permission isolation, and attribution remain an implementation
gap. See [Authentication and Access](auth.md).

## Installation Model

Install the Mohist App once per workspace. It binds the workspace to one Mohist
deployment and creates one dedicated Slack App and Bot identity per connected
Agent. The visible identity decides which Agent a user invokes. One Agent
produces one App and one Bot in one workspace; an interrupted or repeated
installation resumes the same record and never creates a duplicate Bot.

The Mohist App discovers, installs, diagnoses, disables, and uninstalls Agent
identities. It neither executes work for an Agent nor changes the Agent's
configured capabilities. Connecting a workspace or becoming an Owner grants no
Mohist management permission by itself.

### Local Socket Mode

The integration is local and self-hosted. A local `mohist-slack` service opens
an outbound Socket Mode connection for the Mohist App and every Agent App.
Users configure no public domain, callback service, or Slack login session.
Each App keeps its own credentials.

A member allowed to install Apps must confirm each Slack installation, and
workspace policy can require administrator approval. Socket Mode also needs an
App-level token with only `connections:write` per App. Slack's public App
management API cannot return that token; the user generates it on the App
settings page. Mohist cannot perform that step.

### App Provisioning Credentials

Mohist creates and maintains Apps through one workspace-level **Configuration
token pair** (access + refresh token):

- **Provide once.** `mo slack setup` directs the user to Slack's App management
  page to generate the pair, submitted through protected input without echo.
  The user can revoke and replace it at any time.
- **Self-recovering.** The access token is short-lived. Server rotates it with
  the refresh token and stores the new pair atomically. When the refresh token
  also fails, App maintenance becomes Degraded with one next step — rerun
  `mo slack setup` — while installed Bots keep delivering messages.
- **Runtime credentials stay per App.** After installation, the user provides
  the Bot token and the App-level token through protected CLI input. A local
  deployment has no HTTPS OAuth callback that Slack can reach, so Mohist does
  not pretend to receive these two results automatically.

Mohist automates what the platform permits and asks the user only for
authorization and the results Slack shows only to them. The guide is
self-contained: it assumes no Slack CLI and requests no Slack login session.
Credentials never pass through a Mohist App conversation, command argument,
log, or Session transcript.

## Talk to the Mohist App

Users manage the integration in natural language in the Mohist App's DMs:

- **Install an Agent** — "Install review-bot in Slack." The Mohist App creates
  a recoverable installation record and returns the authorization link. When a
  secret is required, it directs the user to `mo slack install-agent
  review-bot` on the Mohist host. Chat is never a secret-input channel, and a
  rerun resumes the same installation record.
- **View and diagnose** — "Which Agents are connected?" or "What is
  review-bot's status?" Answers come from the same facts as the Web UI and CLI:
  one current state and one next step.
- **Adjust lifecycle** — change access policy, enable or disable a Connection,
  or start Owner transfer. Permanent App deletion is not available in
  conversation; use the Web UI or CLI.
- **Create an Agent** — "Create an Agent that watches CI failures every day."
  The Mohist App asks at most two questions: the name and the daily purpose.
  Everything else uses defaults. After creation it guides Slack installation.

The Mohist App binds a built-in Mohist Agent named `mohist-slack`. Its
capabilities are the same operations the CLI and Web UI call, over the same
resources and semantics. Its conversation follows the same liveness and reply
contract as every Agent Connection (see [Present Replies](#present-replies)):
acceptance and progress are reactions, and the Agent authors replies through
the same send action. It reports only confirmed results. The conversation is
not a separate command channel: no hidden protocol, no message-shaped commands,
and no effects beyond those operations.

Only operators allowed to manage the target resource can drive the Mohist App.
An ordinary workspace member cannot obtain a management operation by talking to
it. By default, its DMs accept only the operator claimed during installation.
The principle matches execution: the ability to invoke means the ability to use
what is behind it.

## First-version Boundary

- One Connection binds one Agent in one Project to one Bot identity in one
  Slack workspace.
- One Agent can have Connections in several workspaces, each with independent
  permissions, state, and thread mappings. In one workspace, an Agent has at
  most one non-deleted Connection. One Bot can join multiple channels; do not
  copy a Connection per channel.
- Each Agent has its own Bot identity. There is no shared `@mohist` Bot that
  infers an Agent from message text; the Mohist App is a management entry
  point, not a shared execution identity.
- The local `mohist-slack` service only translates protocol. Agents, sessions,
  and pending delivery live in Mohist.
- DM entry is one-to-one only. Team discussion uses a channel thread the Bot
  has joined.
- Private Apps only. A public marketplace and multi-tenant hosting are a
  separate product phase.

## Before You Connect

- Connecting a workspace requires one authorization by a member allowed to
  install Apps, plus the one-time provisioning credentials. Many workspaces
  prohibit member installs by default; then the Mohist App, and later each
  Agent App, can each require administrator approval. Confirm the workspace
  policy before connecting Agents.
- Each Agent installation is a recoverable process with stable identity. After
  interruption, timeout, cancellation, or pending approval, it resumes the same
  record. At each step the user sees one current state and one next action.
- Agent Readiness and Connection installation are independent and can complete
  in parallel. Test the Agent from the Web UI or `mo agent launch` before
  inviting users. A Connection can be Ready while its Agent is not; new
  delegations then get an explicit, safe rejection. Slack never becomes a
  second source of Agent configuration, and the Connection page shows Readiness,
  installation progress, and connection health separately instead of hiding a
  gap behind "Connected."

## Installation Flow

`mo slack setup` and `mo slack install-agent <agent>` are idempotent, resumable
guides. Each advances every automatable step and stops only where Slack
requires a user action, naming the exact page, action, and continuation
command. A rerun reads durable progress and never creates a second App. A
Mohist App conversation starts the same Server-side flow and returns to the
local CLI for secret-bearing steps.

### Set Up the Mohist App

1. Run `mo slack setup` on the Mohist host. Without provisioning credentials,
   it directs the user to generate a Configuration token pair on Slack's App
   management page and submit it through protected input.
2. Mohist validates the workspace, creates the Mohist App, and shows its
   installation link. The user confirms in the browser; when administrator
   approval is required, setup retains progress and waits.
3. The user provides the Bot token and a `connections:write` App-level token
   through protected input.
4. `mo slack setup` validates workspace, App, Bot, and Socket, stores the
   credentials securely, and starts the local `mohist-slack` service. It shows
   Ready only after the Socket connects and the App identity is confirmed.

### Install an Agent

1. The installer selects from active Agents they may manage. Each shows its
   Slack state: Not installed, Action required, Pending approval, Installing,
   Ready, Degraded, Disabled, or Removed. Slack identity cannot reveal an Agent
   the installer may not manage.
2. The installer confirms the Bot name, avatar, and description, the requested
   Slack permissions with reasons, the default access policy, and the initial
   Connection Owner. The name is a suggestion, not an identity key; on
   collision Mohist proposes a name with a stable suffix.
3. Mohist creates a recoverable Connection and installation record fixed to the
   Agent and workspace, then creates the Agent App. If the create result is
   unknown, the record enters **Result unknown** and never creates a second App
   automatically; reconcile with Slack or arbitrate manually first, so one
   Agent still maps to one App.
4. The installer completes Slack installation authorization. Cancellation,
   expiry, and pending approval all resume the same App later; Mohist never
   creates another Bot.
5. After authorization, Mohist verifies that workspace, App, and Bot identities
   match. A mismatch stores no credentials and binds no Connection.
6. The installer provides the Bot token and App-level token through protected
   input. The Connection is not Ready until both validate. Credentials never
   appear in Instructions, messages, logs, or transcripts.
7. **Claim owner.** The installer generates a short-lived, single-use claim
   code and sends it in a DM to the Bot. The code is shown once; regenerating
   it invalidates the old code. Only a current full workspace member can claim;
   external collaborators, Bots, and deactivated members cannot. A successful
   claim also proves the App can receive and reply to DMs.
8. Select the channel access policy (default **Owner only**). Allowlist uses
   member search with names and avatars. Every policy accepts DMs only from the
   Owner. After claim and a healthy connection, state becomes **Ready**.
9. Invite the Bot to a channel, send a test task in DM, or mention it in a
   channel root message, then verify the result in Jobs and Sessions. When the
   Agent is not Ready, Slack shows a safe unavailability summary; only the
   Owner and Web/CLI operators see the specific gap.

The installation page shows Agent Readiness, installation progress, connection
health, and identity sync separately, and highlights one current state and one
next step.

### CLI

`mo slack` covers all Connection operations:

```text literal
mo slack setup
mo slack install-agent <agent> [--workspace-team <team-id>]
mo slack status
mo slack list <agent>
mo slack view <connection-id>
mo slack edit <connection-id> --access-policy allowlist --allow-member <slack-member-id>
mo slack disable <connection-id>
mo slack enable <connection-id>
mo slack transfer-owner <connection-id>
```

CLI and Web UI operate on the same Connection record. `install-agent` also
resumes a record created from a Mohist App conversation. App creation, manifest
updates, credential submission, and delivery recovery are internal installation
steps, not commands users orchestrate.

`--allow-member` can be repeated and replaces the complete member list except
the Owner. Owner only and Anyone reject `--allow-member` before modification.
Member IDs are the CLI automation interface; the Web UI and Slack use member
search and avatars.

## Agent Connection Configuration

| Setting | Meaning |
|---|---|
| Agent | Fixed at creation; create another Connection for another Agent |
| Slack workspace | Confirmed by Mohist App installation, never a user-entered name |
| Bot identity | Initialized from Agent name and avatar, then managed in Slack and verified by Mohist |
| Slack description | Initialized from Agent Description; never becomes Instructions or a hidden prompt |
| Runtime mode | Local Socket Mode; each App has its own Bot token and App-level token |
| Owner | Claimed Slack member; the only caller by default and always in the Allowlist |
| Access policy | Who can invoke in channels; Owner only by default |
| Allowed members | Workspace members who can invoke under Allowlist; DMs remain Owner-only |
| Installation progress | Not installed / Action required / Pending approval / Installing / Ready / Degraded / Disabled |
| Status | Setup required / Ready / Degraded / Disabled; Degraded carries one actionable reason |
| Identity sync | Whether Bot name and avatar still match the Agent |

Instructions, Runtime, Model, Variant, Skills, and concurrency limit are Agent
configuration, not Connection configuration. New work uses the new snapshot,
and the concurrency limit applies live to later launches and follow-ups.

Bot identity is not a second Agent configuration. After an Agent name or avatar
change, Mohist marks the identity out of sync and gives the Slack settings
entry point and the expected values; the user updates Slack and verifies again.
Identity drift never presents as a disconnected Connection. A Bot token cannot
read the full App configuration, so Mohist reports a gap only when a capability
actually fails and never claims synchronization it cannot verify.

## Slack Message Permissions

To follow up naturally in a bound thread, the App must receive message events
from channels it has joined. Mohist processes only Bot DMs, explicit mentions,
and replies in bound threads. Other channel messages are discarded before any
durable record or log.

The configuration page lists every requested permission with its reason. Invite
the Bot only to channels where it is needed. The first version has no
per-permission toggles, which would create states where Slack offers a
follow-up the App cannot perform.

Mohist reads the basic member directory — IDs, names, avatars — to select
Owners and Allowlist members and to show senders. It never reads member email,
never gives the directory to an Agent, and removes the data when the Connection
is deleted.

Socket Mode needs no public inbound address, but Slack retains unconfirmed
messages only briefly. When workspace policy allows, recommend **Delayed
Events**. Even then, recovery is not indefinite: after a long outage the status
page warns that messages may be missing and asks the user to resend critical
delegations.

## Use Slack

### Start New Work

New work starts from a DM when the conversation has no current Session, or from
a channel root message that mentions the Bot.

#### Setup Mode

Before a new launch is admitted, Mohist checks the Slack Connection and then the
bound Agent. If either is not ready, Slack receives one Server-authored message
that says the Agent or Slack Connection is not ready to accept the task and
points the caller to the responsible owner or operator. It does not expose
health reasons, readiness gap codes, configuration or credential details,
repair paths, or commands, and it never claims that execution started.

Owners and authorized operators can inspect the detailed Agent readiness gaps,
next actions, and repair entry points in the Agent and Connection diagnostic
surfaces. Agent readiness is independent from Slack Connection health and
installation state: a Connection can be healthy while its Agent needs setup,
and a Connection can be unavailable while the Agent remains executable.
Existing DM sessions and bound channel threads continue through their persisted
Session route; setup mode applies only to new launches.

After removing the mention, the message must contain task text or a usable
attachment. A bare mention gets a question, not an AgentJob. An attachment
alone is valid input; Mohist invents no hidden prompt.

On acceptance Mohist creates the AgentJob, AgentSession, first SessionInput,
and first AgentTurn. Acceptance is the **👀 (Received)** reaction on the user's
message, not an acknowledgement message. Liveness then shows whether the work
is running or queued. A queued or running Turn can be stopped. Agent replies,
failures, and conclusions that need human action return to the same
conversation. When the needed action is one Mohist can execute — such as
stopping or retrying a Turn — the notice carries a signed action button.
Pressing it performs the operation under the presser's authority with the same
result as the CLI or Web operation. A button is a shortcut to the same
operation, never a second command grammar.

Completing the AgentJob does not close the Session; when a reply asks a
question, the user answers directly.

### Continue the Same Session

In a **channel**, a reply in the bound thread follows up the bound Session. In
a **DM**, every ordinary message continues the one continuing Session, even
after a Turn ends. The first version has no "new task" control in a DM; the
Agent handles topic changes in the same context. Parallel work uses separate
channel threads.

Follow-ups never create another AgentJob and keep the Session's context. Every
message becomes a SessionInput with a stable identity. A follow-up received
during execution steers by default — it joins the current Turn or waits for the
next — and only an explicit stop interrupts. Each Session has a bounded queue;
at the boundary the Bot rejects new messages and asks the sender to retry
later. Accepted input is never discarded to make room.

One thread can host several Agents, each with an independent Session:

- One bound Agent: an unmentioned human reply continues its Session.
- Several bound Agents: an unmentioned reply is human discussion; the user must
  mention the target Bot.
- Mentioning another Mohist Bot for the first time starts an independent
  Session for it, without switching or contaminating the original context.
- One message mentioning several Bots starts no work and produces one
  interactive selection; choosing an Agent starts its work.
- A Bot's own message never becomes input — not for itself, not for another
  Bot. Separate Mohist Servers do not coordinate one multi-Bot message.

As in Buzz, several Agents can act as independent collaborators in one
discussion without triggering one another or making follow-up ownership
ambiguous.

### Mention in an Existing Discussion

A first Bot mention inside a human discussion passes the Bot-visible thread
history as initial context and treats the mention as the task. Oversized
context is truncated oldest-first and marked in both the Agent input and the
Slack confirmation — never silently. If permissions, rate limits, or a Slack
failure prevent a complete read, Mohist rejects the delegation and creates no
AgentJob.

Imported thread history is untrusted user input, not Instructions. Anyone may
have written it, so its maximum effect is the capability already granted to the
Agent. Consider that before widening a policy to Anyone. See
[Permissions](#permissions).

Editing an accepted message does not rerun it; send a follow-up. Deleting a
message does not remove the created AgentJob, Session, or audit record.

### Files and Links

The Bot reads files it can access that the message or thread explicitly
provides; they become input attachments with their source preserved. An
unreadable, oversized, or unsupported file is named as unused, not pretended
read. Links stay part of the message text: the integration fetches no URLs, and
the Agent opens one only within its configured Skills and Runtime permissions.

## Present Replies

Slack carries two signals with different owners: **liveness** — the system is
processing — owned by Mohist, and the **reply** — what the Agent chooses to
say — owned by the Agent.

- **Mohist owns liveness**: reactions (👀 Received, ⏳ Working, ✅ Completed, ⚠️
  exception) and one status message updated in place. Liveness starts at
  acceptance and does not depend on an Agent reply. Reactions are best-effort:
  a failed reaction never blocks or fails work. Every accepted input's liveness
  reaches a terminal form (✅ or ⚠️) on every outcome — completion, failure,
  cancellation, Agent crash, or service restart.
- **The Agent owns the reply**: the Agent sends content through Mohist's send
  action to the injected reply anchor. Reasoning, tool calls, and intermediate
  output never appear in Slack. Only actively sent content becomes a message.
  Mohist never extracts or invents a reply from an execution result, and no
  Mohist component interprets Agent output to produce Slack content or
  management effects.

The Web session timeline holds the complete execution record. When an External
Web URL is configured, **Open in Mohist** links to that timeline; otherwise the
message shows stable Job and Session IDs. Mohist never sends a localhost
address to Slack.

### One Input, One Answer

For each accepted input, Mohist adds 👀 to the user's message, adds ⏳ and one
status message while executing, and the Agent's reply replaces the status
message in place. One input has at most one status message and one final
answer; retries and duplicate delivery never create a second answer. Fast work
may skip the status message. If a status update fails, Mohist appends the final
answer once in the same thread and records a diagnosable delivery problem.

Reactions are liveness signals, not work facts. Success, cancellation, and
failure come only from confirmed Session and Turn state. Where the platform
cannot react on the user's message, the reactions appear on the status message.

- **The Agent is the speaker.** Status fields such as exit code, artifact
  count, or IDs are secondary metadata, never the answer.
- **Silence is valid.** A Turn that ends without a reply is not a failure.
  Mohist closes liveness normally and invents no summary.
- **A failing Agent explains its failure**, with reason and next step. Only an
  Agent crash or non-response permits a system fallback, explicitly labeled as
  a system failure.
- **Rendering is safe.** The Agent writes Markdown; Mohist renders Slack
  mrkdwn, degrades unsupported tables and headings to readable text, and
  renders all content as text, so a reply cannot trigger `@channel`, `@here`,
  or forge a control. Only Mohist creates real controls from current state.
- **Delivery is reconciled, never blind.** A definite rejection retries per
  platform rules and enters Degraded. An unknown result reconciles first; if
  uncertainty remains, the Connection shows **Delivery uncertain** and a manual
  resend warns about duplicates. A failed Slack reply never changes the
  execution result.
- **Pending delivery is bounded.** Replaceable progress can coalesce. Final
  results, definite failures, and user actions are never silently dropped; if
  they no longer fit, the Connection enters Degraded (Backpressured) and stops
  accepting Slack input while accepted execution continues.
- **Artifacts stay in Mohist.** The first version shows result text, links,
  artifact names, and stable IDs; it does not copy artifacts into Slack files.
  The reply still carries the conclusion and next step.
- After a restart or reconnect, delivery resumes from the last confirmed
  position without duplicating Jobs, Inputs, or confirmed replies.

## Slack Collaboration Rules for Agents

Mohist injects these rules into the Agent as a visible, evolving Skill. They
govern behavior in a shared space and change no capabilities:

- **You are the speaker.** Reply with the send action to the injected anchor.
  Reasoning and tool calls are invisible to Slack users; only sent content
  becomes a message. Send a useful conclusion; send nothing when there is no
  new information. Mohist will not derive or invent your reply.
- **No empty acknowledgements.** A message that only confirms interrupts the
  channel and can trigger other Bots. Silence is a normal completion, not a
  failure. The exception is a human's direct question: always answer it, even
  if the answer is only that you have nothing to add — never leave a person
  waiting.
- **Call back after a delegation.** When delegated work completes, mention the
  delegator in the result. Mention someone only when they need to act or notice
  the result; a narrative reference needs no mention.
- **Self-contained and proportionate.** Conclusion, evidence summary, and next
  step in Slack. Fine-grained progress belongs in the Web session timeline.
- **Never guess the reply location.** Mohist supplies the thread and message
  anchor for every input. Do not reply from memory or post into another
  channel.
- **Resume silently.** After a restart, Session recovery, or context
  compaction, rebuild state from durable records and the thread, and continue.
  Never announce the interruption or ask how to proceed.

## Permissions

| Policy | Direct messages | Channel mentions and bound threads |
|---|---|---|
| Owner only | Owner only | Owner only (default) |
| Allowlist | Owner only | Owner and listed workspace members |
| Anyone | Owner only | A verified full workspace member in a channel where the Bot is present |

**Anyone who can invoke the Bot can use every capability granted to the
Agent**, including repository writes, tools, and credentials. Widening a policy
is a permission grant, not a convenience switch. Do not use Anyone for an Agent
with repository write access.

This follows Buzz's DM hardening: DMs are Owner-only under every policy.
Allowlist and Anyone re-verify on every invocation that the sender is still a
full workspace member; deactivated, restricted, external, Bot, and unconfirmed
identities are rejected. Anyone also verifies that the Bot is in the sender's
channel. The Owner DM check does not depend on these lookups, so the Owner can
always invoke.

Channel membership decides only whether the Bot receives a message; it never
replaces the access policy. A Bot in a private channel still checks the sender.
Slack Connect participants and identities whose ownership cannot be confirmed
cannot invoke in the first version. An unauthorized user gets a short, explicit
rejection, and Mohist creates no AgentJob or AgentSession.

Only the Connection Owner or the Session starter can stop its Turn. A stale
button cannot stop a later Turn.

Access policy answers only who may invoke. It neither reduces nor expands the
Agent's execution capability, and a Slack message cannot add or replace
capability temporarily. Every input records workspace, channel, thread, and
sender for audit; a Slack identity is not a Mohist administrator identity, and
message content cannot switch Project, Agent, or policy. Only the Owner can
change invocation scope, through an explicit manage-access operation. A policy
change applies immediately to later inputs, including follow-ups, and does not
revoke accepted work or delete history. Only a Mohist operator can start an
Owner transfer: the system issues a new one-time claim, the old Owner remains
until the new one claims in a Bot DM, and the new Owner must be a current full
workspace member.

## Lifecycle and Failures

Three independent lifecycle actions, each explicitly confirmed:

- **Disable** pauses the Connection: no new Slack input or replies, while
  accepted execution continues. The Slack App and its management facts remain.
  Enable restores the previous state and projects only still-current results,
  never stale progress from the disabled interval.
- **Remove binding** detaches the Connection and clears runtime records —
  receipts, Session mappings, pending delivery — while preserving Agent App
  management facts. It does not uninstall the Slack App.
- **Permanent delete** deletes the Mohist-created Slack App. It requires a
  second confirmation, separate permission, complete audit, and no active
  binding. An unknown delete result is reported as unknown and reconciled or
  arbitrated, never claimed as success. Mohist deletes only Apps it created.

| Situation | Product behavior |
|---|---|
| Agent name or avatar edited | Bot identity marked out of sync; user updates Slack App settings and verifies again |
| Agent description edited | Expected Slack description updated; Agent behavior unchanged |
| Agent execution definition edited | New AgentJobs use the new snapshot; existing Sessions keep theirs |
| Agent concurrency limit edited | Applies to later launches and follow-ups; running input is not stopped |
| Agent archived | New root delegations rejected; existing Sessions remain readable and continuable |
| Agent becomes Needs setup | New delegations get a safe summary in the channel; Owner and operators see the specific gap; existing Sessions continue on their snapshots |
| Agent Readiness Unknown | Delegation accepted and waits for Runner validation; explicit failure if execution later proves impossible |
| Connection Disabled | Input and replies stop immediately; messages received while disabled are acknowledged and discarded, never replayed |
| Binding removed | Runtime records cleared; App management facts preserved; Slack App not uninstalled |
| Slack App permanently deleted | Second confirmation, no active binding, audit; unknown results reconcile |
| Agent App creation unknown | Enters Result unknown; no automatic second App; reconcile or arbitrate |
| Authorization cancelled, expired, or pending approval | Same Agent App resumes after recovery |
| Authorized identity mismatch | Nothing stored, nothing bound; one explicit next action |
| Slack credential invalid | Degraded; new input stops; rerun `install-agent` and verify the same identities |
| Agent already connected to the workspace | Points to the existing Connection; never overwrites |
| Socket Mode temporarily unavailable | Connection and progress preserved; Slack retries within its window; beyond it the user resends |
| Owner must change | Operator starts a new one-time claim; old Owner remains until success; a Slack sender cannot reset ownership |
| Owner leaves or is deactivated | Degraded (Owner unavailable); channel Allowlist and Anyone continue; DMs and Owner management wait for operator transfer; never transfers to a same-named member |
| Slack redelivers an event | Returns the existing Job, Session, Input, and Turn |
| Input delivery to execution unconfirmed | Marked Unknown and reconciled as the same Input, never replayed as new |
| Concurrency or Session queue limit reached | Shown as queued or rejected with retry-later; capacity is not execution failure |
| Pending-result capacity reached | Progress coalesces; Degraded (Backpressured); final results, failures, and user actions preserved |
| Owner or Session starter stops a queued Turn | Ended locally as cancelled; a first Turn's AgentJob ends with failure category `cancelled` |
| Slack cannot send a reply | Work result unchanged; definite rejection retries; unknown result shows Delivery uncertain without blind duplication |

## Non-goals

- A Slack Bot runs no Agent Runtime and owns no Agent configuration or hidden
  prompt.
- The Mohist App neither speaks for an Agent nor acts as a shared execution
  identity.
- Slack is not an Agent editor, Workflow board, Issue manager, or diagnostics
  console.
- No shared Bot guesses a target Agent from natural language.
- Ordinary channel messages are not all sent to Mohist; only DMs, explicit
  mentions, and bound-thread replies trigger it.
- No Slack-native Agent entry point, Agent Home, streaming replies, slash
  commands, or message shortcuts. Structured control in Slack uses signed
  action buttons; everything else is a message.
- No public marketplace, multi-tenant hosting, billing, Slack Connect
  invocation, cross-company discovery, group DMs, or cross-Server Bot
  coordination.
- Mohist artifacts are not copied into new Slack files.
- Installation is not one-command automation. The user confirms Slack
  installation, waits for required administrator approval, and provides the Bot
  token and App-level token that Slack shows only to them. Mohist automates
  creation, configuration, validation, and connection around those steps.

## Status

Delivered:

- Data plane: channel root mentions, bound-thread follow-ups, multi-Bot
  ownership selection, duplicate delivery protection, thread history import
  with marked truncation, attachments, and the Owner only, Allowlist, and
  Anyone policies. Anyone verifies that the Bot can see the channel; DMs are
  Owner-only under every policy.
- Reply contract: in-place status messages, the Agent-authored reply action,
  collaboration rules and reply anchor on every input, steer-by-default
  follow-ups, explicit Stop, and reconciled Delivery uncertain with a single
  fallback answer.
- Control plane: resumable `mo slack setup` and `mo slack install-agent`
  guides, one-time Owner claim, transparent Configuration-token rotation, and
  Owner transfer. Remove binding and permanent delete are independent,
  explicitly confirmed CLI or Web actions.
- End-to-end acceptance passed in an isolated real Slack workspace: setup,
  install-agent, Owner claim, DM task execution, and reply delivery over real
  Socket Mode.

Current gaps:

- Per-Connection selection of accepted channels is not delivered.
- Coordination between Bots managed by different Mohist Servers is not
  available.
- The Mohist App conversation is migrating to the standard liveness and reply
  contract. The current build still acknowledges management requests with a
  text message and performs management through a model-output protocol instead
  of the ordinary operations surface.

Future phases: Slack's native Agent entry point and streaming replies,
approval-gate notifications routed into Slack, message shortcuts, a public
marketplace, multi-tenant hosting, and a complete diagnostics console.
