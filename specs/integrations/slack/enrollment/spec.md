# Slack

Slack is an interaction interface for Mohist, alongside the Web UI and CLI.
This document defines what users can do in Slack. The component boundary and
wire contract are in [Slack Design](design.md).

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

`mo slack` covers Connection operations. See [CLI Reference](../../../interfaces/cli/spec.md)
for the command language.

CLI and Web UI operate on the same Connection record. `install-agent` also
resumes a record created from a Mohist App conversation. App creation, manifest
updates, credential submission, and delivery recovery are installation steps,
not separate commands.

`mo slack edit <id> --access-policy <owner_only|allowlist|anyone>` atomically
replaces the access policy and the member list. `--allow-member` can be
repeated and replaces the complete member list except the Owner; `allowlist`
with no members clears the list. Owner only and Anyone reject `--allow-member`
before modification, and the CLI rejects that combination, a missing or invalid
policy, a blank member, or an undeclared flag with exit 2 before any request.
Member IDs are the CLI automation interface; the Web UI and Slack use member
search and avatars.

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
