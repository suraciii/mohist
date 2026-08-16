# Slack

Slack is one Mohist interaction interface. The same Agents and Workflows are
also available from the Web UI, CLI, or CI. This document defines the Mohist
Slack integration.

Mohist appears as two types of App in a Slack workspace. The **Mohist App** is
the management entry point. Users talk to it in Slack to connect a workspace,
install and adjust Agents, view status, create Agents, and perform other
management operations. An **Agent App** is an execution entry point. Each
connected Agent has its own independent Slack App and Bot identity, accepts
work directly in channels and direct messages, and returns results there.
Management operations and work tasks have clear, separate identities. One does
not send on behalf of the other.

An Agent Connection presents an already configured Mohist Agent in Slack with
an independent Bot identity. A user mentions the Bot or sends it a direct
message. Slack delivers the message to that Mohist Agent, and the result returns
to the same Slack conversation.

Slack does not run a model, store another copy of Instructions, Runtime, Model,
or Skills, or decide work state. After removal of its Slack Agent Connection,
the same Agent remains available from the Web UI, CLI, event routing, and
comment mentions.

An Agent Connection differs from a Hermes notification. A notification pushes
a change one way to a chat tool. An Agent Connection lets a user start work,
continue a session, stop the current execution, and receive the result.

### Current Development Management Plane

The current development management plane trusts the deployment boundary rather
than identifying and authorizing each caller. Expose it only on a trusted local
or administration network: any caller that can reach it can request deployment
management operations. Slack installation authorization proves control of the
Slack workspace and App, but it does not establish a Mohist operator identity
or grant Mohist management permission. Per-caller authentication, permission
isolation, and attribution remain an implementation gap. See
[Authentication and Access](auth.md).

## Installation Model

Install the **Mohist App** only once in a Slack workspace. It is the workspace
installation and operations entry point. It binds the workspace securely to a
Mohist deployment, then creates **a dedicated Slack App and independent Bot
identity** for each connected Agent instead of sharing one Bot across Agents.

The visible identity therefore determines which Agent a user invokes. Each
Agent remains an independent identity that can be mentioned natively, and its
own Bot always sends its replies. The Mohist App never sends on its behalf. The
Mohist App only discovers, installs, resumes installation, diagnoses, disables,
and uninstalls those Agent identities. It neither executes work for an Agent nor
expands or reduces the Agent's configured capabilities.

Slack remains the interaction entry point. Mohist remains the authority for
Agents, work, sessions, and invocation permissions. Connecting a workspace to
the Mohist App or making a Slack member the Owner does not automatically grant
Mohist management permission.

### Local Socket Mode

The Mohist Slack integration is a local self-hosted capability. The local
`mohist-slack` service actively connects the Mohist App and each Agent App to
Slack through Socket Mode. Users do not configure a public domain, public
callback service, or Slack login session for Mohist. One local service can hold
connections for multiple Apps, but each Agent retains its own App, Bot, and
credentials.

A member authorized to install Apps must confirm Slack installation, and
workspace policy can require administrator Approval. Socket Mode also requires
an App-level token with only `connections:write` for each App. Slack's public
App management interface can create and configure an App but does not return an
App-level token that Mohist can use. The official flow still requires token
generation on the App settings page, so Mohist cannot perform this step for the
user.

One Agent produces one App and one Bot in one workspace. An interrupted or
failed installation resumes the same installation record and App. Rerunning a
command does not create a duplicate Bot.

### App Provisioning Credentials

To create and continuously maintain the Mohist App and Agent Apps for a
workspace, Mohist requires one set of **workspace-level App provisioning
credentials**: a Slack Configuration access token and its refresh token. This
minimizes the credentials that the user must handle manually:

- **Provide once per workspace**: Provide the pair when first connecting the
  workspace. `mo slack setup` directs the user to the Slack App management page
  to generate an access and refresh token pair and submit it through protected
  input without echo. The user can revoke and replace it at any time.
- **Self-recovering expiration and revocation**: A Configuration access token
  has a short lifetime. On expiration, the Server uses the refresh token to
  rotate it and atomically store the new pair. The user does not periodically
  resubmit it, and rotation is transparent until the refresh token also fails.
  Provisioning credentials never appear in messages, logs, CLI output, or
  Agent-visible text. When the refresh token fails or is revoked, App
  maintenance for the workspace becomes Degraded and gives one next step:
  rerun `mo slack setup`. Existing Bot message delivery continues, but creation
  and repair are blocked.
- **Provide runtime credentials per App**: With provisioning credentials,
  Mohist automates Mohist App and Agent App creation, manifest configuration,
  and installation links. The user still confirms Slack installation and uses
  protected CLI input to provide the Bot Token shown after installation and the
  App-level token generated for Socket Mode. A fully local deployment has no
  HTTPS OAuth callback that Slack can reach. Mohist does not pretend that it can
  receive those two results automatically.

Design principle: **Mohist automates what the platform permits and asks the user
only for authorization and necessary result input**. The guide must be
self-contained. It does not assume that Slack CLI is installed or request a
Slack login session. `mo slack` directly gives the entry point, required action,
reason, and continuation for each step. Credentials must not pass through a
Mohist App conversation, command argument, log, or Session transcript.

## Talk to the Mohist App

The Mohist App is both the initial installer and the persistent Slack management
entry point. Users perform daily management operations in natural language in
its direct messages:

- **Install an Agent**: "Install review-bot in Slack." The Mohist App creates a
  recoverable Connection record and Agent App and returns the installation
  authorization link. When a Bot Token or App-level token is required, it only
  directs the user to continue with `mo slack install-agent review-bot` on the
  Mohist host. It does not accept a secret in Slack. The complete process uses
  one installation record, and rerunning the command after interruption resumes
  from the current step.
