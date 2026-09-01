# Agent Event Routing

A Project-scoped routing table starts Mohist Agents from system events. This
document defines the table, matching, prompt rendering, and launch boundary.
[`event-protocol.md`](event-protocol.md) defines the event envelope and match
language. [`agent-execution.md`](agent-execution.md) defines Agent ownership and
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
   [`event-protocol.md`](event-protocol.md).
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

Names follow [`cli.md`](cli.md): the resource comes first, and Project scope
uses the active Project or `--project`.

`mo routing rule create --agent` and `mo routing rule edit --agent` accept a
Project-scoped Agent name or stable ID. The CLI resolves the reference before
mutation and sends only the stable `AgentId`. Edit sends only supplied fields;
omitted fields remain unchanged. Its PATCH presence vocabulary is `name`,
`match`, `agentId`, `responsePrompt`, and `continue`. The Server does not
resolve names or accept alternate field casing. See
[`decisions/routing-agent-reference.md`](decisions/routing-agent-reference.md)
for this boundary.

## Non-Goals

- A separate AgentSubscription resource, matcher, or arbitrator.
- An Agent-specific approval channel. Agents use the regular command surface.
- Per-rule retry, outbox, trigger rate limits, cooldowns, or a per-Agent
  concurrency gate.
- Strict conflict prevention. Dry-run and visibility expose the configuration.
- Matching `event.data.*`. Promote a required routing dimension to an envelope
  attribute under [`event-protocol.md`](event-protocol.md).

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
