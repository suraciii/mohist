# Slack

Slack is an interaction interface for Mohist, alongside the Web UI and CLI.
This document defines what users can do in Slack. The component boundary and
wire contract are in [Slack Design](../design/slack.md).

Mohist appears in a Slack Workspace as two App types:

- The **Mohist App** manages the Workspace, Connections, and Agents.
- An **Agent App** exposes one Mohist Agent through one Bot identity.

Management and execution never share an identity. The Mohist App never replies
on behalf of an Agent.

An **Agent Connection** exposes one configured Mohist Agent in Slack. It can
start work, continue a Session, stop execution, and return results. It is not a
notification and it does not copy the Agent's Instructions, Runtime, Model,
Variant, Skills, or concurrency limit.

## Product Commitments

- One Agent has at most one non-deleted Connection in one Slack Workspace.
- One Agent can have independent Connections in multiple Workspaces.
- The Mohist App is a management entry point. An Agent App is an execution
  entry point.
- Slack never decides Agent configuration, execution state, or work results.
- Installation is idempotent and resumable. It never creates a duplicate App,
  Bot, Connection, or installation record.
- Agent Readiness, installation progress, connection health, and identity sync
  remain separate facts.
- Mohist uses local Socket Mode. It needs no public callback service or Slack
  login session.
- A Slack user who can invoke an Agent can use every capability granted to that
  Agent.
- The Agent authors reply content. Mohist owns liveness and delivery state.
- Accepted input is never discarded to make room. Duplicate delivery never
  creates duplicate work or a second final answer.
- Removing a Connection does not remove the Agent or its other interfaces.

## Installation Model

Install the Mohist App once per Workspace. It binds that Workspace to one
Mohist deployment. Each connected Agent receives its own Slack App and Bot
identity.

The Mohist App discovers, installs, diagnoses, disables, and uninstalls Agent
identities. It does not execute work for an Agent or change the Agent's
configured capabilities. Connecting a Workspace or becoming an Owner grants
no additional Mohist management permission.

### Local Socket Mode

A local `mohist-slack` service opens an outbound Socket Mode connection for the
Mohist App and every Agent App. Users configure no public domain, callback
service, or Slack login session. Each App keeps its own credentials.

A member allowed to install Apps must confirm each Slack installation. Workspace
policy may also require administrator approval. Each App needs an App-level
token with `connections:write`. Slack does not return this token through its
public App management API; the user generates it on the App settings page.

### App Provisioning Credentials

Mohist creates and maintains Apps through one Workspace-level **Configuration
token pair** containing an access token and refresh token:

- `mo slack setup` directs the user to Slack's App management page to generate
  the pair and submits it through protected input without echo.
- Server rotates an expired access token with the refresh token and stores the
  new pair atomically. If the refresh token fails, App maintenance becomes
  Degraded with one next step: rerun `mo slack setup`. Installed Bots keep
  delivering messages.
- After installation, the user provides each App's Bot token and App-level
  token through protected CLI input. These credentials never pass through a
  Mohist App conversation, command argument, log, or Session transcript.

There is no Slack CLI requirement and no HTTPS OAuth callback.

## Talk to the Mohist App

Users manage the integration in the Mohist App's DMs:

- **Install an Agent:** say, "Install review-bot in Slack." The App creates or
  resumes the installation and returns the authorization link. Secret steps
  continue with `mo slack install-agent review-bot` on the Mohist host.
- **View and diagnose:** ask which Agents are connected or ask for an Agent's
  status. The answer uses the same facts as the Web UI and CLI: one current
  state and one next action.
- **Adjust lifecycle:** change access policy, enable or disable a Connection,
  or start Owner transfer. Permanent App deletion requires the Web UI or CLI.
- **Create an Agent:** give a name and daily purpose. The App asks for no more
  than those two values, uses defaults for the rest, and then guides Slack
  installation.

The Mohist App uses the same operations as the Web UI and CLI. It does not use a
hidden message protocol or parse model text into operations. Only operators
allowed to manage the target resource can use it. By default, its DMs accept
only the operator claimed during installation.

## Before You Connect

- Confirm that a Workspace member may install Apps. Workspace policy can
  require administrator approval for the Mohist App and each Agent App.
