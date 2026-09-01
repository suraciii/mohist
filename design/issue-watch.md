# Issue Watch

An Issue watch declaration makes one Agent respond when an Issue reaches an
Approval Point or terminal run failure. It is the per-Issue autopilot switch.
The routing table owns Project-wide and expression-based coverage. See
[`event-routing.md`](event-routing.md). Responses follow
[`event-response.md`](event-response.md).

## Design Drivers

- Watch owns the daily switch for one Issue and one Agent.
- The event set is fixed. Other event responses use routing rules.
- Muting must suppress every matching launch before dispatch.
- Watch and routing must share one idempotent launch identity.

## Model

```text literal
WatchEntry
  ProjectId, IssueNumber, AgentId
  State: watching | muted
```

Agent context owns and persists WatchEntry. The Issue aggregate does not own it.
Issue details show watching and muted declarations as read projections.

- `watching` launches that Agent for `stage.approval-requested` and `run.failed`.
- `muted` suppresses every launch of that Agent for the Issue, including a
  routing-rule match.
- These two event types are fixed. Use routing rules for other responses.
- Watch launch uses a builtin response prompt with event facts and an
  instruction to follow Agent Instructions. Watch has no per-rule
  `ResponsePrompt`.

## Semantics

### Command Surface

- `mo issue watch add <issue> --agent <name>` creates a watching declaration,
  changes muted to watching, or returns the current watching state.
- `mo issue watch remove <issue> --agent <name>` deletes a watching declaration,
  creates muted when no declaration exists, or returns the current muted state.
  The muted record withdraws Project-wide coverage for this Issue.
- `mo issue watch list <issue>` lists watching and muted declarations. `mo issue
  view` shows both groups.

Both mutation commands require an active Agent. An archived Agent is rejected.

### Launch

When an event has an `issue` attribute, dispatch checks WatchEntry as well as
the routing table:

```text literal
for each Agent matched by a rule:       # Existing routing path
  if (Issue, Agent) is muted:
    treat as no match and write a structured log

if event type is approval-requested or run.failed:
  for each watching (Issue, Agent) declaration:
    launch(Agent, prompt = builtin template, context = Issue)
```

The normalized idempotency key is `hash(projectId, eventId, agentId)`. One Agent
starts once for one event even when both a rule and a watch match.

Muting wins over every rule and watch on the Issue. Watching and muted cannot
coexist because one WatchEntry has one state. Workspace resolution, trigger
labels, and failed AgentJob creation after preflight failure use routing launch
semantics. The trigger label records watch as the origin.

### Routing Table Boundary

- Use a routing rule for Project supervision, arbitrary event types, or
  arbitrary expressions. Rules have order and Continue behavior.
- Use watch for one Issue's daily switch. Watch is not in the routing table and
  has no ordering semantics.
- When an `@` mention requests continuing watch, the Agent runs `mo issue watch
  add` instead of creating a routing rule. See
  [`agent-mentions.md`](agent-mentions.md).

## Status

WatchEntry persistence, `mo issue watch add/remove/list`, Issue projections,
muted suppression, and watching launches with a builtin prompt are implemented.
The launch uses a synthetic `watch:` rule ID. Durable Event-Agent
deduplication therefore remains scoped to one dispatch instead of one stable
Agent identity across dispatches.
