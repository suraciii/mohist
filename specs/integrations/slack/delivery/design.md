# Delivery Design

## Reliability Contract

Slack-to-adapter transport is externally at-least-once; the system cannot
claim end-to-end exactly-once.

- Deduplication occurs in Server: the same Slack message identity that became
  input always resolves to the same SessionInput.
- Provider inbox and SessionInput deduplicate separately; redelivery after a
  lost request result accepts neither twice.
- Accepted input cannot be deleted through drop-oldest or similar policy;
  reject new input when capacity is exhausted.
- The outbound outbox is bounded; the replaceable Session card may coalesce;
  final results, explicit failures, and user actions never disappear silently.
- Slack delivery failure never changes AgentJob or AgentTurn results.
- Messages authored by a Mohist Bot are rejected at admission before any
  durable record: an Agent can never trigger itself or another Agent through
  its own replies.
- A long outage may exceed Slack event retention. After recovery, display that
  a gap may exist.

Control-plane create/delete is likewise at-least-once: a repeated attempt does
not repeat App creation/deletion, and an unknown result converges only through
reconciliation or human arbitration under Four-Axis State.

### State Projection and Message Identity

Server is the sole judge of AgentSession and AgentTurn state; provider and
adapter project only Server-confirmed state and cannot infer success from a
Slack API response or Runner output.

An accepted input uses stable message identity as input identity and derives
one dispatch reference for the work item. Per dispatch reference, Server
allows:

- At most one replaceable Session-card projection, persisted with its provider
  message identity. Its blocks show the canonical Session reference,
  navigation, and state-bound controls. Its top-level text retains the Session
  reference as a fallback. It never carries Agent-authored text.
- At most one terminal Agent reply, deduplicated by a stable Turn delivery key.
  It is a separate message in the same thread and never depends on the Session
  card's Pending, Claimed, Delivered, or Delivery uncertain state.
- Fast work may omit the Session card and project only the Received reaction
  plus one final answer. If the platform cannot react on the user's message,
  the Received fallback remains a Server-authored receipt.

Slack renders blocks independently from the top-level text fallback. Every
Session card must start with a `section` whose `plain_text` is
`Session: {sessionId}`, using the canonical Session ID. Append an optional
navigation `section` with `mrkdwn` text `<url|Open in Mohist>`, then the
existing signed control blocks unchanged. Missing navigation or controls must
not remove the identity section; an ID-only card still has that one block.

Navigation must be an ordinary link, not a button. It carries no `action_id`
or `value` and creates no provider interaction inbox entry. Its URL uses the
resolved project name and the same canonical Session ID, with escaped path
segments and the configured base path. Allow HTTPS, or HTTP with an explicitly
allowlisted development origin. Reject malformed, credentialed,
query-bearing, fragment-bearing, localhost, loopback, private, and link-local
origins. The development allowlist must not bypass local-host rejection.
An unusable origin or unresolved project omits only navigation.

Default reactions: `Received=👀`, `Working=⏳`, `Completed=✅`, exception `⚠️`.
Reactions are liveness signals, not Session/Turn facts; a missing, late, or
successful reaction mutation never changes Server state.

### Reply Body Belongs to the Agent; Liveness Belongs to Server

- **Server owns liveness and the Session card.** Reaction mutation is
  best-effort: a bounded, logged failure never blocks or fails a Turn. The
  Session card contains only a stable Session reference, navigation, and
  state-bound controls; it is not a textual execution status. Once queued, it
  is never promoted into or overwritten by a terminal reply or failure notice.
- **The Agent owns the reply body.** During a Turn it sends what it wants to
  say through the **reply action**: an intent API to Server carrying body and
  reply anchor. The reply action enters the same outbox and reuses redaction,
  duplicate protection, anchor validation, and uncertain-result
  reconciliation. The Agent never connects directly to Slack. The final answer
  is independent from the Server-authored Session card.

Server never extracts assistant text from Runner Turn output; the reply action
is the only reply source. Terminal handling owns reaction closeout for every
session kind: every accepted input reaches Completed or attention on every
outcome — completion, failure, cancellation, Agent crash, or Server restart.
The outbox persists the closeout intent, while the neutral Session card remains
a valid observation entry instead of a stale `Working...` claim.

A system-authored fallback for Agent crash or complete non-response is a
separate outbox message with its own stable dispatch reference. A retry action
may be attached to that fallback, but the fallback never reuses the Session
card's provider message identity and never replaces its navigation surface.

- **Silence is valid.** A Turn that ends with no reply action closes liveness
  normally; Server invents no status summary.
- **The Agent reports failure.** On execution failure or required human
  action, the Agent sends reason and next step through the reply action. Only
  an Agent process crash or complete non-response permits a system-authored
  fallback explicitly labeled as a system failure.
- Detecting a Turn that ended without publishing belongs to the Runtime as an
  advisory reminder to the model, never to Server, which neither synthesizes
  the missing reply nor treats silence as failure.

### Reply Action CLI: `mo slack message send`