- Prepare the Configuration token pair and the per-App runtime credentials
  through the protected setup flow.
- Test the Agent from the Web UI or `mo agent launch` before inviting users.
- Treat Agent Readiness and Connection installation as independent. A
  Connection can be Ready while its Agent needs setup; new delegations then
  receive a safe rejection.

## Installation Flow

The two guided commands are idempotent and resumable:

```text diagram
        +-----------+
        | Run setup |
        +-----+-----+
              |
              v
  +-----------------------+
  | Confirm Slack install |
  +-----------+-----------+
              |
              v
     +----------------+
     | Provide tokens |
     +--------+-------+
              |
              v
 +-------------------------+
 | Verify App, Bot, Socket |
 +------------+------------+
              |
              v
       +-------------+
       | Claim Owner |
       +------+------+
              |
              v
          +-------+
          | Ready |
          +-------+
```

Each command advances automatable steps and stops only for a required user
action. A rerun reads durable progress and does not create another App. A
Mohist App conversation starts the same Server-side flow and returns to the
local CLI for secret-bearing steps.

### Set Up the Mohist App

1. Run `mo slack setup` on the Mohist host. Without Configuration credentials,
   it directs the user to generate the pair in Slack's App management page.
2. Mohist validates the Workspace, creates the Mohist App, and shows its
   installation link. The user confirms it in the browser. Setup waits if
   administrator approval is required.
3. Provide the Bot token and a `connections:write` App-level token through
   protected input.
4. Mohist validates the Workspace, App, Bot, and Socket, stores the credentials
   securely, and starts the local `mohist-slack` service. It shows Ready only
   after Socket identity is confirmed.

### Install an Agent

1. Select an active Agent that you may manage. Slack does not reveal Agents
   outside your authority.
2. Confirm the Bot name, avatar, description, requested permissions and their
   reasons, default access policy, and initial Connection Owner. The name is
   not an identity key; a collision receives a stable suffix.
3. Mohist creates a recoverable Connection and installation record fixed to the
   Agent and Workspace, then creates the Agent App. An unknown create result
   becomes **Result unknown** and is reconciled or arbitrated before another
   create attempt.
4. Complete Slack installation authorization. Cancellation, expiry, and
   pending approval resume the same App.
5. Mohist verifies Workspace, App, and Bot identities. A mismatch stores no
   credentials and binds no Connection.
6. Provide the Bot token and App-level token through protected input. The
   Connection is not Ready until both validate. Credentials never appear in
   Instructions, messages, logs, or transcripts.
7. Generate a short-lived, single-use claim code and send it in a DM to the
   Bot. The code is shown once; regenerating it invalidates the old code. Only
   a current full Workspace member can claim. External collaborators, Bots,
   and deactivated members cannot. A successful claim also proves the App can
   receive and reply to DMs.
8. Select the channel access policy. **Owner only** is the default. Allowlist
   uses member search. Every policy accepts DMs only from the Owner. After
   claim and a healthy connection, the state becomes **Ready**.
9. Invite the Bot to a channel, send a test task in DM, or mention it in a
   channel root message. Verify the result in Jobs and Sessions. If the Agent
   is not Ready, Slack shows a safe summary; only the Owner and operators see
   the specific gap.

The installation view shows Readiness, installation progress, connection health,
and identity sync separately. It highlights one current state and one next
action.

### CLI

`mo slack` covers Connection operations. See [CLI Reference](cli-reference.md)
for the command language.

CLI and Web UI operate on the same Connection record. `install-agent` also
resumes a record created from a Mohist App conversation. App creation, manifest
updates, credential submission, and delivery recovery are installation steps,
not separate commands.

`--allow-member` can be repeated and replaces the complete member list except
the Owner. Owner only and Anyone reject `--allow-member` before modification.
Member IDs are the CLI automation interface; the Web UI and Slack use member
search and avatars.

## Agent Connection Configuration

A Connection carries these settings:

- **Agent:** fixed at creation. Create another Connection for another Agent.
- **Slack Workspace:** confirmed by Mohist App installation, never a
  user-entered name.
- **Bot identity:** initialized from the Agent name and avatar, then managed in
  Slack and verified by Mohist.
- **Slack description:** initialized from Agent Description. It never becomes
  Instructions or a hidden prompt.
