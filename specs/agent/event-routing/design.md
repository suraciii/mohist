# Agent Event Routing

A Project-scoped routing table starts Mohist Agents from system events. This
document defines the table, matching, prompt rendering, and launch boundary.
[`event-protocol.md`](../../platform/events/spec.md) defines the event envelope and match
language. [`agent-execution.md`](../execution/design.md) defines Agent ownership and
lifecycle.

## Core Decisions

- One ordered Project table owns routing rules. An Agent subscription is an
  Agent-scoped view of that table, not another resource.
- Matching reads only the event envelope. It never queries a domain aggregate.
- Table order supplies priority. `Continue` supplies fanout.
- A rule starts an Agent through `IAgentLauncher`; it does not own execution.
- An event starts one Agent at most once, even when several trigger paths match.
- A rule that cannot execute is a skipped match, not a routing failure.
- Rules use the same event attributes and expression language for matching and
  prompt rendering.
- Routing has no `Priority`, `Arbitrate`, or `CoordinationMode` concept.

## System Boundary

```text diagram
      +------------+
      | CloudEvent |
      +------+-----+
             |
             v
 +-----------------------+
 | Project routing table |
 +-----------+-----------+
             |
             v
 +----------------------+
 | ordered active rules |<-----+
 +-----------+----------+      |
             |                 |
             v                 |
        +--------+             |
        | Match? +-------------++
        +----+---+             ||
             +--+              ||
                vyes           ||
      +-------------------+    ||
      | launch Agent once |    ||
      +---------+---------+    ||
                |              ||
                v              ||
          +-----------+        ||
          | Continue? |        ||
          +-----+-----+        ||
         +------+------+       ||
         vyes          vno     ||
   +-----------+   +------+  no||
   | next rule +<--| stop |----++
   +-----------+   +------+
```

The routing table consumes the CloudEvent Published Language from the
infrastructure layer. It cannot import `Workflow.Domain` or `Issue.Domain`.
System subscription handlers consume the same envelope through the dispatcher,
but routing rules remain the user-facing Agent consumer. An Agent response uses
normal commands such as `mo run approve` and `mo issue comment create`.

## Model

```text literal
RoutingRule (one ordered, Project-scoped table)
  Id, ProjectId, Name
  Position                  unique order used for evaluation
  Match                     CEL-subset expression
  AgentId                   responding Agent
  ResponsePrompt            {{event.<attr>}} template
  Continue                  continue after a match; default false
  Status                    active | archived | deleted
```

Each rule references an Agent in the same Project. The Agent subscription view
filters and edits rules by `AgentId` without changing their order or semantics.

## Evaluation Semantics

For an event with `projectid`, Mohist reads active rules for that Project in
ascending `Position` order:

1. Evaluate `Match`. A false result advances to the next rule.
2. On a match, render `ResponsePrompt` and attempt one Agent launch.
3. Skip a rule when its Agent is archived or its rendered prompt is empty. Log
   the reason as a structured routing result and continue.
4. Treat an expression evaluation error as no match, as defined by
   [`event-protocol.md`](../../platform/events/spec.md).
5. Stop after a launch when `Continue` is false. Otherwise evaluate the next
   rule.
6. If another rule or watch already launched the same Agent for the event, log
   and skip the later launch. Keep the first rule for attribution.

An event without `projectid` enters no routing table. A matching Agent is
launched through the normal Agent API and receives the event context through
its response prompt.

The order gives first-match exclusivity and visible fallback precedence. An
earlier `Continue` gives fanout. There is no numeric priority calculation.

## Write-time Validation

Rule creation and update reject:

- an expression that does not compile;
- a missing or inactive `AgentId`;
- an empty `ResponsePrompt`.

The write path owns validation. Runtime does not repeat it. An Agent archived
after validation is skipped at evaluation time.

## Prompt Rendering

`{{event.<attr>}}` substitutes an envelope property from the same namespace as
`Match`. Rendering is envelope-only and uses no general template engine. An
unresolved placeholder remains unchanged. The aliases
`{{workflow_run_id}}`, `{{stage}}`, and `{{event_type}}` remain supported.
`{{event.stage}}` works only when the event family promotes `stage`; rendering
never parses `data`.

