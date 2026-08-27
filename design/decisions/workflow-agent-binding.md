# Workflow Agent Binding DSL

## Background

The unified execution model ([`../agent-execution.md`](../agent-execution.md),
[`../domain-analysis.md`](../domain-analysis.md)) makes AgentJob the sole
top-level execution owner and requires every executable Workflow task to launch
a real Mohist Agent. The Profile DSL therefore needs one way to bind a task to
an Agent.

A past decision (2026-08-14) requires `uses` to name a concrete Action
implementation and rejects a runtime-neutral generic Agent Action. The unified
model removes the tension behind that decision: Runtime selection moves into
the Agent definition, so the Agent-binding Action has no Runtime dimension at
all.

## Decision

### 1. Agent Work Is an Ordinary Action

An executable Workflow task uses the existing `mohist/agent` Action. The task
structure is unchanged — every task is `uses` plus `with`:

```yaml
- id: plan
  uses: mohist/agent
  with:
    name: mohist/planner
    session: plan
    prompt: ${{ prompts.plan }}
```

Reasons:

- Zero new syntax. The parser, validator, and examples already exist.
- `uses` still names a concrete implementation: `mohist/agent` is the Agent
  launcher, not a dynamic dispatcher. Mechanical Actions (`mohist/push`,
  `core/script`, …) keep `uses` unchanged.
- The layering matches Agent Connection: the Agent definition holds capability
  (Instructions, Skills, Runtime, Model); the Profile holds the invocation
  (name, input, session).

### 2. Syntax Is Unchanged; Semantics Change

`mohist/agent` keeps its current syntax and gains new semantics: it enters the
canonical AgentJob launch boundary and creates a real AgentJob and AgentSession
instead of snapshotting an Agent definition into a TaskRun. A missing,
archived, or not-ready Agent fails launch explicitly.

`with` fields:

- `name`: required. Resolves to a Project Agent. Built-in Agents under the
  `mohist/` prefix act as the loader fallback, so built-in Profiles run without
  manual Agent creation; a Project Agent of the same name overrides the
  built-in definition.
- `session`: optional. Unchanged semantics: the same name within one
  WorkflowRun continues the logical Session only when the Agent and Workspace
  identities also match; without a name, each AgentJob gets a distinct Session.
- `prompt`: required. The task input.
- `timeout`: optional. Per-execution deadline, unchanged from the current
  implementation.

`with.options` is removed. Runtime, Model, Reasoning Effort, and Variant belong
to the Agent definition, and the `agentRuntime` Profile projection is deleted
with them.
### 3. Runtime-Specific Actions Leave the Profile

`mohist/opencode` and `mohist/pi` are removed from the Profile `uses` surface.
They become the Agent-to-Runner execution contract selected by the Agent
definition. Recovery-handler Agent tasks use the same `mohist/agent` syntax as
ordinary tasks; there is no special case.

## Consequences

- `docs/actions/agent.md` is the syntax and semantics reference for Agent task
  binding.
- The legacy Workflow dispatch paths (`mohist/opencode`, `mohist/pi`, and the
  snapshot-only `mohist/agent`) are deleted by the implementation migration,
  not kept as compatibility modes.
- Legacy-marked Workflow documents are rewritten after the migration lands.