- **Runtime mode:** local Socket Mode with one Bot token and App-level token
  per App.
- **Owner:** a claimed Slack member. The Owner is the only caller by default
  and is always in the Allowlist.
- **Access policy:** who may invoke in channels. Owner only is the default.
- **Allowed members:** Workspace members who may invoke under Allowlist. DMs
  remain Owner-only.

A Connection reports independent installation progress, status, and identity
sync. `Degraded` carries one actionable reason. Identity drift never presents
as disconnection. If the Agent name or avatar changes, Mohist reports the
expected Slack values and the user updates Slack. A Bot token cannot read the
full App configuration, so Mohist reports a gap only when a capability fails.

Instructions, Runtime, Model, Variant, Skills, and concurrency limit belong to
the Agent. New work uses the new execution snapshot. A concurrency-limit
change applies to later launches and follow-ups, not running input.

## Slack Message Permissions

Mohist processes Bot DMs, explicit mentions, and replies in bound threads.
Other channel messages are discarded before a durable record or log.

The configuration view lists every requested permission and its reason. Invite
the Bot only where it is needed. The first version has no per-permission
toggles and no per-Connection channel list. The Bot accepts invocations from
every channel where it is present.

Mohist reads only basic member identity: IDs, names, and avatars. It never reads
member email, gives the directory to an Agent, or keeps it after Connection
delete.

Socket Mode needs no public inbound address. Slack retains unconfirmed
messages only briefly. Delayed Events may help during an outage, but recovery
is not indefinite. After a long outage, the status view warns that messages
may be missing and asks the user to resend critical delegations.

## Use Slack

### Start New Work

New work starts from a DM with no current Session or from a channel root message
that mentions the Bot.

After removing the mention, the message must contain task text or a usable
attachment. A bare mention gets a question, not an AgentJob. An attachment
alone is valid input.

On acceptance Mohist creates the AgentJob, AgentSession, first SessionInput, and
first AgentTurn. The **👀 Received** reaction marks acceptance. Liveness then
shows whether work is running or queued. A queued or running Turn can be
stopped. Agent replies, failures, and requests for human action return to the
same conversation.

A signed action button performs a supported operation, such as Stop or Retry,
under the presser's authority. Buttons are shortcuts to CLI and Web operations,
not a second command grammar.

Completing an AgentJob does not close its AgentSession. A user can answer a
question in the same conversation.

### Continue the Same Session

In a channel, a reply in the bound thread follows up the bound Session. In a
DM, every ordinary message continues the current Session, even after a Turn
ends. To start a fresh Session, begin the DM with `new task` followed by task
text. Separate channel threads provide parallel Sessions.

Follow-ups do not create another AgentJob. Every accepted message becomes a
SessionInput with stable identity. Input during execution steers the current
Turn or waits for the next one. Only explicit Stop interrupts. A full Session
queue rejects new messages and asks the sender to retry later. Accepted input
is never discarded.

Runtime failure does not end the Slack conversation. A DM input waiting for its
first Runtime binding is queued. A retry-safe infrastructure failure retries the
recorded work with its original snapshot, moves the DM route to the replacement
Session, and then accepts the current message there. Slack redelivery resolves
to the same retry and SessionInput.

An idle Session whose physical Runtime Session is confirmed missing is recovered
on the same Runner and logical AgentSession. Mohist never automatically replays
input while execution is active or its effects are unknown. Those states need
explicit reconciliation. `new task` is an intentional command, never recovery.

One thread can host several Agents:

- One bound Agent: an unmentioned reply continues its Session.
- Several bound Agents: an unmentioned reply is discussion; mention the target
  Bot.
- Mentioning another Bot starts an independent Session without contaminating
  the original one.
- One message mentioning several Bots starts no work and shows one chooser.
  Choosing an Agent starts exactly one execution from the original message
  under the selected Connection and Project. The signed chooser expires after
  five minutes and survives a Server restart without rerunning the prompt.
- A Bot's own message never becomes input for itself or another Bot.

Separate Mohist Servers do not coordinate one multi-Bot message.

### Mention in an Existing Discussion

