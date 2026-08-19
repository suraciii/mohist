---
status: converged
---

# Agent Event Routing

A Mohist Agent responds to system events automatically through a
Project-scoped **event routing table**, without a manual launch. Agent means a
Mohist Agent throughout this document; see
[`agent-execution.md`](agent-execution.md) for terminology and ownership
invariants. See [`event-protocol.md`](event-protocol.md) for the envelope and
match-expression syntax.

This design replaces the earlier subscription plus priority-arbitration model,
which used a separate AgentSubscription aggregate and Arbitrate operation. The
current Agent subscription surface is an Agent-scoped configuration view over
the same RoutingRule table for API, CLI, and Web addressing. It does not
reintroduce a separate persisted object, matcher, or arbitrator.

## Boundary

Routing belongs to the Agent context and consumes the CloudEvent PL from the
infrastructure layer. It cannot import `Workflow.Domain` or `Issue.Domain`.
Matching and rendering use only the envelope and perform no cross-domain query.

## Model

```text literal
RoutingRule (one ordered, Project-scoped table)
  Id, ProjectId, Name
  Position                  Unique position; evaluation uses this order
  Match                     CEL-subset expression from event-protocol.md
  AgentId                   Agent that responds
  ResponsePrompt            Template with {{event.<attr>}} placeholders
  Continue                  Continue evaluation after a match; default false
  Status                    active | archived | deleted
```

Each Project has one table. Rules reference Agents, while execution ownership
remains in the Project-scoped table. The Agent subscription view shows and
modifies rules for one `AgentId`. It can express that one Agent has several
subscriptions without moving ordering, fallback, or takeover order under the
Agent.

## Evaluation Semantics

The routing table is Project-scoped. An event envelope without `projectid`
enters no routing table, consistent with existing dispatch behavior. When an
event with `projectid` arrives, Mohist reads that Project's active rules in
ascending `Position` order:

1. Evaluate `Match`. Continue to the next rule when it does not match.
2. On a match, render `ResponsePrompt` and launch the Agent through
   `IAgentLauncher`. Stop when `Continue == false`; otherwise evaluate the next
   rule.
3. Treat a match that cannot execute as no match, write a structured log, and
   continue. This includes an archived Agent or an empty rendered prompt.
4. Treat a runtime expression error as no match, as defined in
   `event-protocol.md`.
5. Launch the same Agent at most once for one event. When an earlier rule or
   watch declaration launched it, log and skip any later matching rule for that
   Agent. Use the response prompt from the first rule that launched it.

This produces:

- **Exclusive by default**: First match wins. Table order is priority, without
  numeric priority arithmetic.
- **Fanout**: An earlier rule sets `Continue`.
- **Fallback and takeover**: Specific rules appear above fallback rules.

There is no Arbitrate operation, Priority field, or CoordinationMode.

## Write-time Validation

Creating or updating a rule rejects:

- A `Match` expression that does not compile.
- A missing or inactive `AgentId`.
- An empty `ResponsePrompt`.

Runtime performs evaluation only. It does not repeat validation as a fallback;
an Agent archived after validation is a runtime skip.

## Rendering

`{{event.<attr>}}` directly substitutes an envelope property from the same
namespace used by Match. Rendering is envelope-only and uses no template
engine. An unresolved placeholder remains unchanged. The old
`{{workflow_run_id}}`, `{{stage}}`, and `{{event_type}}` tokens remain aliases.
`{{event.stage}}` depends on `stage` being promoted into a Workflow-family
envelope and does not parse `data`.

## Idempotency and Visibility

- Launcher key is `hash(projectId, eventId, agentId)`. One event launches one
  Agent at most once, regardless of trigger path through routing rule, watch,
  or mention. Duplicate delivery does not create another Job because it uses
  the AgentLauncher idempotent-launch mechanism. The matching rule is trigger
  attribution only and does not enter the idempotency key.
- A triggered AgentSession carries `mohist.io/trigger/event-id` and
  `mohist.io/trigger/rule-id`. Event, rule, and AgentJob are queryable in both
  directions.
- AgentJob determines response completion. AgentSession provides conversation
  and audit evidence through SessionInput, AgentTurn, and transcript.

`deleted` is only the RoutingRule storage tombstone, not a readable or routable
resource. Deleted rules do not appear in rule or Agent-subscription list and
read results and do not participate in matching. Repeating deletion of a known
rule returns the same `deleted` confirmation; an unknown ID returns `404`.
Because name uniqueness applies only to readable states, a non-deleted rule can
reuse the name after deletion.

## System Handler Relationship

The routing table is the user consumer surface. `[Subscription]` handlers are
the system consumer surface. Both consume the same envelope protocol through
the same dispatcher. Agents have no special channel; a response uses normal
commands such as `mo run approve` and `mo issue comment create`.

## Command Surface

Names follow [`cli.md`](cli.md): the resource comes first, and Project scope
uses the active Project or `--project`.

## Non-goals

- An Agent-specific approval channel; Agents use the regular command surface.
- Strict conflict detection; dry-run and visibility replace it.
- Per-rule retry or outbox; reuse dispatcher delivery guarantees and AgentJob
  failure visibility.
- Matching `event.data.*`; promote an attribute under the admission criterion
  in `event-protocol.md`.
- A per-Agent concurrency gate; rules and visibility provide initial control.
- Trigger rate limits or cooldowns. In the short term, response prompts limit a
  supervising Agent's loop risk, such as failure -> rerun -> failure -> another
  trigger, by using a comment count. System rate limiting waits for a concrete
  need.

## Status

Implemented: the Project-scoped ordered routing table with `Position` and
`Continue`; CEL-subset matching with write-time compilation;
`{{event.*}}` rendering; envelope-only self-response protection; the
`mo routing rule` surface and `mo routing test` dry-run; `mo event tail --match`;
and `mo agent subscription`, API, and Web views over the same RoutingRule
facts.

Implementation gap: the durable launch-pipeline key is still
`(projectId, eventId, ruleId)`, so event-and-Agent coalescing applies only
within one dispatch.