- **Explicit destination.** The exact invocation is in the [CLI reference](../../../interfaces/cli/spec.md#slack). The CLI maps one validated request carrier to the Server wire fields and refuses a partial anchor before making a request. The Agent reads the anchor from injected context instead of choosing a destination from memory.
- **Explicit ownership and dispatch identity.** Server validates the complete
  anchor against durable Session provenance and its Turn before it scopes
  terminal reply selection and coalescing to that logical Turn. Distinct sends
  are accepted only while the Turn is active; an identical retry after
  terminal completion still returns the committed delivery intent.
- **Manager separation.** `MOHIST_MANAGER_MODE=1` selects the existing Runner
  credential broker. The CLI sets the Manager marker, uses the broker-issued
  Manager credential, and calls the dedicated Manager reply route without
  reading bearer values from the environment.
- **Body through `--text`.** A string, or `-` for stdin so shell escaping does
  not consume newlines. The Agent writes standard Markdown; the renderer
  converts it to Slack mrkdwn and degrades unsupported tables and headings to
  readable text without error.
- **Mentions in body.** The CLI resolves `@displayname` to a valid Slack
  mention; ambiguity or an invisible target returns an actionable error and
  sends nothing.
- **Images.** `--file` uploads a local image; `--image` references a public
  URL. An Agent may explicitly send a useful screenshot or artifact.
- **Success means outbox acknowledgement.** Send synchronously commits to the
  outbox, confirming that reliable delivery was accepted, not that Slack
  displayed it. Eventual Slack failure surfaces as Delivery uncertain without
  reporting back into Agent execution. A different payload cannot mutate a
  claimed or delivery-uncertain intent and is rejected instead of
  acknowledged.
- **Multiple sends coalesce.** Within one Turn, Server coalesces sends into
  one final answer for that dispatch reference. An extension that would exceed
  Slack's single-message limit is rejected rather than duplicated through
  overflow posts. The invariant remains at most one final answer per input.
- **Scope.** No broadcast across channels and no distinct message types.
  `mo slack message` is a command group; only `send` is implemented.

### Signed Action Buttons

Slack interactivity reaches Server as `block_actions` provider interactions.
Every interactive control carries a Server-signed payload bound to Connection,
conversation, actor Slack identity, target resource, and expiry. Server
revalidates signature, freshness, and actor authorization on every click; a
stale, foreign, or expired action is rejected with a visible notice. An
accepted click enters the durable provider inbox like any other input and
delegates to the same application service the equivalent CLI or Web call uses.
Buttons are shortcuts to existing operations, never a second command grammar:
they carry no free text, and their effects are exactly the effects of the
operation they name.

Current actions: Stop a queued or running Turn, Retry from a failure notice,
and Agent selection for a multi-Bot mention. The chooser preserves the
original message facts and starts exactly one execution under the selected
Connection's owning Project. Mohist approval gates are not Slack actions;
routing them into Slack requires a notification routing policy.

### Delivery Intent, Claim/Ack, and Unknown Results

Server persists one delivery intent per logical projection, carrying the
Connection, dispatch reference, target conversation/thread, projection kind, a
stable deduplication key, the current provider message identity if known, and
a replayable content reference. Projection kinds: replaceable Session card,
terminal Agent reply / explicit failure, user action, reaction mutation. Tool
calls and Runner logs are not user messages.

```text diagram
                         +---+
                         | * |
                         +-+-+
                           |
                           v
                      +---------+
                      | Pending |<---------------------------+
                      +----+----+                            |
                           |                                 |
                           v                                 |
                      +---------+                            |
                      | Claimed |                            |
                      +----+----+                            |
   +---------------+-------+-------+----------------+        |
   v               v               v                v        |
+-----------+   +-----------+   +-----------+   +-------------+ |
| Delivered |   | Retryable +---| Uncertain |---| Dead-letter |-+
+-----+-----+   +-----------+   +-----------+   +-------------+
     |
     v
   +---+
   | * |
   +---+
```

- **Claimed**: one adapter holds a short lease. Claim is not delivery success,
  and a second adapter cannot project the same intent.
- **Delivered**: definite provider success; provider identity persisted;
  duplicate ack is idempotent.
- **Retryable**: definite retryable rejection. Return to the same intent;
  never create another progress or final answer.
- **Uncertain**: timeout, connection loss, or unparseable response. Never
  resend blindly: reconcile by stable identity first, and retry the original
  intent only after confirming no side effect occurred. A segmented intent
  follows the same rule part by part: only a complete read showing that none of
  its parts landed authorizes the sequence again, a partly present sequence
  stays unknown, and a provider rejection that interrupts the sequence after
  the first part settles unknown instead of re-posting the confirmed parts.
- **Dead-letter**: definite non-retryable failure or human intervention.
  Retain the intent, reason, and actionable next step. A confirmed AgentTurn
  result is never rewritten as provider failure.

`chat.update`, reaction mutation, and progress creation follow the same
claim/ack/uncertain semantics. When an update is confirmed impossible, Server
may append exactly one final answer in the same thread, under its own stable
terminal delivery key, so retry, reconnect, and duplicate ingress never append
a second final answer.

### Delivery Notices and Re-send

A delivery notice is one outbox intent per original delivery, under the
reserved dispatch key `slack-delivery-notice:{originalDeliveryId}`, reusing the
terminal explicit-failure kind so the outbox keeps a single state machine and
its existing capacity, ordering, and dead-letter rules. It targets the original
Conversation and thread, carries no Agent text, and renders the Session card's
identity section plus the same optional navigation rules. Its statement is
carried in a `section` block of the message body: Slack renders a blocks
message from its blocks, so the top-level text stays only the notification
fallback.

Settlement and notice authoring are separate writes. The dispatcher's
notice-recovery sweep therefore re-derives the obligation from durable row
state — a content row settled uncertain or dead-lettered whose notice intent is
missing — and authors it idempotently, so a crash between the two writes, or a
single failed authoring attempt, converges on the same notice on a later tick
instead of leaving the delivery permanently silent. The sweep is bounded in
progress as well as in size: it selects only rows whose owner can currently
receive a notice, and it resumes after the last row it examined, restarting
from the beginning once the batch comes back short. A row that cannot be
authored — a gone owner, a payload that no longer parses — keeps its obligation
without holding the first slots of every tick, so one owner's failure never
stalls another owner's notice.

Server authors it only for content intents — terminal Agent reply and
replaceable Session card — and only on the settlement transition that actually
happened: unknown (claim timeout, uncertain ack) or dead-letter (retry budget
exhausted, unknown retention expired). Reaction mutations, explicit failures,
and notices themselves never produce a notice, so no notice about a notice can
exist. The dispatch key makes a replayed ack, a restart, an operator retry, or a
sweep rerun converge on the same intent instead of posting again.

The original intent carries the canonical Session ID so the notice can render
the identity section, and it keeps the uncertainty evidence: once an intent has
been unknown, its uncertainty timestamp is never cleared by a retry, a merge
back to Pending, or a re-send — only a confirmed provider identity resolves it —
so a later exhaustion notice cannot claim the content was never delivered.
Payload facts (`notice`, `possiblyDelivered`) keep that distinction queryable
without parsing message text.

The re-send entry points never queue a mutation directly. An unknown intent
stays unknown and is left to the adapter's claim-uncertain path, which
reconciles through provider history and re-posts the original payload only when
the evidence proves the mutation absent. The evidence must cover the original
Conversation and the original thread: a threaded intent is reconciled against
`conversations.replies` for that thread, and an incomplete pagination, a
permission failure, or any other inconclusive read is treated as inconclusive —
the delivery stays unknown and performs no provider mutation. An exhausted
intent is revived into unknown with a fresh retention window, so it passes the
same reconciliation before any provider call. Both paths revalidate the
Connection is live and Enabled before accepting the request — an unknown
delivery as well as an exhausted one — and keep the intent's original
Conversation and thread, so a changed binding is never redirected and original
content is never leaked; the Agent's repeated send for an existing terminal key
follows the same rule instead of reporting convergence for content that never
landed.

### Capability Boundaries

The integration separates four capabilities because each has a different
authority and failure mode:

- **Ingress translation** belongs to `mohist-slack`. It normalizes wire events
  and acknowledges only a definite Server decision. It does not authorize the
  caller, choose a Session, or invoke an Agent.
- **Admission and execution arbitration** belong to the Server Connection
  boundary and Agent API: authorize stable Slack identities, deduplicate
  input, choose start or follow-up semantics, expose canonical Session/Turn
  state. A provider response cannot alter those facts.
- **Reply intent and liveness projection** are separate Server capabilities.
  The Agent authors reply body through the reply action; Server derives only
  liveness from canonical execution state. The separation makes silence valid
  and prevents status rendering from inventing Agent speech.
- **Provider projection** starts from a durable delivery intent. The adapter
  translates it to Slack post, update, upload, or reaction operations under
  one Socket lease. It cannot reinterpret work state or choose different reply
  content.

Markdown conversion, segmentation, attachments, and Slack control-syntax
escaping are provider rendering concerns after Server redaction. The adapter
never parses Runner logs, overrides Agent configuration, or writes the Mohist
database directly. Manager and Agent Connections use the same boundaries; the
Manager adds only the capability credential and its Agent-facing management
surface.

When a Connection is Disabled, the adapter still acknowledges the Slack event
at the transport layer, records audit, and discards it: no SessionInput,
AgentJob, or delivery intent is created, and the event cannot be replayed
after re-enable. Already accepted work remains under Mohist arbitration, but
no new Slack replies are claimed or sent while Disabled. After re-enable,
project only still-relevant current or final state, never stale progress from
the disabled interval. Remove binding and permanent delete remain distinct,
explicitly confirmed lifecycle operations; neither deletes the Mohist Agent or
AgentSession.
