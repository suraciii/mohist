# Slack User Setup

Status: accepted

## Problem

Slack setup currently exposes two different product paths. The Web UI creates
an empty connection and asks the user to create an App and paste credentials;
the Server already has a managed, resumable App setup flow, but the Web UI and
CLI do not use it. Internal identifiers are also required at the user boundary.

The result is that one product operation is expressed as several unrelated
forms, routes, and commands. A user cannot tell which step is authoritative.

## Decision

The managed Slack setup flow is the only product setup flow. The old manual
connection-creation flow, its credential route, its identity-preview UI, and
the old CLI branch are removed. No compatibility wrapper, fallback, dual write,
or second user-facing protocol is added.

The user-facing contract is:

1. `mo slack setup` starts or resumes one workspace setup. The Web directs an
   unconfigured host to that protected local command. The user never supplies
   `workspaceTeamId`, `enrollmentId`, or an `agentAppId`.
2. The first workspace setup accepts one protected Configuration token pair.
   The provider-confirmed `team_id` is the workspace identity.
3. Mohist creates and applies the Mohist App manifest, returns one Slack
   install link, and waits for approval.
4. The user supplies the Mohist App Bot token and App-level token in one
   protected step. Mohist verifies the identity and Socket hello before
   reporting Ready.
5. Web **Connect Slack** or `mo slack install-agent <agent>` starts the Agent
   sequence: select the Agent, approve the generated App, supply its two
   runtime tokens once, then claim the owner. Re-running the same entry point
   resumes the same records and reconciles interrupted provider writes.

Every response exposes one phase, one next action, and an actionable error.
Secrets never appear in arguments, URLs, logs, JSON responses, transcripts, or
Slack messages. App and manifest operations remain idempotent and fenced by
the existing domain state machines.

## Server boundary

`SlackManagerSetupOrchestrator` owns workspace setup and
`SlackInstallAgentService` owns Agent setup. Thin HTTP and CLI projections
expose only phase, next action, install URL, and actionable error; the durable
domain stores remain the state authorities.

Configuration setup derives the workspace from the successful rotation result
and no longer accepts a user-supplied workspace id. Agent setup accepts only a
project and Agent selection at the user boundary. Secret-bearing calls remain
loopback/protected and are never routed through the Manager conversation.

Creating an App is not complete until the generated manifest is applied and
the applied version/hash is persisted. Runtime credentials are not usable
until Bot identity and Socket hello verification succeed.

## Removal order

1. Implement and test the managed user-setup service and its progress view.
2. Switch CLI and Web to that service.
3. Delete the old Web identity/credential steps and old create/configure route.
4. Delete the obsolete CLI branch and update the single command reference.
5. Remove tests and documentation for the deleted path; keep only diagnostics
   and recovery operations that belong to the managed state machine.

Existing durable setup records are resumed by the managed service. Existing
Slack Apps are not deleted as part of this product change.

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

## Acceptance

- A new user sees only Connect Slack, one install approval, and protected token
  steps; no internal id is requested.
- Re-running after interruption does not create a second App, Connection, or
  credential binding.
- `manifestState` is `applied` before any setup reports Ready.
- Credential material is absent from all user-visible and diagnostic output.
- Server, Runner, and adapter live verification uses the same candidate SHA;
  Manager and Agent targets are active, credential-verified, and leaseable.
- The full repository gate `npm run verify` passes.