A first Bot mention in a human discussion passes the Bot-visible thread history
as initial context and treats the mention as the task. Oversized context is
truncated oldest-first and marked in the Agent input and Slack confirmation.
If permissions, rate limits, or a Slack failure prevent a complete read,
Mohist rejects the delegation and creates no AgentJob.

Imported history is untrusted user input, not Instructions. Its maximum impact
is bounded by the Agent's configured capability. Editing an accepted message
does not rerun it. Deleting a message does not remove its AgentJob, Session, or
audit record.

### Files and Links

The Bot reads files that the message or thread explicitly provides. The files
become input attachments with their source preserved. An unreadable, oversized,
or unsupported file is reported as unused. Links remain message text. Mohist
does not fetch URLs; an Agent opens one only through its configured Skills and
Runtime permissions.

## Present Replies

Slack carries two signals with different owners:

- **Liveness** is owned by Mohist. Reactions are 👀 Received, ⏳ Working, ✅
  Completed, and ⚠️ exception. Mohist also maintains one replaceable status
  message. Every accepted input reaches a terminal liveness form on completion,
  failure, cancellation, Agent crash, or service restart.
- **The reply** is owned by the Agent. The Agent sends content through the send
  action and the injected reply anchor. Reasoning, tool calls, and intermediate
  output never become Slack messages.

Reactions are best-effort and never change work state. The Web Session timeline
holds the complete execution record. **Open in Mohist** links there when an
External Web URL is configured; otherwise Slack shows stable Job and Session
IDs. Mohist never sends a localhost address to Slack.

### One Input, One Answer

For each accepted input, Mohist may add 👀, ⏳, and one status message. The Agent
reply replaces the status message when possible. One input has at most one
status message and one final answer. Fast work may skip the status message.
Retries and duplicate delivery never create a second answer. If a status update
fails, Mohist appends the final answer once in the same thread and records a
diagnosable delivery problem.

The Connection ID, triggering message ID, and dispatch reference identify the
answer. Repeated sends converge only within the owning Connection and Turn. A
later input or another Connection gets a separate answer.

- Status fields such as exit code, artifact count, or IDs are metadata, not the
  Agent's answer.
- Silence is valid. A Turn with no reply is not a failure, and Mohist invents no
  summary.
- A failing Agent explains its failure with a reason and next step. Only a
  crash or non-response permits a system fallback, labelled as a system
  failure.
- Mohist renders Agent Markdown as Slack text. Unsupported tables and headings
  become readable text. Replies cannot trigger `@channel`, `@here`, or forge a
  control.
- A definite rejection follows platform retry rules. An unknown result is
  reconciled before retry. Remaining uncertainty appears as **Delivery
  uncertain**.
- Replaceable progress can coalesce. Final results, failures, and user actions
  are never silently dropped. If capacity is full, the Connection becomes
  Degraded (Backpressured) and rejects new Slack input while accepted work
  continues.
- Artifacts stay in Mohist. Slack shows result text, links, artifact names, and
  stable IDs, not copied artifact files.
- After restart or reconnect, delivery resumes from the last confirmed position
  without duplicating Jobs, Inputs, or confirmed replies.

## Slack Collaboration Rules for Agents

Mohist injects these rules as a visible, evolving Skill:

- Reply with the send action to the injected anchor. Reasoning and tool calls
  are invisible. Send a useful conclusion; send nothing when there is no new
  information.
- Do not send an empty acknowledgement. Silence is normal completion. Answer a
  direct human question even when there is nothing new to add.
- Call back after delegated work completes. Mention the delegator when the
  result needs their attention.
- Keep replies self-contained and proportionate. Put fine-grained progress in
  the Web Session timeline.
- Never guess the reply location. Mohist supplies the thread and message
  anchor.
- Resume silently after restart, Session recovery, or context compaction. Do
  not announce the interruption or ask how to proceed.

## Permissions

Three access policies decide who may invoke a Bot:

- **Owner only** (default): only the Owner may invoke in DMs and channels.
- **Allowlist:** DMs remain Owner-only; listed members may invoke in channels
  and bound threads.
- **Anyone:** DMs remain Owner-only; any verified full Workspace member may
  invoke in a channel where the Bot is present.

