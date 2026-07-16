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

### 持久化应用协调者（durable application process manager）

当一条跨聚合命令需要在多个参与者之间串行化、且其结果在重试、激活丢失或网络中断下
仍要可恢复时，可以引入一个持久的应用层 process manager（即协调者 grain）。它**不**
是新的业务权威，而是对「本聚合提交 → durable handler → 目标聚合幂等命令」这一既定
模式的窄化特例。它存在是为了把一组容易竞态的命令一次性收敛，并让每一步骤都具备
重投安全。

约束（缺一不可）：

- **只持久化不确定的命令投递状态**。协调者 grain 内部只保存当前正在执行的命令 fence
  （如 `Pending { commandId, kind, payload, expectedRevision }`）。命令得到明确 applied
  或 rejected 结果后 fence 立即清空，不缓存任何业务结果。
- **每条命令最多写入一个参与者聚合**。协调者一次同步调用链只进入一个参与者事务；它
  不得跨聚合写、不得用 join table 或 repository 绕过聚合边界。若一次业务操作要影响
  两个聚合，由各聚合自己的事务 + 持久事件接力，协调者只负责串行化与幂等。
- **位于参与者接口的下游**。协调者只单向调用参与者的窄接口命令；参与者**不得**在
  该同步调用栈中回调协调者，也不得持有协调者引用。`Issue`、`Project` 等参与者聚合
  不感知协调者存在，事件路由、handler、其它上下文命令继续直接走它们自己的接口。
- **不存储重复的业务事实**。协调者持久层不含 Issue / Project / 仓库 / WorkflowRun 的
  业务状态——这些事实在对应聚合内才是唯一权威。协调者最多持有 `commandId`、命令种类、
  canonical 化的命令参数快照与 expected revision 这些技术性 fence 字段。
- **不得引入同步回调环**。协调者调用参与者 → 参与者提交 → 持久事件 → 协调者从
  handler 重新进入；这一步必须经过 durable dispatch，不得由参与者在命令内部再
  同步调回协调者。

适用范围（当前唯一）：`IssueRepositoryCoordinatorGrain` 串行化 Project 内的
Issue 创建 / 仓库重新指派 / cancelled Issue reopen / 仓库删除这一组会建立或破坏
非终态绑定的命令。它对参与者使用窄接口（`IIssueParticipant` / `IProjectParticipant`），
并通过 `ArchTest` 防止生产代码绕过协调者。

不适用范围：参与者内部的不变量校验、跨聚合的最终一致性推进、UI 推送、session 与
runtime 绑定——这些不走协调者。

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
