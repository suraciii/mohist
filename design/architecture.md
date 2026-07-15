# Architecture

## Boundary

```
User / Web / CLI
       │
       v
Control Plane        owns state, makes decisions
       │
       v
Execution Plane      runs commands, reports facts
       │
       v
User Project
```

## What goes where

| Concern | Belongs in | Not in |
|---|---|---|
| user commands | CLI | Server |
| observe & act | Web UI + API | Runner |
| state authority | Server | Runner |
| decide workflow | Server | Runner |
| register/presence/capacity | Server | Web / CLI |
| workspace prep/clean | Runner | Server |
| run shell/process/agent | Runner | Server |
| git side effects | Runner | Server |
| OpenSpec side effects | Runner | Server |
| explore/chat | external agent skill | Mohist runtime |
| skill install | CLI | Server |
| product design | docs/ | design/ |
| domain model | code | design/ |
| architecture rules | design/architecture.md | OpenSpec |
| builtin workflow content | *.workflow.yaml | design/ |

## Facts and decisions

```
Task executes.
Check verifies.
Runner reports.
WorkflowRun decides.
```

Runner produces facts. Never interprets them.
Workflow interprets facts. Never produces them.

## Report pipeline

```
Side effect
  │
  v
Report              ← fact, not command
  │
  v
Ownership check     ← reject without proof
  │
  v
Decision            ← interpret in workflow context
  │
  v
State change        ← advance or wait
```

Runner may say: completed / failed / verification passed / output produced.
Runner may not say: advance state / mark done / bypass approval / allow retry.

Every in-flight work has an owner. Stale reports get rejected, never merged.

## Events: two channels

| Channel | SLA | Purpose |
|---|---|---|
| Domain reaction | durable at-least-once | advance cross-aggregate state |
| UI push | best-effort | update screen |

UI disconnect → self-reconcile. Never depend on UI for workflow progress.

Events append in same transaction as state save. Dispatcher is the sole notifier.

## Persistence

- Product state: persist.
- Workflow state: persist.
- Runner workspace: rebuildable.
- Artifact: persist (audit trail).
- Authority grains: no `[Reentrant]`.

## Explore is external

Mohist does not own AI chat. Explore belongs to external agent skills (mohist-explore, etc.).

External skills read projects, call `mo` CLI, write files. Never touch Mohist DB.
Runner may adapt OpenCode or another runtime for Workflow TaskRun and AgentJob work.
Agent/Session ownership invariants: [`agent-execution.md`](agent-execution.md).

## Constraints

- CLI never merges into Server.
- All shell/agent/git/OpenSpec execution goes to Runner.
- Single daemon today. Actor model for state, not distribution.
- Durable dispatcher notifies. Never executes tasks or calls runner.
- OpenSpec is not architecture authority.
