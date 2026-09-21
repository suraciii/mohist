# Slack User Setup

Status: accepted

## Problem

Slack setup exposes two product paths for one operation. The Web UI creates an
empty connection and asks the user to create an App and paste credentials, while
the Server already has a managed, resumable App setup flow that neither the Web
UI nor the CLI uses. Internal identifiers are also required at the user
boundary. A user cannot tell which step is authoritative, and a configuration
repair means assembling a credentials file by hand.

## Decision

The managed Slack setup flow is the only product setup flow. The manual
connection-creation flow, its credential route, its identity-preview UI, and the
old CLI branch are removed. No compatibility wrapper, fallback, dual write, or
second user-facing protocol is added.

The user-facing contract is:

1. `mo slack setup` starts or resumes one workspace setup. The Web directs an
   unconfigured host to that protected local command. The user never supplies
   `workspaceTeamId`, `enrollmentId`, or an `agentAppId`.
2. Ordinary terminal calls collect credentials through hidden input. Automation
   names one protected, user-owned file with `--credentials-file`; no shared
   default credential file is read and no credential literal is accepted on the
   command line.
3. `--workspace-team <team-id>` optionally selects an already enrolled Workspace
   for continuation, repair, or status. Without a selector, exactly one eligible
   Workspace is selected automatically and several are offered as readable
   choices; no fallback chooses the first record in a list. First enrollment
   derives its identity from the verified Configuration pair.
4. The first workspace setup accepts one protected Configuration token pair.
   The provider-confirmed `team_id` is the workspace identity.
5. Mohist creates and applies the Mohist App manifest, returns one Slack
   install link, and waits for approval.
6. The user supplies the Mohist App Bot token and App-level token in one
   protected step. Mohist verifies the identity and Socket hello before
   reporting Ready.
7. A credential write is bound to the resolved target before submission. A
   mismatched pair changes no stored credential, and an explicit replacement
   pair is the only path that rotates credentials on a ready installation.
8. Web **Connect Slack** or `mo slack install-agent <agent>` starts the Agent
   sequence: select the Agent, approve the generated App, supply its two
   runtime tokens once, then claim the owner. Re-running the same entry point
   resumes the same records and reconciles interrupted provider writes.

Every response exposes one phase, one next action, and an actionable error.
Workspace Ready is a technical fact, not Owner claim; a remaining operator
binding is projected as the one next action. Secrets never appear in arguments,
URLs, logs, JSON responses, transcripts, or Slack messages. App and manifest
operations remain idempotent and fenced by the existing domain state machines.

`SlackManagerSetupOrchestrator` owns workspace setup and
`SlackInstallAgentService` owns Agent setup. Thin HTTP and CLI projections
expose only phase, next action, install URL, and actionable error; the durable
domain stores remain the state authorities. Configuration setup derives the
workspace from the successful rotation result and no longer accepts a
user-supplied workspace id. Agent setup accepts only a project and Agent
selection at the user boundary. Secret-bearing calls remain loopback/protected
and are never routed through the Manager conversation.

Creating an App is not complete until the generated manifest is applied and the
applied version/hash is persisted. Runtime credentials are not usable until Bot
identity and Socket hello verification succeed.

## Alternatives considered

- Keep the manual Web forms beside managed setup. Rejected because it leaves
  two authorities for one operation and still exposes provider and Mohist
  identifiers to the user.
- Wrap the old create/configure routes for compatibility. Rejected because a
  compatibility layer would preserve the duplicate protocol and its partial
  records.
- Replace local Socket Mode with hosted OAuth or one shared Bot. Rejected
  because that changes the self-hosted product boundary and still cannot issue
  the App-level Socket token required by this setup.
- Keep a shared default credentials file as the normal input. Rejected because
  it makes an automation path the default journey, leaves a plaintext secret
  file on disk between runs, and does not tell the user which step needs input.

## Consequences

- The normal path never asks the user to assemble configuration JSON or locate
  internal identifiers, and automation gains one explicit protected file instead
  of an implicit default.
- Setup must address more than one enrolled Workspace, so target selection and
  target-bound credential verification become part of the contract, and a
  missing or conflicting selector must fail rather than mutate the first record
  returned by a list.
- A ready installation can receive replacement credentials, so an ordinary
  rerun has to stay distinguishable from an explicit replacement and must not
  rotate credentials or resubmit a consumed Configuration pair.
- Progress becomes one Server-owned projection with exactly one primary action;
  the CLI and Web render it instead of deriving competing next actions.
- The obsolete setup paths, routes, forms, CLI branch, tests, and documentation
  are deleted as the replacement journey is delivered, so any remaining
  reference to the manual flow is a defect rather than a supported path.
- The Mohist App conversation keeps the same operations but carries no secret
  input; secret steps stay on the local host, which is a deliberate
  self-hosting constraint.
