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
- `retrySelf`: runner constructs self-retry task carrying the unchanged `recovery` declaration and `recoveryRemaining = remaining - 1`, then appends it to tasks.
- `recoveryRemaining`: per-attempt execution state. Fresh attempts carry explicit `null`; numeric values continue the current recovery round.

## 剩余预算（recoveryRemaining）

`recovery` 配置从 YAML 到 runner 全程只读。「本轮还剩几次」是执行状态，放在配置之外的独立字段 `recoveryRemaining` 里随任务流转：

```
YAML budget:2 ──► TaskRun ──────────► WorkItem / dispatch ──► runner tryRecovery
                  Recovery(只读)                              null -> budget
                  RecoveryRemaining                                 │ 匹配且 remaining > 0
                        ▲                                           │
                        └── RuntimeTaskInput ◄────── addTasks ◄─────┘
                            (retrySelf: recovery 原样, recoveryRemaining = remaining - 1)
```

- 读写权威只有一个：runner `tryRecovery`。引擎对该字段只透传，从不读值。显式 `null` 表示新一轮；字段缺失视为畸形传输并保持普通结果，不得重新打开预算。
- 引擎侧 `recoveryRemaining` 不属于任务定义：摄入 addTask 时作为旁路状态传给 `TaskRun`（同 `causedByFeedbackId` 先例），不进 `TaskDefinition`。

## 人工 retry 开启新一轮

不变量：**budget 界定一轮连续自动恢复；人工 retry 开启新一轮，拿满额预算。**

机制：人工 retry（`RetryFailedTask`）经 `TaskRun.ToDefinition()` 只从定义性字段重建新 attempt。哪些字段构成「定义」在 `ToDefinition()` 一处收敛，执行状态（`recoveryRemaining`）从结构上进不了重建路径。新 attempt 到 runner 时 `recoveryRemaining` 显式为 `null`，runner 按声明的 `budget` 初始化本轮，自动恢复循环重新可用。

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
if recoveryRemaining is absent:
    return ordinary result
remaining = recoveryRemaining is null
    ? recovery.budget
    : clamp(recoveryRemaining, 0, recovery.budget)
handler = recovery.handlers.find(h => matchesWhen(h.when, output))

if handler && remaining > 0:
    addTasks = handler.tasks with their own full recoveryRemaining
        + (retrySelf ? retryTask(recovery unchanged, recoveryRemaining = remaining - 1) : [])
    return completed + addTasks

if result.success:
    return completed
return failed
```

Negative remaining state clamps to zero, and a value above the declaration
clamps to the declaration. A matching handler consumes one allowance; an
unmatched output consumes none. The declaration is never rewritten.

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

Manual retry creates a new attempt from the immutable task definition and starts a new round with explicit `recoveryRemaining: null`. The failed attempt and its consumed numeric state remain unchanged.

## Server behavior

```
result.completed
  → mark task completed
  → addTasks non-empty? → AddRuntimeTaskAttempts
  → Advance

result.failed
  → mark task failed → stage failed → workflow failed
```

## What is removed

Replaces `onFailure`: server-side matching, engine-managed budget (`RemainingRecoveries`), `TaskFailureAction`, `TaskFailureCase`, failed-task state for recovered tasks — all gone. Recovery logic and budget live entirely in the runner.
