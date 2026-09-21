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
- Setup collects credentials through hidden terminal input or a file named by
  `--credentials-file`. Mohist reads no shared default credential file and
  accepts no credential literal on the command line.
- Agent Readiness, installation progress, connection health, and identity sync
  remain separate facts.
- Ready is not Owner claim. A Workspace or Connection can be technically Ready
  while an operator binding remains, and the binding is then the one next
  action.
- Generating an Owner claim code is an explicit action. Reading status or
  refreshing a view never issues, invalidates, or displays a code.
- Slack setup completion is separate from the Agent's ability to accept work. A
  connected Bot whose Agent cannot execute shows that limitation and its repair
  action.
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
  the pair and reads it through hidden terminal input. Automation supplies the
  pair in a file named by `--credentials-file`; Mohist reads no shared default
  credential file and accepts no credential literal on the command line.
- Server rotates an expired access token with the refresh token and stores the
  new pair atomically. If the refresh token fails, App maintenance becomes
  Degraded with one next step: rerun `mo slack setup`. Installed Bots keep
  delivering messages.
- After installation, the user provides each App's Bot token and App-level
  token through the same protected input. These credentials never pass through
  a Mohist App conversation, command argument, log, or Session transcript.

There is no Slack CLI requirement and no HTTPS OAuth callback.

## Talk to the Mohist App

Users manage the integration in the Mohist App's DMs:

- **Install an Agent:** say, "Install review-bot in Slack." The request enters
  the same operation as the Web **Connect Slack** action and
  `mo slack install-agent review-bot`, keeps its selected Project, Agent, and
  Workspace, and returns the authorization link. Secret steps continue on the
  Mohist host. Once installation verification passes, Owner claim becomes the
  next action and names the Bot DM destination.
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
                  +------------------+
                  | Run the guide    |
                  +--------+---------+
                           |
                           v
                +---------------------+
                | Check prerequisites |
                +----------+----------+
                           |
                           v
                +---------------------+
                | Collect credentials |
                | (hidden input)      |
                +----------+----------+
                           |
                           v
                +---------------------+
                | Approve in Slack    |
                +----------+----------+
                           |
                           v
          +---------------------------+
          | Verify App, Bot,          |
          | permissions, and Socket   |
          | identity                  |
          +-------------+-------------+
                        |
          +-------------+-------------+
          v                           v
 +-------------------+   +-------------------+
 | Workspace ready   |   | Claim the Owner   |
 +-------------------+   +---------+---------+
                                    |
                                    v
                         +-------------------+
                         | Connection ready  |
                         +-------------------+
