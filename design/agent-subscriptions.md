---
status: wip
---

# Agent Subscriptions

Agent listens to CloudEvents and auto-responds by prompt, instead of manual launch.
Here, Agent always means the project-scoped Mohist Agent definition, never an Inline Agent
invocation or the OpenCode runtime `agent` option.
Lifecycle ownership and Session relationships are defined in
[`agent-execution.md`](agent-execution.md).

## Boundary

Subscription belongs to Agent context. Consumes CloudEvent PL (infra layer). Never `using Workflow.Domain` or `Issue.Domain`.

## How context flows

Handler reads only the CloudEvent envelope (`source`, `data.stage`, `type`). Renders into response prompt.

Agent starts → `mo workflow get <runId> --json` → gets `IssueRef.Number + Title` → `mo issue show <number>`.

No handler reverse-query. No identity stamping on events. Zero cross-domain imports.

Pre-req: `mo workflow get` returns issue ref — delivered by issue #381.

## Subscription model

```
AgentSubscription (1 Agent : N subscriptions)
  Id, ProjectId, AgentId, Name
  Filter                    expression on CloudEvent attributes
  ResponsePrompt            template with {{workflow_run_id}} {{stage}} {{event_type}}
  CoordinationMode          fanout | exclusive
  Priority?                 arbitration; null = default
  Status                    active | archived
```

### Filter

Matches CloudEvent envelope attributes. Extends existing `[Subscription]` type syntax to multi-attribute:

| Capability | Syntax |
|---|---|
| exact type | `com.mohist.workflow.stage.approval-requested` |
| prefix wildcard | `com.mohist.workflow.*` |
| catch-all | `*` |
| multi-value | `a\|b\|c` |
| scope by source | constrain `source` attribute to specific issue's run |

No business-domain queries in matching.

### Coordination

- fanout: all matching active subscriptions fire.
- exclusive: group by Agent, pick highest priority Agent, then highest priority subscription within that Agent.

Same priority = deterministic pick (subscription id). No error, no block.

### Visibility over validation

Every triggered session gets two metadata labels:
- `mohist.io/trigger/event-id` — which CloudEvent
- `mohist.io/trigger/subscription-id` — which subscription

Traceable in both directions. User owns config correctness; system owns observability.
Each trigger creates an AgentJob plus an AgentSession. AgentJob is authoritative for response
completion; the labeled AgentSession supplies conversation and audit evidence.

## Components

1. Subscription aggregate + Store
2. IAgentLauncher service (extract from `AgentSessionLaunchRoutes`)
3. Dispatch handler: `[Subscription]` → match filter → coordination → render prompt → IAgentLauncher
4. Template rendering: simple string replace for 3 variables

Handler has zero business domain `using`.

## Not doing

- Agent-specific approve channel. Agents use `mo workflow approve` / `mo issue approve`.
- Strict conflict detection. Visibility replaces it.
- Per-subscription retry/outbox. Reuse event bus delivery + AgentJob failure visibility and
  AgentSession audit evidence.
- Workflow profile `requiresApproval`. Orthogonal.
- Per-agent concurrency gate. Control through subscription and visibility first.