## Idempotency and Visibility

The launch key is `hash(projectId, eventId, agentId)`. It is shared by routing,
watch, and mention trigger paths. The matching rule is attribution only and
is not part of the key.

A triggered AgentSession carries `mohist.io/trigger/event-id` and
`mohist.io/trigger/rule-id`. Event, rule, and AgentJob are queryable in both
directions. AgentJob owns response completion; AgentSession owns conversation
and audit evidence.

`deleted` is a storage tombstone. Deleted rules do not appear in reads and do
not match. Repeating deletion of a known rule returns the same confirmation;
an unknown ID returns `404`. A readable rule may reuse a deleted rule's name.

## Command Surface

Names follow [`cli.md`](../../interfaces/cli/design.md): the resource comes first, and Project scope
uses the active Project or `--project`.

`mo routing rule create --agent` and `mo routing rule edit --agent` accept a
Project-scoped Agent name or stable ID. The CLI resolves the reference before
mutation and sends only the stable `AgentId`. Edit sends only supplied fields;
omitted fields remain unchanged. Its PATCH presence vocabulary is `name`,
`match`, `agentId`, `responsePrompt`, and `continue`. The Server does not
resolve names or accept alternate field casing. See
[`decisions/routing-agent-reference.md`](../../../design/decisions/routing-agent-reference.md)
for this boundary.

## Non-Goals

- A separate AgentSubscription resource, matcher, or arbitrator.
- An Agent-specific approval channel. Agents use the regular command surface.
- Per-rule retry, outbox, trigger rate limits, cooldowns, or a per-Agent
  concurrency gate.
- Strict conflict prevention. Dry-run and visibility expose the configuration.
- Matching `event.data.*`. Promote a required routing dimension to an envelope
  attribute under [`event-protocol.md`](../../platform/events/spec.md).

## Status

The Project-scoped ordered table, `Position`, `Continue`, CEL-subset matching,
`{{event.*}}` rendering, envelope-only self-response protection, `mo routing
rule`, `mo routing test`, `mo event tail --match`, and the Agent subscription,
API, and Web views are implemented.

Implementation gaps:

- The durable launch key is still `(projectId, eventId, ruleId)`, so event and
  Agent coalescing applies only within one dispatch.
- Rule create and edit still send the raw CLI Agent value. Edit also serializes
  omitted values, and the Server PATCH presence vocabulary does not yet match
  store application.
## Agent Event Response

An event response starts one AgentJob from an event-routing decision and
records the resulting Agent work. [`event-routing.md`](design.md) owns
launch idempotency. This document defines response execution and attribution.

### Design Drivers

- An event records past facts. The responding Agent must verify current state
  before it acts.
- Routing, watches, and mentions can name the same work. One idempotency key
  must prevent duplicate starts without serializing unrelated responses.
- Owners must distinguish Agent decisions from human actions in history.

### Model

A response is not a new entity. It consists of one routing-triggered AgentJob
and the Agent-launch-origin AgentSession. When the Agent writes an Issue
comment, it explains the handoff to the owner.

```text diagram
        +-------+
        | event |
        +---+---+
            |
            v
        +-------+
        | route |
        +---+---+
            |
            v
 +---------------------+
 | check current state |
 +----------+----------+
            |
            v
   +----------------+
   | start AgentJob |
   +--------+-------+
            |
            v
    +--------------+
    | AgentSession |
    +-------+------+
            |
            v
  +------------------+
  | action or result |
  +------------------+
```

AgentJob decides whether the response completed or failed. AgentSession records Agent actions.

### Semantics

#### Launch and Current State

- One Agent starts at most once for one event, independent of whether the
  trigger came from a routing rule, watch, or mention. The durable key is
  defined by [`event-routing.md`](design.md).
- An event says what occurred. Before acting, the Agent uses the command
  surface to confirm current state. For example, it confirms that a run still
  waits at an Approval Point.
- Domain commands reject stale state explicitly. Approving a run that no longer
  waits is rejected. The Agent treats this rejection as a normal signal and
  does not retry it as an internal error.
- Responses for one Issue may run concurrently. Mohist adds no per-Issue lock;
  target aggregate validation rejects conflicting commands without dirty state.

