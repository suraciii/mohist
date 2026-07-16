# Task Recovery

After task execution, runner executor matches `when` expressions against action output fields, constructs recovery tasks, returns them via `addTasks`. Engine inserts mechanically.

Recovery is part of task completion, not post-failure remediation. Engine never understands recovery semantics.

Matching is independent of task success/failure. A successful task whose output matches `when: promise=FAIL` still triggers recovery.

## Design

- Workflow engine: generic. Only knows stage/task/check/completed/failed. Never knows "recovery."
- Recovery is a task top-level property: `recovery.budget` + `recovery.handlers`. Engine passes through.
- Recovery config is immutable end-to-end. Remaining budget is separate per-attempt state (`recoveryRemaining`), never a mutated config copy.
- Matching in runner executor: `when` expression matches any field in action output. Action knows nothing about recovery.
- Recovery tasks are real workflow tasks: visible in graph/timeline/status.
- Recovery = completed: current task produced recovery tasks as follow-up work.

## Structure

```yaml
- id: merge-pr
  uses: mohist/merge-github-pr
  with:
    prNumber: ${{ vars.github.pr.number }}
  recovery:
    budget: 2
    handlers:
      - when: errorCode=base-moved
        tasks:
          - id: recover:rebase
            uses: mohist/rebase
            with: { baseBranch: ${{ repository.baseBranch }} }
          - id: recover:push
            uses: mohist/push
            with: { source: ${{ workspace.branch }}, target: ${{ workspace.branch }} }
        retrySelf: true
```

- `budget`: max consecutive automatic recoveries. Default 0. Never mutated.
- `handlers`: ordered, first-match.
- `when`: `field=value` match on any action output field.
- `tasks`: user-defined recovery tasks.
- `retrySelf`: runner constructs self-retry task carrying `recovery` unchanged plus `recoveryRemaining = remaining - 1`, appends to tasks.

## 剩余预算（recoveryRemaining）

`recovery` 配置从 YAML 到 runner 全程只读。「本轮还剩几次」是执行状态，放在配置之外的独立字段 `recoveryRemaining` 里随任务流转：

```
YAML budget:2 ──► TaskRun ──────────► WorkItem / dispatch ──► runner tryRecovery
                  Recovery(只读)                              remaining = recoveryRemaining ?? budget
                  RecoveryRemaining                                 │ 匹配且 remaining > 0
                        ▲                                           │
                        └── RuntimeTaskInput ◄────── addTasks ◄─────┘
                            (retrySelf: recovery 原样, recoveryRemaining = remaining - 1)
```

- 读写权威只有一个：runner `tryRecovery`。引擎对该字段只透传，从不读值。
- 引擎侧 `recoveryRemaining` 不属于任务定义：摄入 addTask 时作为旁路状态传给 `TaskRun`（同 `causedByFeedbackId` 先例），不进 `TaskDefinition`。

## 人工 retry 开启新一轮

不变量：**budget 界定一轮连续自动恢复；人工 retry 开启新一轮，拿满额预算。**

机制：人工 retry（`RetryFailedTask`）经 `TaskRun.ToDefinition()` 只从定义性字段重建新 attempt。哪些字段构成「定义」在 `ToDefinition()` 一处收敛，执行状态（`recoveryRemaining`）从结构上进不了重建路径。新 attempt 到 runner 时 `recoveryRemaining` 为空，回退到 `budget`，自动恢复循环重新可用。

## Division of labor

| Layer | Does |
|---|---|
| workflow YAML | declares budget, handlers (when, tasks, retrySelf) |
| action | returns normal output. Zero recovery awareness |
| runner executor | matches `when`, computes `remaining = recoveryRemaining ?? budget`, constructs `addTasks` |
| engine | mechanically inserts `addTasks`; passes `recoveryRemaining` through as opaque per-attempt state; manual retry rebuilds from definition fields only |

## Runner executor flow

```
result = action.execute()
output = parseJSON(result.output)
handler = recovery.handlers.find(h => matchesWhen(h.when, output))
remaining = work.recoveryRemaining ?? recovery.budget

if handler && remaining > 0:
    addTasks = handler.tasks + (retrySelf ? retryTask(recovery unchanged, recoveryRemaining = remaining - 1) : [])
    return completed + addTasks

if result.success:
    return completed
return failed
```

## WorkResult

```json
{
  "status": "completed",
  "addTasks": [
    { "id": "recover:rebase", "uses": "mohist/rebase", "with": {...} },
    { "id": "merge-pr", "uses": "mohist/merge-github-pr", "with": {...}, "recovery": {"budget": 2, ...}, "recoveryRemaining": 1 }
  ]
}
```

- `completed` + `addTasks`: engine inserts tasks into current stage.
- `completed` (no `addTasks`): normal completion.
- `failed`: workflow failed.

## Server behavior

```
result.completed
  → mark task completed
  → addTasks non-empty? → AddRuntimeTasks
  → Advance

result.failed
  → mark task failed → stage failed → workflow failed
```

## What is removed

Replaces `onFailure`: server-side matching, engine-managed budget (`RemainingRecoveries`), `TaskFailureAction`, `TaskFailureCase`, failed-task state for recovered tasks — all gone. Recovery logic and budget live entirely in the runner.

## 差距脚注

正文是 spec，以下是现状差距，收敛后删：

- `recoveryRemaining` 未实装：runner 现在把递减后的 budget 写回 recovery 配置副本（`decrementRecoveryBudget`），配置被当状态改写。
- 人工 retry（`RetryFailedTask`）克隆失败 TaskRun 快照，连同耗尽的 budget 一起继承——预算耗尽后 retry 无法重新触发自动恢复循环。
- `TaskRun.ToDefinition()` 未建：重建仍靠手抄字段列表。
