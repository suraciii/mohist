# Self-Review: Issue 515 — Slack channel and thread Agent use

## Coverage Check

The proposal names both required capabilities, and both have a corresponding spec file. The specs
use the required `### Requirement` / `#### Scenario` structure and cover the issue's root mention,
thread follow-up, multi-Agent isolation, ambiguity, Owner-only access, provenance, redelivery, and
restart criteria. `tasks.json` is valid JSON and its dependencies form a DAG.

## Findings

### 1. [High] Thread binding is not channel-scoped, so two different channel threads can resolve to one session

**Location:** `design.md:101-109`, `tasks.json:T-002` acceptance criterion 1.

D2 defines the durable mapping key as `(ConnectionId, ThreadTs)`, while treating `ChannelId` only
as denormalized audit data. The current stable Slack message identity explicitly requires all three
`(WorkspaceTeamId, DmConversationId, MessageTs)` because `MessageTs` is guaranteed unique only
*within a channel* (`Infrastructure/Slack/SlackProviderModels.cs:4-17`). A Connection can be in
multiple channels, so two root messages with the same timestamp in different channels can collide
under the proposed mapping and route a reply in one channel into the other channel's AgentSession.

The binding's unique key and lookup API must include the Slack conversation/channel identity, for
example `(ConnectionId, ConversationId, ThreadTs)`. The multi-Agent binding-count query in finding
3 must use the same identity.

### 2. [High] The acknowledged launch-to-bind crash window violates the required restart continuity

**Location:** `design.md:266-269`, `tasks.json:T-002` acceptance criteria 1 and 5.

The design accepts a crash after `LaunchConnectionAsync` succeeds and before `BindAsync` persists
the thread mapping, claiming a subsequent reply can relaunch without duplicate work. That is not
true for the user-visible conversation: a later reply has a different Slack message identity and
therefore a different follow-up/launch idempotency key (`SlackConnectionRoutes.cs:467-475`). With
no binding, an unmentioned reply is ignored; if the user repeats the mention, it creates a second
AgentJob and AgentSession. Either outcome violates the issue's requirement that restart preserves
the original thread-bound session.

The plan needs a durable reconciliation path: persist enough thread identity and routed session id
with the accepted root inbox entry, then idempotently complete/recover the binding on redelivery or
recovery before classifying later replies. T-002 must include a fault-injection test for the exact
crash window, not only a restart after a completed binding.

### 3. [High] The plan cannot determine whether a no-mention thread reply has one or multiple bindings

**Location:** `design.md:105-107`, `design.md:146-149`, `tasks.json:T-002`, `tasks.json:T-003`.

D2 defines only `GetSessionIdAsync(projectId, connectionId, threadTs)` and `BindAsync(...)`. D3
then requires the ingress to distinguish a thread bound to exactly one Agent from one bound to two
or more Agents. A per-Connection lookup cannot supply that cardinality. D4's proposed workspace
Bot query only tells the Server which Bots exist; it does not tell it which Connections are bound to
this thread. Neither task requires a thread-binding list/query or an index that can implement it.

Add an explicit Server-authoritative `ListBindingsAsync(projectId, conversationId, threadTs)` (or
equivalent count/query) backed by a channel-scoped index. T-003 must consume it to implement the
multi-Agent no-mention branch and its tests.

### 4. [Moderate] The plan cannot implement the required Bot/self and unidentified-sender gate from its normalized envelope

**Location:** `design.md:78-83`, `design.md:137-139`, `tasks.json:T-002` acceptance criterion 4.

D1 adds only `threadTs` and `mentionedUserIds`, but D3 requires the Server to ignore Bot and
unidentified senders. The current adapter exposes only `event.user` as a required string
(`mohist-slack/src/adapter.ts:152-168`); it forwards no `bot_id`, message subtype, or membership
verification result. Events without `user` currently throw before the Socket Mode acknowledgement
(`adapter.ts:103-109`, `:158-159`), causing redelivery rather than an explicit ignore. A plain
`OwnerSlackUserId` comparison can reject an unknown identity but cannot classify Bot-self as ignore
or establish that an identity belongs to the workspace.

The plan must define one authority for normalized sender kind and workspace identity: carry the
relevant Slack event facts and classify them in Server, or resolve the sender through the existing
Server-side Slack member client. It must also test that ignored events are acknowledged and leave no
inbox row or persisted text.

### 5. [Moderate] The ambiguity prompt destination remains an open question despite a normative spec requirement

**Location:** `design.md:301-302`, `tasks.json:T-003` acceptance criterion 3,
`specs/channel-attribution/spec.md:60-72`.

The spec requires the choose-one prompt in the originating conversation. For an ambiguous thread
reply, this means the thread; for an ambiguous root message, it means the channel root. The design
leaves "thread or channel root" as an Open Question, and T-003 accepts "originating channel or
thread", allowing a root-level prompt for a thread reply. That breaks the thread-local interaction
model and leaves the delivery input unspecified.

Decide and encode the routing rule in D5 and T-003: use the inbound `threadTs` when present, and
omit it only for a root message.

## Verdict

The artifacts cover the intended product surface, but the binding identity, crash recovery, and
multi-Agent binding query are incomplete enough to permit cross-channel context leakage, lost
thread continuity, or guessed routing. The sender gate and prompt destination also need a concrete
implementation contract before build work can safely start.

<promise>FAIL</promise>