#### Failure

- Terminal AgentJob failure, including preflight failure, emits
  `com.mohist.agent.job.failed` with `agentid` and available business lineage
  such as Issue, Epic, and WorkflowRun.
- The failure event enters the inbox and Hermes by default as an
  Agent-response-failed notification.
- `agent.job.failed` uses the normal routing protocol. A rule does not match
  when its AgentId equals the envelope's `agentid`. Mohist bases this decision
  only on envelope data and records a structured log.
- This self-response rule cannot stop a two-Agent cycle such as `A -> B -> A`.
  Users must detect such configuration through dry run and visibility.

#### Attribution

Every Agent decision must be distinguishable from a person's action.

- A comment records the authenticated Principal as its author.
  `--display-name` is presentation-only and cannot set or replace attribution.
- A decision at an Approval Point records the authenticated Principal in
  `decidedBy`. `--display-name` is presentation-only and cannot set or replace
  `decidedBy`. `mo run approve` and `mo run request-changes` accept it only for
  presentation. Decision events and read models include the authenticated
  identity and display name when present.
- Web UI Approve and Request Changes do not require an authenticated actor. An
  unsigned decision leaves `decidedBy` empty and does not synthesize `web`,
  `owner`, or another value.

### Non-Goals

- A per-Issue response serialization lock.
- Automatic response retry. Job failure surfaces through
  `agent.job.failed`; retry is a new event or manual action.
- Trigger rate limits or cooldowns. See [`event-routing.md`](design.md).
- Suppression of direct notification for a supervised event. See
  [`agent-supervision.md`](../supervision/design.md).

### Status

The `agent.job.failed` event and notification, authenticated Principal
attribution for comments and Approval Point decisions, and presentation-only
display names are implemented. The launch and current-state rules use the
existing routing and domain-command contracts.
## Agent Subscription Contract

An Agent subscription is the Agent-scoped view of the existing Project routing
table. The Server remains the only source of truth for subscription data and
event matching. The API adds no matcher, arbitration algorithm, or Slack
Connection lifecycle.

### Design Drivers

- Routing needs one match and priority model. Clients must not reconstruct it.
- Configuration remains readable when delivery is unavailable.
- Read state must distinguish an empty list from an unknown Agent, a missing
  Connection, and an unavailable Connection.
- Writes must preserve routing-rule validation and idempotency without starting
  execution.

### Model

`match`, `continue`, and `position` are the routing contract. The list uses the
same `position` as the Project routing table. A subscription does not own
Agent execution or delivery state.

### Semantics

#### Read states

A resolved Agent always receives an explicit state, including for an empty
list:

- `configured`: at least one subscription exists.
- `empty`: the Agent is active and the list is empty.
- `unconfigured`: Agent Readiness is `needs-setup`; the list remains
  authoritative and may be empty or contain saved rules.
- `unavailable`: a Connection exists but is incomplete, unhealthy, or
  disabled, so delivery cannot currently be relied on.
- `no_connection`: the Agent has no non-deleted Connection. Subscription
  configuration remains readable and writes remain local configuration writes;
  the response explains that delivery needs a Connection.

The state never changes a missing Agent into an empty list. Unknown Project or
Agent, authentication, authorization, transport, malformed JSON, and other
request failures retain their error status and API error code. CLI and Web
show an error state instead of an empty list.

#### Writes and lifecycle

Create and patch validate the existing routing expression and Agent rules.
Archived Agents reject create and patch with `409` and `agent_archived`.
Existing subscriptions remain listable and deletable.

Delete removes only the addressed routing rule from the active and readable
view. `deleted` is a storage-only tombstone: it is never readable, listed, or
routed. An unknown subscription returns `404`. Repeating DELETE for the same
known ID returns the same `deleted` acknowledgement.

Patch is final-state idempotent when submitted values already match. Create
uses the normal idempotency key: a repeated request with the same normalized
values returns the original resource and does not create another row. Reusing
a key for different values is a conflict. Without a key, a create retry is a
new request and is not silently deduplicated.

No subscription write starts a Runtime, probes Slack, mutates a Connection, or
changes event matching. A missing or unhealthy Connection is observable state,
not a reason to return a fake subscription list.
