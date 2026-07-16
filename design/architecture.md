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

## 聚合与事务

聚合是强一致性边界，也是数据库事务边界。

- 一个事务只能保存一个聚合的状态，以及由这次状态变化产生的该聚合领域事件。
- 不允许用同一个事务修改两个聚合，也不允许用 join table、repository 或 handler 绕过
  聚合边界完成跨聚合写入。
- 同一限界上下文内的聚合可以相互引用、查询和发送命令；是否允许依赖不由事务边界
  决定。每条同步调用链必须有明确方向，调用过程中不得同步回调形成环。
- 跨聚合流程由「本聚合提交状态与事件 → durable handler → 目标聚合幂等命令」推进。
  任一步失败都靠事件重投或命令重试继续，不回滚已经提交的另一个聚合。
- 一个业务事实只有一个写入权威。其他聚合需要该事实时，只保存完成自身决策所需的
  最小上下文或读模型；这些副本是最终一致的，不参与原事实的校验和写入。
- 跨聚合查询可以用于选择候选或组装命令，但目标聚合必须再次校验自身不变量。查询
  结果过期只能导致拒绝、重试或重新选择，不能破坏目标聚合状态。

因此「状态与事件同事务」只指同一聚合的状态和自己的事件，不意味着一次业务操作里
涉及的全部聚合共享事务。

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