- **View and diagnose**: "Which Agents are connected?" or "What is review-bot's
  status?" The Mohist App gives the current state and single next step for each
  item from the same facts shown in the Web UI and CLI.
- **Adjust and manage lifecycle**: Change access policy, disable or enable an
  Agent Connection, or start Owner transfer. Permanent Slack App deletion is
  not available in conversation because it requires separate confirmation and
  audit. Use the Web UI or CLI.
- **Create an Agent**: "Create an Agent that watches CI failures every day."
  The Mohist App asks at most two questions: the name and daily purpose. It uses
  defaults for everything else, drafts Instructions, and selects the default
  Runtime and Model without asking about each technical setting. After creation,
  it immediately guides Slack installation.

The Mohist App binds a built-in Mohist Agent named `mohist-slack`. All of its
capabilities come from existing Mohist operations over the same resources and
semantics as the CLI and Web UI. Conversation is only a natural-language
interface to those capabilities. It neither creates a second meaning for a
management operation nor executes work for another Agent.

The Mohist App conversation follows the same liveness and reply contract as
every Agent Connection (see [Present Replies](#present-replies)): acceptance
and progress are reactions, and the built-in Agent authors replies through the
same send action. It performs management through the same operations the CLI
and Web UI call, under the operator's own authority, and reports only confirmed
results. The conversation is not a separate command channel: it has no hidden
protocol, no message-shaped commands, and no effects beyond those operations.

The people who can drive the Mohist App are the people who can manage the
corresponding resources. It responds to a management request only from an
operator allowed to manage the target Connection or Agent. An ordinary
workspace member cannot obtain a management operation by talking to it. By
default, Mohist App direct messages accept only the operator claimed during
installation. This follows the same principle that the ability to invoke a Bot
means the ability to use everything behind it.

## First-version Product Boundary

- One Agent Connection binds one Mohist Agent in one Project.
- One Agent Connection maps to one Bot identity in one Slack workspace. That
  Bot comes from the dedicated Agent App created for the Agent by the Mohist App.
- One Mohist Agent can have multiple Agent Connections, such as connections to
  personal and team workspaces. Each has independent permissions, state, and
  historical thread mappings.
- One Agent has at most one non-deleted Agent Connection in one Slack workspace.
  One Bot can join multiple channels; do not copy the Connection per channel.
- The first version uses an independent Bot identity per Agent Connection. It
  does not use a shared `@mohist` Bot that selects an Agent from message text.
  The Mohist App is a management entry point, not a shared execution identity.
- The first version targets private Slack Apps. Evaluate a public marketplace
  and multi-tenant hosting as a separate product phase.
- The independent local `mohist-slack` service sends and receives Slack
  messages. It performs only protocol translation and stores no recoverable
  data. Agents, sessions, message receipt progress, session ownership, and
  pending outbound messages remain in Mohist.
- The first version uses ordinary Slack Bots through direct messages, channel
  mentions, and threads. A native Slack Agent entry point is a later phase that
  changes only presentation in Slack, not Agent capability.
- The first-version direct-message entry point supports only one-to-one member
  and Bot conversations, not group direct messages. Team discussion uses a
  channel and thread that the Bot has joined.

## Conditions and Recommendations Before Connection

Connecting a workspace to the Mohist App requires one authorization by a Slack
member allowed to create and install Apps in that workspace and one-time App
provisioning credentials. See [App Provisioning Credentials](#app-provisioning-credentials).
**Many workspaces prohibit members from installing Apps by default.** In that
case, installation of the Mohist App requires administrator Approval. Agent Apps
created later by the Mohist App and their installation authorizations can each
require separate Approval. Confirm that the workspace App installation policy
and plan support this model before selecting Agents to connect.

Each Agent installation is a **recoverable process** with stable identity. After
an interruption, timeout, user cancellation, or pending administrator Approval,
it continues with the same Agent App instead of creating another Bot. At each
step, the Mohist App shows one current state and one next action. It does not
make the user combine facts into a conclusion.

The `mohist-slack` service can be installed or recovered later. Until it is
ready, the Connection remains in its corresponding waiting state instead of
requiring a restart. Agent Readiness and Connection installation are independent
and can complete in parallel.

Before inviting other users, run a real test from the Web UI or with
`mo agent launch` and confirm the expected Agent behavior. A Connection can
reach Connected while the Agent Needs setup, but new Slack delegations are then
explicitly rejected. A channel shows only a safe summary, while the Owner and
Web UI or CLI operators see specific configuration gaps. When Readiness is
Unknown, the delegation is accepted and waits for Runner validation. A
temporarily offline Runner or lack of capacity does not block Connection
creation; work is explicitly queued.

Slack is not a second source of Agent configuration. An Agent that fails when
used directly cannot become available through a hidden Slack prompt. The
Connection page separately shows Agent Readiness, installation progress, and
connection health instead of hiding a configuration gap behind an ambiguous
"Connected."

## Installation Flow

`mo slack install-agent <agent>` or a Mohist App conversation can start
installation. Both use the same Server-side installation flow and retain
recoverable progress in Mohist and Slack. Any step involving a secret must
return to the local CLI. One installation record decides the current state and
single next step throughout the process.

### Install the Mohist App First

1. Run `mo slack setup` on the Mohist host. If App provisioning credentials are
   absent, the CLI directs the user to open the Slack App management page in
   their browser, generate a Configuration access and refresh token pair, and
   submit it through hidden input or a protected file. The CLI neither logs in
   to Slack nor requests the browser session.
2. Mohist validates the credential workspace, generates the fixed Mohist App
   configuration, creates the App, and shows its Slack installation link. The
   user confirms **Allow** in their browser. When administrator Approval is
   required, setup retains progress and waits.
3. After authorization, the CLI directs the user to provide the Bot User OAuth
   Token from **OAuth & Permissions** and generate an App-level token with only
   `connections:write` under **Basic Information > App-Level Tokens**.
4. `mo slack setup` validates the workspace, App, Bot, and Socket, stores the
   credentials securely, and starts or recovers the local `mohist-slack`
   service. It shows Ready only after the Socket connection is established and
   the Mohist App identity is confirmed. After interruption, rerun
   `mo slack setup` to continue the same App.

### Select an Agent and Prepare Installation

1. In the CLI or Mohist App, the installer sees active Agents for which they
   **can manage the Connection**. Each shows its current Slack state: Not
   installed, Action required, Pending approval, Installing, Ready, Degraded,
   Disabled, or Removed. Slack identity cannot reveal or install an Agent that
   the installer is not allowed to manage.
2. The installer selects an Agent and confirms the Bot name, avatar, and
   description that will appear in Slack, the required Slack permissions and
   reasons, the default invocation policy, and the initial Connection Owner.
   The name is a suggestion, not an identity key. On a collision, the Mohist
   App suggests a name with a stable suffix before creation.
3. Mohist creates a **recoverable Connection and installation record** fixed to
   the target Agent and workspace, but with no Slack App or Bot yet. All later
   progress belongs to this record, which resumes after interruption.

### Create the Agent App and Complete Installation Authorization

4. With the supplied App provisioning credentials, Mohist generates a
   versioned App configuration for the Agent and creates an independent Slack
   App. A timeout or network interruption can make the creation result unknown.
   The installation record then enters **Result unknown** and **does not create
   another App automatically**. Reconcile with Slack or use manual judgment
   first to confirm that one Agent still maps to only one App.
5. The CLI or Mohist App guides the installer through Slack installation
   authorization for the Agent App. When workspace Approval is enabled, it
   waits for administrator Approval, which cannot be bypassed. If the user
   cancels, authorization expires, or Approval is pending, Mohist retains the
   same App and resumes later without creating another Bot.
6. After installation authorization, Mohist verifies that the returned
   workspace, App, and Bot identities match the expected identities. On any
   mismatch, it stores no credentials, does not bind the Connection, and gives
   an explicit next step.

### Obtain Local Runtime Credentials

7. In the Agent App, the installer copies the Bot User OAuth Token from
   **OAuth & Permissions** and generates an App-level token with only
   `connections:write` under **Basic Information > App-Level Tokens**.
   `mo slack install-agent <agent>` accepts both through hidden input or a
   protected credential file and immediately validates the workspace, App, Bot,
   and Socket Mode capability. A mismatch is neither stored nor bound. The
   Connection is not Ready until both credentials are present. Credentials do
   not appear in Agent Instructions, messages, logs, or Session transcripts.

### Bind the Owner and Verify

8. After identity and credential verification, enter **Claim owner**. The
   installer selects **Generate owner code**, and Mohist displays a short-lived,
   single-use claim code. The code is not shown again after leaving the page.
   Regeneration after loss immediately invalidates the old code. The installer
   sends the code in a direct message to the Bot. Only a current full member of
   the workspace can become Owner. External collaborators, Bots, and deactivated
   members cannot claim. A successful direct-message claim also proves that the
   current App can receive and reply to direct messages.
9. Select the channel access policy. The default is **Owner only**. Allowlist
   searches workspace members by name and avatar, so users do not need to find
   a Slack member ID. The Owner can also use Slack's native member selector in
   a Bot management operation. Allowlist and Anyone always include the Owner.
   To prevent accidental authorization through a direct message, every policy
   accepts direct messages only from the Owner. After claim and healthy
   connection, state becomes **Ready**.
10. Invite the Bot to a target channel, send a test task in a direct message,
    or mention `@Bot` in a channel root message. Verify the result in Jobs and
    Sessions on the Agent details page. When the Agent is not Ready, Slack shows
    an explicit safe unavailability summary. Only the Owner and Web UI or CLI
    operators see the specific Runtime or credential gap. The Connection itself
    remains Ready.

Confirmed facts determine installation progress, which highlights one current
state and one next step: Not installed, Action required, Pending approval,
Installing, Ready, Degraded, or Disabled. Action required can mean pasting an
App-level token, retrying creation, or continuing authorization. Without
platform evidence, Mohist cannot know whether a user completed an external
Slack step and does not fabricate Ready without credential or authorization
evidence. Completed steps remain inspectable. The page and `slack view` cannot
show only Setup required.

The page can show Agent Readiness, installation progress, connection health,
and identity synchronization separately. Its summary highlights only one
current state and one next step, so the user does not combine facts manually.

### CLI

`mo slack` covers all Slack Agent Connection operations. Installation commands
are idempotent, resumable guides. The CLI advances every automatable step first
and stops only for authorization or token generation that Slack requires a user
to perform. It gives the exact page, action, and continuation command. Rerunning
the same command reads durable progress and does not create a second App.

- `mo slack setup` installs the workspace-level Mohist App in Slack and makes
  the local `mohist-slack` establish its Socket Mode connection. It obtains the
  Configuration token pair, creates and configures the App, guides installation,
  and collects runtime credentials. For an already configured workspace, it
  inspects, repairs, or rotates securely instead of creating another Mohist App.
- `mo slack install-agent <agent> [--workspace-team <team-id>]` installs an
  existing Mohist Agent in Slack. It creates or recovers the Connection record
  and dedicated Agent App, then advances installation, credential validation,
  connection startup, and Owner claim until Ready or a single next step. For one
  connected workspace, `--workspace-team` is unnecessary. If an installation
  record already exists for that Agent and workspace, the command returns to it
  and continues.
- `mo slack status` shows overall workspace, Mohist App, Agent App, and local
  connection state with one next step.
- Manage Agent Connection resources with:

```text literal
mo slack list <agent>
mo slack view <connection-id>
mo slack edit <connection-id> --access-policy allowlist --allow-member <slack-member-id>
mo slack disable <connection-id>
mo slack enable <connection-id>
mo slack transfer-owner <connection-id>
```

The CLI and Web UI configure the same Agent Connection and do not create two
local configurations. `install-agent` can also query and continue a record
created by installation through a Mohist App conversation. Slack App creation,
manifest update, credential submission, and delivery recovery are internal
installation steps, not ordinary commands that users must orchestrate.

`--allow-member` can be repeated. With Allowlist, it replaces the complete
member list except the Owner. The Owner need not be repeated and cannot be
removed. Owner only and Anyone cannot be combined with `--allow-member`; the
error returns before modification. Member ID is the CLI automation interface.
The Web UI and Slack use member search and avatars instead of presenting IDs as
the primary interaction.

## Agent Connection Configuration

| Setting | Meaning |
|---|---|
| Agent | The fixed Mohist Agent; it cannot be rebound after creation, so create another Connection for another Agent |
| Slack workspace | The Bot workspace, confirmed by Mohist App installation rather than a user-entered name |
| Bot identity | External identity initialized from the Agent name and avatar, then managed by Slack and verified by Mohist |
| Slack description | Short App description generated from Agent Description; Mohist generates nonempty generic text when Description is empty |
| Runtime mode | Local Socket Mode; each Mohist App and Agent App has an independent Bot Token and App-level token |
| Owner | Slack member verified during first claim or later transfer; the only caller by default and a permanent Allowlist member |
| Access policy | Who can start work or continue a session in a channel; Owner only by default |
| Allowed members | Slack members added through a member selector who can invoke the Bot in channels under Allowlist; direct messages remain Owner-only |
| Installation progress | Not installed / Action required / Pending approval / Installing / Ready / Degraded / Disabled |
| Status | Setup required / Ready / Degraded / Disabled; Degraded must include one actionable reason |
| Identity sync | Whether the Bot name and avatar still match the current Agent |

Agent Instructions, Runtime, Model, Variant, Skills, and concurrency limit are
not Agent Connection configuration. Edit the Mohist Agent to change its
execution definition, which applies by snapshot to the next new work. Edit the
Agent concurrency limit as well, but it acts as a live scheduling policy for
later launches and follow-ups.

Bot identity is not a second Agent configuration. After an Agent name or avatar
changes, Mohist shows the Agent Connection identity as out of sync and provides
the Slack App settings entry point and correct name and avatar. Verify again
after updating. The first version does not request extra Slack administrator
permission to edit the App profile automatically. Identity drift is not
presented as a disconnected Connection.

Agent Description initializes the short Slack App description and never becomes
Instructions or a hidden prompt. After Description changes, Mohist shows the
new expected text and manual update entry point. Because a Bot token cannot read
the actual App configuration, Mohist states that this field cannot be verified
automatically and does not claim synchronization.

## Slack Message Permissions

To let a user follow up naturally in a bound thread without mentioning `@Bot`
on every message, the Slack App must receive message events from channels it has
joined. Mohist processes only Bot direct messages, explicit mentions, and
replies in threads bound to an AgentSession. It discards other ordinary channel
messages before delivery to a Mohist Agent. Their bodies do not enter durable
Mohist records or logs.

Before installation, the configuration page must list every requested Slack
permission and explain its function. Users should invite the Bot only to
channels where it is needed. The first version does not provide individual
permission toggles, which would create partially configured states where the
interface offers a follow-up or file read that the App cannot perform.

The Agent Connection reads the basic workspace member directory to select
Owners and Allowlist members, show message senders, and exclude Bots,
deactivated members, and external workspace identities. Mohist does not read
member email or give the member directory to an Agent. It stores only member
IDs, names, and avatars needed for presentation and removes them after
Connection deletion.

Mohist can verify Bot identity and granted permissions and confirm the message
path through real send and receive operations. Without requesting Slack
administrator permission, it cannot read the complete App configuration. It
therefore reports a specific gap only when a capability actually fails and does
not claim that all capabilities work only because a token is valid.

Socket Mode does not require a public Mohist inbound address, but Slack retains
unconfirmed messages for only a limited retry window. When workspace policy
allows, the configuration page should recommend **Delayed Events**. Even then,
Mohist does not promise indefinite recovery. After a long Agent Connection
service outage, the status page must warn about potentially missed messages and
ask the user to resend critical delegations.

## Use Slack

### Start New Work

Two situations start new work:

- Send the first task message in a direct message with the Bot when that
  conversation has no current Session.
- Send a new root message in a channel and mention the Bot.

A direct message is a continuing conversation. Once it has a current Session,
later messages continue that Session instead of starting new work. Use separate
channel threads when several tasks must proceed in parallel.

After removing the Bot mention, the message must contain task text or at least
one usable attachment. A bare mention does not create an AgentJob; the Bot asks
for the missing task. An attachment by itself is valid explicit input, and
Mohist does not invent a hidden prompt for it.

After acceptance, Mohist creates an AgentJob, AgentSession, first SessionInput,
and first AgentTurn. Acceptance is the **👀 (Received)** reaction on the user's
message, not an acknowledgement message. The liveness projection then shows
whether the work is running or queued. A queued or running Turn can be stopped.
Agent replies, failures, and conclusions that need human action
return to the same Slack conversation. When the needed action is one Mohist
can execute — such as stopping or retrying a Turn — the notice carries a
signed action button. Pressing it performs the operation under the presser's
authority with the same result as the CLI or Web operation. A button is never
a second command grammar; it is a shortcut to the same operation.
Completing the AgentJob does not close
the Session; when a reply asks a question, the user can answer directly.

### Continue the Same Session

In a **channel**, another message in the thread containing the Bot reply sends
a follow-up to the bound AgentSession.

Direct messages do not normally use threads, so each DM conversation is one
continuing conversation. Every ordinary message continues the same Session,
even after the previous Turn ends. AgentJob completion does not clear that
association; the next message enters the next Turn. The first version has no
"new task" control in a DM. The Agent handles topic changes in the same context
instead of splitting them into parallel Sessions. Use separate channel threads
when independent work must proceed in parallel.

Follow-up behavior is the same in both cases:

- It does not create another AgentJob.
- It preserves the Session's existing context.
- Every message creates a SessionInput with a stable identity.
- When the current Turn has not started, consecutive messages wait in Slack
  receipt order. During execution, a backend that supports additional input
  adds the message to the current AgentTurn; otherwise it waits for a later
  Turn.
- An Input received while idle starts the next AgentTurn in the same Session.
- A message received during execution steers rather than interrupts by default.
  It joins the current work or waits for the next Turn. Only an explicit stop
  operation interrupts the current execution.

Every Session has a bounded waiting queue. At the boundary, the Bot rejects new
messages and asks the sender to retry later. Mohist never discards an accepted
message to make room for a new one.

One Slack thread can contain several Mohist Agents, each with an independent
AgentSession:

- When only one Mohist Agent is bound, an unmentioned human reply naturally
  continues that AgentSession.
- When several Mohist Agents are bound, an unmentioned reply remains human
  discussion and invokes no Agent. The user must mention the target Bot.
- Mentioning another Mohist Bot for the first time in an existing thread
  creates an independent AgentSession for it. The original Agent's context is
  neither switched nor contaminated.
- A message that mentions several Bots managed by the same Mohist Server starts
  no work and produces one interactive selection instead of a free-text
  reply; choosing an Agent starts its work. A Bot's own message never
  becomes input — not for itself, and not for another Bot. Separate Mohist
  Servers have no shared routing state, so the first version does not
  coordinate one multi-Bot message across installations.

As in Buzz, several Agents can therefore act as independent collaborators in
one discussion without automatically triggering one another or making the
owner of a follow-up ambiguous.

### Mention in an Existing Discussion

When the first Bot mention appears inside an existing human discussion, the
integration passes the thread messages visible to the Bot as initial context
and treats the mention as the explicit task. If the context exceeds the limit,
Mohist truncates complete messages starting with the oldest and marks the
truncation in both the Agent input and Slack confirmation. It never drops
context silently. If permissions, rate limiting, or a Slack failure prevents a
complete read of that bounded context, Mohist rejects the delegation, asks the
user to mention the Bot again later, and creates no AgentJob.

Imported thread history is ordinary user input, not Instructions. Anyone may
have written it, so its maximum effect is the capability already granted to
the Agent. Consider that boundary before widening a channel policy to Anyone.
See [Permissions](#permissions).

Editing an accepted Slack message does not rerun it. Send a follow-up to state
the correction. Deleting a Slack message also does not remove the AgentJob,
AgentSession, or audit record already created in Mohist.

### Files and Links

The Bot can read files that it can access in Slack and that the current message
or thread explicitly provides. Mohist preserves their source and passes them
as input attachments. When a file cannot be read, exceeds a limit, or has an
unsupported type, the Bot identifies the unused attachment instead of
pretending to have read it.

Links remain part of the user message. Whether the Agent opens one depends on
its configured Skills and Runtime permissions. The Slack integration does not
fetch arbitrary URLs or expand network access merely because a link appears in
thread history.

## Present Replies

Slack has two signals with different owners. Mohist presents **liveness**, the
fact that the system is processing. The Agent actively sends the **reply**, the
content it chooses to say.

- **Mohist owns liveness**: reactions (👀 Received, ⏳ Working, ✅ Completed)
  and one status message that can be updated in place. These signals begin as
  soon as input is accepted and do not depend on the Agent sending a reply.
  Reactions are best-effort: a failed reaction call never blocks or fails
  work. Liveness always reaches a terminal form (✅ or ⚠️) on every outcome,
  including Agent crash, cancellation, and service restart.
- **The Agent owns the reply**: the Agent uses Mohist's send action to write to
  the injected reply anchor. Agent reasoning, tool calls, and intermediate
  output are not visible to Slack users. Only content that the Agent actively
  sends appears in Slack; Mohist does not extract or invent a reply from the
  execution result, and no Mohist component interprets Agent output to produce
  Slack content or management effects.

The Web session timeline contains the complete execution record, including
Inputs, tool calls, and intermediate output. When an External Web URL is
configured, **Open in Mohist** opens that AgentSession timeline. Channel
members can get the conclusion without leaving Slack, while anyone who needs
the complete process has one canonical destination.

### Liveness and Reply for One Input

For each accepted user Input, Mohist adds the **👀 (Received)** reaction to the
original message. While the Input executes, it adds **⏳ (Working)** and
maintains one status message that can be updated in place. The Agent chooses
and actively sends the reply. Mohist puts that reply at the status-message
location, preferably by updating the status into the final answer. One Input
therefore has at most one status message and one final answer rather than a
stream of status posts. Once created, a status message keeps its identity; a
later update cannot silently become a new message.

Fast work can show only the Received reaction on the user's message and then
send the final answer, without creating a status message. Asynchronous or long
work progresses through **Received -> Working -> Completed**. Work that cannot
finish or needs user action ends as **Needs attention** or **Failed**. If a
status update fails, the integration appends the final answer once in the same
thread and records a diagnosable delivery problem. Retries and duplicate
delivery cannot create a second final answer.

The default reactions are **👀 -> ⏳ -> ✅**, with **⚠️** for an exception.
They say only that a message was received, is processing, or ended. They do not
decide whether Mohist work succeeded. Success, cancellation, partial
completion, and failure come from confirmed AgentSession and AgentTurn facts.
On a platform that cannot react to the user's message, reactions appear on the
single status message.

- **The Agent is the speaker**: Slack body text is content the Agent actively
  sent, rendered as text and redacted. Mohist does not extract or invent it from
  an execution result. Status fields such as an exit code, artifact count, or
  Job and Session IDs are not the answer. They appear only as secondary
  metadata or stable links.
- **Silence is valid**: A Turn that ends without the Agent sending content means
  that the Agent found nothing worth saying. It is not a failure. Mohist closes
  liveness normally by marking the status complete and adding ✅, but does not
  invent a summary for a silent Agent.
- **A failing Agent explains its failure**: When execution fails or needs human
  action, the Agent sends the reason and an actionable next step instead of a
  generic template. Only when the Agent crashes or stops responding does Mohist
  send a system fallback that clearly identifies a system failure rather than
  an Agent conclusion.
- The Agent writes standard Markdown, which Mohist renders into Slack bold,
  code, quotes, and lists. Unsupported tables and headings degrade to readable
  code blocks or bold text. The Agent can attach a local screenshot or a public
  image. Mohist renders message content as text, so content that resembles a
  mention, button, or message configuration cannot trigger `@channel`,
  `@here`, or forge a Stop operation. Mohist alone creates real controls from
  current Job, Session, and Turn state.
- The Agent sends replies with `mo slack message send`. Mohist injects the
  destination reply anchor; the Agent does not guess it. The action uses
  Mohist's reliable delivery rather than connecting directly to Slack. This
  layer owns redaction, duplicate protection, reply-anchor validation, retries,
  and Delivery uncertain reconciliation.
- A Slack reply must contain the conclusion, evidence summary, and next step
  needed for the current decision. It cannot require the user to open Mohist
  Web just to learn the result. Long results can use several messages in the
  same Slack conversation.
- **Open in Mohist** appears only when an administrator configures a Mohist Web
  address that Slack users can access. Mohist never sends a localhost address
  to Slack. Without a usable Web address, the message shows stable Job and
  Session IDs for lookup through Web or CLI.
- Mohist decides the execution result of an AgentJob and every AgentTurn. A
  failed Slack reply does not turn completed execution into failed execution.
- The first version can show result text, existing accessible external links,
  artifact names, and stable IDs. It does not copy Mohist artifacts into new
  Slack files. Retrieve the file from its original result location or through
  Web or CLI. The Slack reply must still contain its conclusion and next step.
- When Slack definitely rejects a reply, the integration retries according to
  platform requirements and enters Degraded. If the send result is unknown, it
  reconciles first. If uncertainty remains, it does not blindly send another
  message. The Connection page, CLI, and available Owner diagnostics show
  **Delivery uncertain**. A manual resend warns that a duplicate may result.
- Pending delivery has a capacity boundary. Replaceable queued or executing
  progress can collapse into the latest state. Final results, definite
  failures, and user actions are never silently deleted. If they no longer fit,
  the Connection enters Degraded with reason **Backpressured** and stops
  accepting Slack input. Mohist preserves and arbitrates already accepted
  execution.
- After a service restart or reconnect, delivery resumes from Mohist's last
  confirmed position. It does not duplicate Jobs, Inputs, or confirmed replies.

> **Current implementation gap:** Channel root mentions, bound-thread
> follow-ups, multi-Bot ownership prompts, duplicate delivery protection, and
> the Owner only, Allowlist, and Anyone Connection policies are available.
> Anyone also verifies that the Bot can see the current channel, while DMs
> remain Owner-only under every policy. Importing existing thread history is
> available. Truncation removes complete Bot-visible messages older than the
> current mention by Slack timestamp and is identified in both the Slack
> confirmation and Agent input.
>
> Attachments can accompany a Slack message as file metadata, including name,
> type, size, and identity. Mohist attempts to fetch content only when it can
> read the file. It rejects unsupported, oversized, or unreadable files. This
> does not guarantee that every attachment can be retrieved or processed.
>
> Connection access policies Owner only, Allowlist, and Anyone are delivered.
> Anyone still confirms that the Bot can see the current channel. Per-Connection
> selection of accepted channels is not delivered. Coordination between Bots
> managed by different Mohist Servers is also not available.
>
> The Mohist App conversation is migrating to the standard liveness and reply
> contract. The current build still acknowledges a management request with a
> text message and performs management through a model-output protocol instead
> of the ordinary operations surface; reactions, the send action, and the
> standard reply contract specified above are not yet delivered for it.

## Slack Collaboration Rules for Agents

Mohist injects a Slack collaboration guide into the Agent as a visible,
evolving Skill. It governs how the Agent behaves in a shared Slack space; it
does not change the Agent's capabilities:

- **You are the speaker**: Replying is an explicit action. Use the send action
  included with the Input and write to its reply anchor. Reasoning and tool
  calls are invisible to Slack users; only content you actively send becomes a
  Slack message. Send a useful conclusion from a Turn, but send nothing when
  there is no new information. Mohist will not derive or invent your reply.
- **Do not send empty acknowledgements**: Messages that say only received,
  understood, or confirmed interrupt a channel and can trigger other Bots.
  Silence without new information is a normal completion, not a failure.
  The exception is a human's direct question: always answer it, even if the
  answer is only that you have nothing to add — never leave a person waiting.
- **Call back after a delegation**: When work delegated by another person or
  Agent is complete, mention the delegator in the result. Missing this callback
  is a common cause of stalled collaboration. Do not mention someone merely to
  acknowledge receipt; mention them only when they need to act or notice the
  result. A narrative reference to a person needs no mention.
- **Keep replies self-contained and progress proportionate**: Put the
  conclusion, evidence summary, and next step in Slack. Milestones such as
  accepted, blocked, or completed can appear in the conversation. Fine-grained
  execution belongs in the Web session timeline.
- **Do not guess the reply location**: Mohist identifies the thread and message
  anchor for every Input. Do not choose a historical message from memory or
  send a reply or delegation into another channel.
- **Resume silently after a restart**: After a service restart, Session
  recovery, or context compaction, rebuild state from durable records and the
  thread and continue. Do not announce the interruption, summarize what was
  lost, or ask how to proceed.

## Permissions

| Policy | Direct messages | Channel mentions and bound threads |
|---|---|---|
| Owner only | Owner only | Owner only; the default |
| Allowlist | Owner only | Owner and explicitly listed workspace members |
| Anyone | Owner only | A verified member of the App installation workspace who can see the Bot in the current channel |

**Anyone who can invoke the Bot can use every capability already granted to
the Agent**, including repository write access, tools, and credentials. Widening
the access policy is a permission grant, not a convenience switch. Do not use
Anyone for an Agent with repository write access.

This follows Buzz's DM hardening. Allowlist or Anyone does not grant access to a
member who happens to enter the Bot's direct messages. On every invocation,
Allowlist and Anyone verify that the sender is still a full member of the
current workspace. Deactivated, restricted, external, Bot, and otherwise
unconfirmed identities are rejected. Anyone also verifies that the Bot is in
the sender's channel. A failed check is a rejection. The Owner check for DMs
does not depend on these lookups, so the Owner can invoke the Bot under every
policy.

Channel membership determines only whether the Bot can receive a message; it
does not replace the Access policy. Slack Connect participants and identities
whose ownership cannot be confirmed do not invoke an Agent in the first
version. Anyone does not mean everyone who can see a message. An invited Bot in
a private channel still checks the sender against the Connection policy. An
unauthorized user receives a short, explicit rejection, and Mohist creates no
AgentJob or AgentSession.

Only the Connection Owner or the Slack member who started an AgentSession can
stop one of its AgentTurns. Other permitted members can continue the
conversation but cannot stop someone else's execution. A stale button cannot
stop a later Turn.

Access policy answers only who may invoke the Agent. It does not reduce the
Agent's configured Runtime, Skills, repository, or tool capabilities. A Slack
message also cannot add or replace those capabilities temporarily. Mohist owns
one Agent permission configuration.

For audit, every Input records the Slack workspace, channel, thread, and sender
identity. Each execution can answer which Slack member started it. A Slack
identity is not a Mohist administrator identity, and ordinary message content
cannot switch the Project, Agent, or Access policy. Only the Owner can change a
Connection's invocation scope through an explicit **Manage access** operation.

An Access policy change applies immediately to every later Input, including a
follow-up in an existing Session. It does not revoke accepted execution or
delete history. The Owner is always included in the Allowlist. Only a Mohist
operator authorized to manage the Connection can initiate Owner transfer. The
system generates a new one-time claim, and the new Owner claims it in a Bot DM.
The replacement is atomic; the old Owner remains until success. The new Owner
must also be a current full member of the workspace.

## Lifecycle and Failures

A Connection has three independent lifecycle actions. Each requires explicit
confirmation and cannot be hidden behind one ambiguous delete operation:

- **Disable** pauses only the Connection. It immediately stops accepting Slack
  Inputs and sending replies, while accepted execution continues in Mohist. It
  does not delete the Slack App or its management facts. Enable returns it to
  its previous state.
- **Remove binding** detaches the Connection from Mohist and clears runtime
  records such as message receipts, Session mappings, and pending delivery. It
  preserves the Agent App management facts so the App can be bound or diagnosed
  again. It does not uninstall the App from Slack.
- **Permanent delete** deletes the Slack App created by Mohist for the Agent. It
  requires a second confirmation, separate permission, complete audit, and no
  active Connection binding. Remove the binding first when needed. A delete
  result can be unknown; Mohist must report that uncertainty and require
  reconciliation or human arbitration instead of claiming success. Mohist can
  delete only Apps that it created.

| Situation | Product behavior |
|---|---|
| Agent name or avatar edited | Mohist updates the Agent immediately and marks Bot identity out of sync; the user updates Slack App settings and verifies again |
| Agent description edited | Updates Mohist discovery information and the expected Slack description; does not change Agent behavior or request Slack management permission |
| Agent execution definition edited | New AgentJobs use the new snapshot; existing Sessions retain their configuration |
| Agent concurrency limit edited | The next launch or follow-up uses the latest limit; running input is not stopped |
| Agent archived | Rejects new root delegations; existing Sessions remain readable and continuable |
| Agent becomes Needs setup | Connection health stays independent; a new root delegation shows only a safe summary in the channel, while the Owner and Web or CLI operators can inspect configuration gaps; existing Sessions continue with their snapshots |
| Agent Readiness is Unknown | Accepts a new root delegation and shows "waiting for Runner validation"; an AgentJob or Turn returns an explicit failure if execution is later known to be impossible |
| Connection Disabled | Stops Slack input and replies immediately; accepted execution continues. Messages received while disabled are acknowledged and discarded instead of becoming later tasks. Enable fills only missing current or final results and does not replay stale progress |
| Binding removed | Clears runtime records and the Connection binding, preserves Agent App management facts, and does not uninstall the Slack App |
| Slack App permanently deleted | Requires second confirmation, no active binding, and audit; an unknown delete result requires reconciliation or human arbitration |
| Agent App creation times out or is unknown | Enters result unknown and cannot create another App automatically; reconcile with Slack or arbitrate manually so one Agent still maps to one App |
| Installation authorization cancelled, expired, or pending approval | Preserves the same Agent App and continues after recovery instead of creating another Bot |
| Authorized identity differs from expectation | Saves no credential and binds no Connection; returns one explicit next action |
| Slack credential invalid | Enters Degraded and stops new input; rerun `mo slack install-agent` and verify the same workspace, App, and Bot, otherwise handle it as a new installation |
| Agent already connected to the Slack workspace | Does not overwrite the Connection; points to the existing one so the user can remove a duplicate and uninstall its extra Slack App |
| Socket Mode temporarily unavailable | Preserves the Connection and installation progress; Slack retries within its window, and the user may need to resend messages beyond it |
| Owner must change | A Mohist operator starts a new one-time claim; the old Owner remains until success, and a Slack sender cannot reset ownership |
| Owner leaves, is deactivated, or becomes a guest | Enters Degraded with Owner unavailable and does not transfer to a same-named member; channel Allowlist and Anyone access can continue, but DMs and Owner management are unavailable until a Mohist operator transfers ownership |
| Slack redelivers an event | Returns the existing Job, Session, Input, and Turn instead of creating another Input |
| Mohist cannot confirm delivery of an Input to execution | Marks it Unknown and reconciles the same Input instead of replaying it as a new request |
| Agent concurrency or Session queue limit reached | Shows queued or asks the sender to retry later; capacity is not execution failure, and accepted input is not discarded |
| Pending-result capacity reached | Collapses replaceable progress, enters Degraded (Backpressured), and rejects new Slack input without dropping final results, definite failures, or user actions |
| Owner or Session starter stops a queued Turn | Ends it locally and records it cancelled; the AgentJob for a first Turn ends with failure category `cancelled` |
| Slack cannot send a reply | Work result remains unchanged; a definite rejection retries automatically, while an unknown result shows Delivery uncertain without blind duplication |

## Non-goals

- A Slack Bot does not run an Agent Runtime or read a Runner or database.
- A Slack Bot does not own Agent configuration or change Agent behavior through
  a hidden prompt.
- The Mohist App neither speaks for an Agent nor acts as a shared execution
  identity for several Agents.
- The Mohist App conversation does not become a Workflow board, Issue manager,
  or complete diagnostics console. Management actions stop at the Agent
  Connection lifecycle.
- Slack does not provide an Agent editor, Workflow board, or complete
  diagnostics console.
- One shared Bot does not infer which Agent to invoke from natural language.
- Ordinary channel messages are not all sent to Mohist. Only DMs, explicit
  mentions, and replies in a bound thread trigger it.
- The first version does not include Slack's native Agent entry point, Agent
  Home, or streaming replies.
- The first version does not include Slack-native slash commands or message
  shortcuts. Structured control in Slack uses signed action buttons;
  everything else is a message.
- The first version does not include a public app marketplace, multi-tenant
  hosting, billing, Slack Connect external-member invocation, or cross-company
  directory discovery.
- The first version does not coordinate Bots managed by separate Mohist Servers
  in one Slack workspace.
- The first version does not support Slack group DMs.
- The first version does not upload Mohist artifacts into new Slack files.
- Local installation does not promise one-command, zero-step automation. The
  installer must complete Slack installation authorization, any required
  workspace administrator approval, and provide the Bot Token shown by Slack
  plus a manually generated App-level token for each App. Mohist automates App
  creation, configuration, validation, and runtime connection around those
  required steps.

## Status

The data plane for messages and Agent invocation is delivered. Channel root
mentions, bound-thread follow-ups, multi-Bot ownership prompts, duplicate
delivery protection, Owner only, Allowlist, and Anyone policies, and thread
history import are available. Anyone verifies that the Bot can see the current
channel. Status messages update in place and show work in progress. Unknown
delivery is reconciled, and a failed update produces only one fallback. Every
Slack Input includes collaboration rules and a reply location. An ordinary
follow-up does not interrupt current work; only an explicit Stop operation for
the attached Session subtree requests interruption. A terminal reply keeps
stable Session identity and includes a secure Session link when an administrator
configures a publicly accessible Mohist address.

The control plane provides resumable local `mo slack setup` and
`mo slack install-agent` guides plus one-time Owner claim. Both resume
idempotently from one durable progress record, automate App creation and
configuration, and stop only for Slack installation confirmation or local
credential input, with one next action. A rerun validates stored credentials;
explicitly supplying credentials for a ready record rotates them. After claim,
the Mohist App conversation uses the built-in `mohist-slack` management Agent
to view status, create and connect an Agent with defaults, change access,
enable or disable a Connection, and start Owner transfer. Secret-bearing steps
return to the local CLI. The conversation and CLI read the same state and next
action. Remove binding and permanent delete remain independent, explicit CLI
or Web lifecycle actions. Low-level commands such as `configure-manager`,
`create`, `configure`, and `create-child-app` are removed from the surface.

End-to-end acceptance has passed in an isolated real Slack workspace. The
verified path includes `setup`, `install-agent`, Owner claim, DM task execution,
and reply delivery through real Socket Mode. App-management calls reactively
rotate an expired Configuration credential, and an Agent owns its reply body
through the reply action while terminal handling owns liveness only.

A public app marketplace, multi-tenant hosting, cross-Mohist-Server
coordination, Slack's native Agent entry point, and a complete diagnostics
console remain future phases rather than incomplete parts of the current
boundary.
