# Delivery

Return results for accepted [Slack interaction](../interaction/spec.md).
The [Connection](../connections/spec.md) owns access and identity;
this feature defines the visible signals and their delivery boundaries.

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
- `mo slack status` reads workspace status through `/api/slack-manager/status`
  with `workspaceTeamId` in the query and the management credential. Manager
  status and management requests use the same broker and never a Connection
  credential.

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
