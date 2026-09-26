# Connections Design

## Connection in the Domain

AgentConnection belongs to the Agent domain: its binding, access policy, and
lifecycle are durable product behavior. Provider inbox, conversation mapping,
and pending delivery are integration records owned by Server infrastructure,
not business facts of AgentConnection or AgentSession. Only the Socket, the
current request, and the active send call are transient adapter state. A
Connection references an Agent without copying its execution definition.

### Staged Binding for `install-agent`

A Connection exists before Agent App creation, so the installation record has
a stable durable target before the first uncertain external write. External
identity is added only after installed credentials pass verification:

- `AgentId + WorkspaceTeamId` are immutable after Connection creation.
- `AppId + BotUserId` change atomically exactly once, from both empty to both
  non-empty; Team, App, and Bot are immutable afterward.
- Partial binding, team rebinding, and a second App/Bot binding are forbidden.
- One Project/Agent/team has at most one non-deleted Connection.

One application boundary enforces staged creation and identity completion for
every caller; generic Connection edits cannot mutate identity fields.

A Connection expresses four independent facts: external installation progress,
operator desired state (Enabled/Disabled), Slack connection health, and Agent
Readiness. `Connected` cannot replace them: a Connection may be connected
while its Agent is `needs-setup`, or the Agent `ready` while Slack is offline.
Views may expose all four, but a summary highlights one current state and
exactly one next action.

## Security Boundary

- All Slack ingress uses Socket Mode; no public ingress endpoint is required
  or opened. A proxy may be configured explicitly for Slack HTTPS and
  WebSocket traffic, but adapter transport to the loopback Server must not use
  that proxy.
- `mohist-slack` is a privileged local component in the Mohist Server trust
  domain. It receives only enough authority to call fixed Connections, read
  results, and return messages.
- The action-signing key and Connection Bot tokens share one self-hosted
  installation trust domain. The adapter may hold the key to forward a
  request, but Server revalidates signature, target Connection, actor, and
  executing Turn before acting. Neither is a Slack user credential, and
  neither may cross a trust boundary.
- Server encrypts App and Bot credentials, Mohist App credentials, and Agent
  App client/signing secrets, addressed by owner under Credential Ownership.
  They never enter Agent Instructions, transcripts, logs, client-visible
  state, durable rows, DTOs, or audit serialization. CLI releases its
  transient secret buffer immediately after submission; it never enters shell
  history, command arguments, or process environment.
- Member authorization uses stable Slack Workspace identity, never display
  name, avatar, or message text.
- Permission to invoke in a channel effectively borrows the Agent's configured
  execution capability, including repository writes, tools, and credentials.
  Access policy is an authorization decision, not a convenience toggle.
  Imported thread history is untrusted input whose maximum impact is bounded
  by Agent configuration.
- Audit records Workspace, conversation, and member identity for every call,
  but these identities never become Mohist administrators. Mohist App
  installer, Agent owner, Connection owner, and ordinary caller are four
  distinct roles.