```

Each command advances automatable steps and stops only for a required user
action. A rerun reads durable progress and does not create another App. A
Mohist App conversation starts the same Server-side flow and returns to the
local CLI for secret-bearing steps.

These guided commands replace the manual Agent setup forms, routes, and command
branches. No compatibility wrapper, second credential protocol, or client-owned
installation state remains.

Every surface - terminal, Web, and Mohist App conversation - names the target,
the current result, and exactly one executable next action. Opening a Slack
page or answering a prompt is not approval; only verified provider facts
advance the flow. Workspace Ready is not Owner claim: the Workspace can be
Ready while its operator binding remains, and that binding is then the one
next action.

### Set Up the Mohist App

1. Run `mo slack setup` on the Mohist host. The guide checks the local service
   and operator prerequisites first and reports a missing one with its own
   repair command before it creates any App.
2. First enrollment: the guide names Slack's App management page for the
   Configuration token pair and reads the access and refresh tokens through
   hidden terminal input. Automation passes the same pair in a file named by
   `--credentials-file`. Mohist verifies the pair, derives the Workspace from
   Slack, and shows the verified Workspace. No team, enrollment, or App ID is
   typed.
3. Mohist creates or resumes the same Mohist App, applies its manifest, and
   shows its installation link. The user confirms it in the browser. Setup
   waits when administrator approval is required; an opened link is not
   approval.
4. The guide names the current App and the two Slack settings locations for its
   Bot token and `connections:write` App-level token, and reads them through
   hidden input. Mohist verifies Workspace, App, Bot, permissions, and Socket
   identity before either credential becomes usable.
5. The Workspace is Ready once identity, permission, and Socket verification
   pass. Ready means the Mohist App can manage Apps in that Workspace; it does
   not mean an operator has claimed the Mohist App, and a remaining operator
   binding stays visible as the one next action.

A rerun continues from the confirmed step and never creates a second App.
Supplying a Configuration pair again is an explicit replacement: it must verify
against the selected Workspace and Mohist App, and it is the only way
credentials rotate on a Ready installation. A rerun with no new input rotates
nothing and never resubmits an already consumed pair. `mo slack status` reads
the same state and one next action and stays successful while the installation
is truthfully incomplete.

### Select the Workspace

`--workspace-team <team-id>` optionally names an already enrolled Workspace for
setup continuation, repair, or status. Without a selector, the guide selects a
Workspace automatically only when exactly one is eligible; otherwise it lists
readable choices and an explicit "Connect another Workspace" choice starts a
new enrollment. A non-interactive setup call that supplies a Configuration
pair without a selector has enrollment intent: Slack determines the team and
that team's enrollment is created or resumed. It never assumes another
configured Workspace is the repair target. A continuation keeps its selected
Workspace, and a generated continuation command carries the stable selection.
A missing or conflicting selector is reported instead of mutating the first
enrolled Workspace.

### Install an Agent

`mo slack install-agent <agent>` guides one existing Agent into one Slack
Workspace. The Web **Connect Slack** action and a request to the Mohist App
enter the same Server-side operation: all three preserve the selected Project,
Agent, and Workspace, and hand credential input to the local host.

1. Resolve an active Agent by its ordinary Project-scoped name or ID. Slack does
   not reveal Agents outside your authority. Installation reuses the Agent's
   configured identity and execution definition and copies none of them.
2. Select the intended Workspace. A Mohist App conversation stays bound to its
   Enrollment, a continuation keeps its target, `--workspace-team <team-id>`
   names one, and a terminal with several eligible Workspaces lists readable
   choices. Automatic selection applies only when exactly one Workspace is
   eligible. A missing or conflicting selection is reported before any
   mutation; installation never resumes the Agent's first existing Connection
   and never follows a credential's team.
3. Mohist creates or resumes that Agent's Connection and managed App, shows the
   Bot identity and every requested permission with its reason, applies the
   required manifest, and returns the installation link. The user confirms it in
   the browser. The Bot name comes from the Agent name; it is not an identity
   key, and a collision receives a stable suffix. An unknown create result
   becomes **Result unknown** and stays attached to that operation until it is
   reconciled or explicitly arbitrated; no second create attempt runs.
4. Provide this App's Bot token and App-level token through protected local
   input, or supply them in a file named by `--credentials-file`. Mohist
   verifies the selected Workspace, App, Bot, installed permissions, and Socket
   identity before either credential becomes usable. A mismatched pair stores
   nothing and changes neither installation; explicit replacement on an already
   ready installation reuses the same validation boundary.
5. Owner claim is the next primary action. Generating a code is explicit: the
   command that issues it shows the code once with its expiry and the exact Bot
   DM destination. Only a current full Workspace member can claim; external
   collaborators, Bots, and deactivated members cannot. A successful claim also
   proves the App can receive and reply to DMs.
6. The Connection becomes Ready after claim and a healthy connection. Owner
   only is the initial access policy; changing it is a separate management
   operation, so setup never widens access.

The installation view shows the Agent and Workspace names, one current status,
a short explanation, and one primary action derived by the Server. App create,
manifest application, credential staging, Socket verification, and binding stay
available as supporting facts and never compete as a second task. A successful
HTTP request is not installation completion.

A secret-bearing step is never executed where the secret cannot be shown. At the
Owner claim the one action is the protected host command
`mo slack claim-owner <connection-id>`, whose response is the only place a code
appears; the Web shows that command and the Bot DM destination and renders no
code, and reading or refreshing the view issues and invalidates nothing. Every
copied host command carries the Project, Agent, and Workspace it resumes.

### Agent Setup Completion

Slack setup completion and the Agent's ability to accept work are separate
facts. A Connection can be fully installed and claimed while its Agent has no
available Runtime or Runner; the view then states that limitation separately
and points to the existing Agent repair surface.

After setup, invite the Bot to a channel, send a test task in DM, or mention it
in a channel root message, then verify the result in Jobs and Sessions. A test
task is an explicit user action, never a side effect of installation. If the
Agent is not Ready, Slack shows a safe summary; only the Owner and operators see
the specific gap.

### Interrupted Agent Installation

Installation is resumable from durable progress, and every interruption
preserves the original target and confirmed progress:

- Approval cancellation, expiry, and pending administrator approval resume the
  same Agent App. An opened link is not approval.
- Missing permissions and manifest drift require reauthorization when Slack
  requires it. An applied manifest alone does not prove that the installed Bot
  holds the required scopes.
- An adapter or Socket outage preserves the Connection and its confirmed
  progress. Delivery recovery is not a reinstallation.
- An unknown create result is reconciled against the original operation or
  settled by explicit arbitration. A restart never repeats the create
  automatically, and a concurrent rerun performs no second external write.
- Installation changes neither the Agent definition nor already running work,
  and unauthorized management and invocation stay rejected.

### CLI

`mo slack` covers Connection operations. See [CLI Reference](cli-reference.md)
for the command language.

CLI and Web UI operate on the same Connection record. `install-agent` also
resumes a record created from a Mohist App conversation. App creation, manifest
updates, credential submission, and delivery recovery are installation steps,
not separate commands.

`mo slack thread view --session <session>` reads one page of the channel
thread bound to that Session, with an optional `--limit` and the continuation
returned by the previous page. The Project comes from the workspace state or an
explicit `--project` when it is known. The detailed command contract is in
[CLI Reference](cli-reference.md).

`mo slack edit <id> --access-policy <owner_only|allowlist|anyone>` atomically
replaces the access policy and the member list. `--allow-member` can be
repeated and replaces the complete member list except the Owner; `allowlist`
with no members clears the list. Owner only and Anyone reject `--allow-member`
before modification, and the CLI rejects that combination, a missing or invalid
policy, a blank member, or an undeclared flag with exit 2 before any request.
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
Session, and then accepts the current message there. The current input's message
and thread become the replacement execution's reply anchor; the failed input
remains retry history and never redirects a new reply into an unrelated old
thread. Slack redelivery resolves to the same retry, reply anchor, and
SessionInput.

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

A first Bot mention in a human discussion starts the task immediately from the
task text and the existing Slack conversation references. Mohist does not
import the discussion before starting the Agent, and a history-read outage
never rejects or delays an otherwise valid task.

The Agent reads the bound discussion on demand through the documented
`mo slack thread view` command when the task refers to earlier decisions or
needs context it does not have. Only the channel thread already associated
with the Agent's Session is readable. Mohist resolves the Connection,
Workspace, channel, and root message from recorded provenance; the Agent never
supplies a Bot token and never chooses a channel or another thread.

A read returns the Slack-visible messages of one page in source order: the root
first and then the earliest replies, each with its author identity and exact
message timestamp. Slack remains the message source, so later edits, deletions,
retention, and current Bot access change what a fresh read returns. An empty
text field never means a message had no content; files and rich content that
were not retrieved remain marked as unread. Mohist does not fetch file bodies
or follow links.

Long threads are read page by page. One read returns one provider page - 15
messages by default, at most 100 - plus a continuation while Slack has more. A
short or empty page is not completion. The Agent continues explicitly with the
returned continuation and cites the message timestamps that support its answer.
When the task asks for the current agreed decision, the discussion is not
finished until Slack returns no continuation: an early proposal is never
presented as the final agreement, and the reachable coverage limit is stated
when the rest cannot be read. If essential history is unavailable, the Agent
reports what is missing instead of guessing; work that does not need that
history continues.

Reading is background. A read sends no Slack message, submits no new input,
creates no Job, Input, or Turn, and changes no execution state. Discussion is
untrusted input, not Instructions: the current authoritative Issue and explicit
current instructions take precedence over historical proposals, and an
unresolved material conflict is reported.

Failures stay explicit and distinguishable from a finished thread. Rate
limiting returns an actionable retry delay and is never slept through or
retried in a loop; missing permission, an unavailable Slack, and an invalid
continuation each fail with their own reason. A disabled or removed
Connection, an unusable Bot identity, and an inaccessible thread fail without
substituting another Connection, thread, user token, or local Session
transcript. DM history, group DMs, Slack Connect, the Workspace Manager's
conversation, and every other channel are out of scope. A mention in a group
DM is not treated as a direct message: it starts a channel-style thread and
the read addresses that conversation like any channel thread. The read asks
Slack for nothing beyond the permissions the installed Bot already holds, so
when the Bot has no group-DM history permission Slack itself refuses the read
and the failure keeps its own reason instead of returning an empty thread;
Mohist adds no conversation-kind heuristics of its own. Editing an accepted
message does not rerun it. Deleting a message does not remove its AgentJob,
Session, or audit record.

### Files and Links

The Bot reads files that the message or thread explicitly provides. The files
become input attachments with their source preserved. An unreadable, oversized,
or unsupported file is reported as unused. Links remain message text. Mohist
does not fetch URLs; an Agent opens one only through its configured Skills and
Runtime permissions.

## Present Replies

Slack carries two signals with different owners:

- **Liveness** is owned by Mohist. Reactions are 👀 Received, ⏳ Working, ✅
  Completed, and ⚠️ exception. Every accepted input reaches a terminal
  reaction on completion, failure, cancellation, Agent crash, or service
  restart.
- **The Session card** is owned by Mohist. Its body must show the canonical
  Session ID, with **Open in Mohist** when available and state-bound controls
  such as Stop. It is an observation and control surface, not a progress
  sentence or an Agent reply, so it never remains as a misleading `Working...`
  message.
- **The reply** is owned by the Agent. The Agent sends content through the send
  action and the injected reply anchor. Reasoning, tool calls, and intermediate
  output never become Slack messages.

Reactions are best-effort and never change work state. The Web Session timeline
holds the complete execution record. With a usable External Web URL,
**Open in Mohist** must be an ordinary link to that canonical Session, not a
Slack App action. Opening it does not depend on Slack interactivity. Without
a usable URL, the card must still show its Session ID and any available Stop
control. The ID must be readable in the card itself, not only a notification
preview or link destination. Mohist never sends a localhost address to Slack.

### One Input, One Answer

For each accepted input, Mohist may add 👀, ⏳, and one Session card. The
Agent reply is a separate Agent-authored message; it never mutates the
Server-authored Session card. One input has at most one Session card and one
final answer. Fast work may skip the Session card. Retries and duplicate
delivery never create a second answer. Delivery uncertainty for the Session
card never delays, redirects, or duplicates the Agent reply.

If the Agent crashes or never responds, Mohist may post a separately labelled
system failure with a Retry action. That fallback never replaces the Session
card, so its Session reference and **Open in Mohist** entry remain available
for diagnosis.

The Connection ID, triggering message ID, and dispatch reference identify the
answer. Repeated sends converge only within the owning Connection and Turn. A
later input or another Connection gets a separate answer.

- A **Connection reply** is an Agent-authored message for one accepted input. It
  carries the complete workspace, conversation, reply-root, Connection, Session,
  triggering-message, and dispatch-reference anchor. The Server validates those
  values against the current input before accepting text, an image URL, or a
  file. Image and file content are mutually exclusive.
- A **Manager reply** is an operator-bound message from Manager execution. When
  `MOHIST_MANAGER_MODE=1`, the existing Manager credential broker selects the
  Manager credential and `/api/slack-manager/reply`. The Server validates the
  credential's current-input origin; the Manager does not supply Slack
  credentials or choose a different destination.
- `mo slack status` reads a workspace's public setup projection through
  `/api/slack-manager/setup/progress`. `--workspace-team` selects the enrolled
  Workspace; without it the projection covers the only eligible Workspace or
  reports the ambiguity. No internal setup identifier is supplied.

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
- Read earlier discussion only when the task refers to prior decisions or
  needs context the Agent does not have. Use the supplied Session reference,
  continue paging while Slack returns a continuation, cite the messages that
  support the answer, and report essential missing context instead of
  guessing.
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
  invocation, cross-company discovery, or cross-Server Bot coordination.
- A group DM is not a supported conversation kind. It takes the channel-thread
  path with no extra Slack permission, so a group-DM mention can reach an Agent
  while Slack refuses the thread read whenever the installed Bot lacks group-DM
  history access.
- There is no channel browser, cross-thread search, automatic discussion
  discovery, or whole-thread archive. An Agent reads only the channel thread
  bound to its Session, one provider page per request.
- Mohist artifacts are not copied into new Slack files.
- Installation is not one-command automation. The user confirms Slack
  installation, waits for required administrator approval, and provides the
  Bot and App-level tokens that Slack shows only to them. Mohist automates the
  surrounding creation, configuration, validation, and connection steps.