Anyone who can invoke the Bot can use every capability granted to the Agent.
Widening a policy is a permission grant. DMs are Owner-only under every policy.
Allowlist and Anyone reverify that the sender is a full Workspace member on
every invocation. Deactivated, restricted, external, Bot, and unconfirmed
identities are rejected. Anyone also verifies Bot channel membership.

Channel membership does not replace access policy. Slack Connect participants
and identities whose ownership cannot be confirmed cannot invoke in the first
version. An unauthorized user gets an explicit rejection and Mohist creates no
AgentJob or AgentSession.

Only the Connection Owner or Session starter can stop its Turn. A stale button
cannot stop a later Turn.

Access policy does not alter Agent capability. A Slack message cannot add or
replace capability, switch Project or Agent, or change policy. Only the Owner
can change invocation scope. A policy change applies to later inputs, including
follow-ups; it does not revoke accepted work or delete history. Only a Mohist
operator can start Owner transfer. The old Owner remains until a current full
Workspace member claims through a Bot DM.

## Lifecycle and Failures

- **Disable** pauses new Slack input and replies while accepted execution
  continues. The App and management facts remain. Enable restores delivery of
  current or final state, never stale progress from the disabled interval.
- **Remove binding** detaches the Connection and clears receipts, Session
  mappings, and pending delivery. It preserves Agent App management facts and
  does not uninstall the Slack App.
- **Permanent delete** deletes only a Mohist-created Slack App. It requires
  separate permission, explicit confirmation, complete audit, and no active
  binding. An unknown delete result is reconciled or arbitrated, never claimed
  as success.
- Agent edits never change running work. New AgentJobs use the new snapshot.
  Existing Sessions keep theirs. A description edit updates the expected Slack
  description, not Agent behavior.
- An archived Agent rejects new root delegations while existing Sessions remain
  readable and continuable. An Agent that needs setup gives a safe summary; an
  unknown Readiness waits for Runner validation and fails explicitly if it is
  unusable.
- Installation and recovery never create duplicates. An invalid credential
  makes the Connection Degraded and stops new input until the same identities
  are revalidated. A temporary Socket outage preserves the Connection.
- Owner loss makes the Connection Degraded with reason **Owner unavailable**;
  it never transfers ownership silently. Channel Allowlist and Anyone policies
  continue. DMs and Owner management wait for transfer.
- Delivery uncertainty settles to the same input record, never a replayed input.
- Capacity is not execution failure. Full concurrency or Session queues show
  work as queued or reject it with retry-later.
- Stopping a queued Turn ends it as cancelled. Stopping its first Turn ends the
  AgentJob with failure category `cancelled`.

## Implementation Gaps

The current management plane trusts its deployment boundary: any caller that
can reach it can request management operations. Expose it only on a trusted
local or administration network. Per-caller authentication, permission
isolation, and attribution remain an implementation gap. Slack installation
proves control of the Slack Workspace and App; it does not grant Mohist
management permission.

Multi-Bot interactive selection is delivered. Ambiguous root messages, explicit
multi-Bot thread mentions, and unmentioned replies in multi-bound threads show
one signed chooser. A choice starts at most one execution from the original
message under the selected Connection's Project. Pending choices expire after
five minutes and recover after restart. The original sender remains the
initiator of record.

## Non-goals

- A Slack Bot runs no Agent Runtime and owns no Agent configuration or hidden
  prompt.
- The Mohist App neither speaks for an Agent nor acts as a shared execution
  identity.
- Slack is not an Agent editor, Workflow board, Issue manager, or diagnostics
  console.
- A shared Bot never guesses a target Agent from natural language.
- Ordinary channel messages are not sent to Mohist. Only DMs, explicit mentions,
  and bound-thread replies trigger it.
- There is no Slack-native Agent entry point, Agent Home, streaming reply,
  slash command, or message shortcut. Structured control uses signed buttons.
- There is no public marketplace, multi-tenant hosting, billing, Slack Connect
  invocation, cross-company discovery, group DM, or cross-Server Bot
  coordination.
- Mohist artifacts are not copied into new Slack files.
- Installation is not one-command automation. The user confirms Slack
  installation, waits for required administrator approval, and provides the
  Bot and App-level tokens that Slack shows only to them. Mohist automates the
  surrounding creation, configuration, validation, and connection steps.
