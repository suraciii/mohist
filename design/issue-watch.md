---
status: converged
---

# Issue Watch

An Issue-level Agent watch declaration makes an Agent respond when the Issue
reaches an Approval point or terminal failure. It is the Issue autopilot switch.
The routing table owns Project-wide and arbitrary expressions; see
[`event-routing.md`](event-routing.md). Watch owns the daily switch for one
Issue. Responses follow [`event-response.md`](event-response.md).

## Model

```text literal
WatchEntry
  ProjectId, IssueNumber, AgentId
  State: watching | muted
```

- Agent context owns and persists WatchEntry. The Issue aggregate does not own
  it. Watching and muted sections in Issue details are read projections.
- `watching` starts that Agent for an Approval request or terminal run failure
  on the Issue.
- `muted` suppresses every launch of that Agent for the Issue, including a
  routing-rule match.
- The fixed event set is `stage.approval-requested` and `run.failed`. These two
  moments define autopilot and are not configurable. Use routing rules for other
  responses.
- Watch launch uses a built-in response prompt containing event facts and an
  instruction to act according to Agent Instructions. It has no per-rule
  ResponsePrompt. Agent Instructions own the discipline.

## Semantics

### Command Surface

- `mo issue watch add <issue> --agent <name>` creates a watching declaration
  when none exists, changes a muted declaration to watching, and idempotently
  reports current state when the declaration is already watching.
- `mo issue watch remove <issue> --agent <name>` deletes a watching
  declaration, creates a muted declaration when none exists to withdraw
  Project-wide coverage for this Issue, and idempotently reports current state
  when the declaration is already muted.
- `mo issue watch list <issue>` lists the watching and muted declarations for
  the Issue; `mo issue view` shows both groups.

`watch add` and `watch remove` require an existing active Agent and reject an
archived Agent.

### Launch

When an event with an `issue` attribute arrives, dispatch examines WatchEntry in
addition to the routing table:

```text literal
for each Agent matched by a rule:       # Existing routing path
  if (Issue, Agent) is muted:
    treat as no match and write a structured log

if event type is approval-requested or run.failed:
  for each watching (Issue, Agent) declaration:
    launch(Agent, prompt = builtin template, context = Issue)
```

- The normalized idempotency key is
  `hash(projectId, eventId, agentId)`. One Agent starts once for one event even
  when both a rule and watch match.
- Muting suppresses launch before any trigger. On one Issue, muted takes
  precedence over every rule and watch. Watching and muted cannot coexist
  because WatchEntry has one state.
- Workspace resolution, trigger labels, and failed AgentJob creation after
  preflight failure match routing launch behavior. The trigger label records
  watch as the origin.

### Routing Table Boundary

- Use a routing rule for Project supervision, arbitrary event types, or
  arbitrary expressions. Rules have order and Continue.
- Use watch as a daily switch for one Issue. It does not enter the routing table
  and has no ordering semantics.
- When an `@` mention requests continuing watch, the Agent runs
  `mo issue watch add` instead of writing a routing rule. See
  [`agent-mentions.md`](agent-mentions.md).

## Status

Implemented: WatchEntry persistence in Agent context; `mo issue watch
add/remove/list`; Issue read-model projection; dispatch-side muted suppression
and watching launch with a built-in prompt and a synthetic `watch:` rule ID.

The durable launch key still includes a synthetic `watch:` rule identity.
Event-Agent deduplication therefore applies only within one dispatch instead of
using one stable Agent identity across dispatches.
