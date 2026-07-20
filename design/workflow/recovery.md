# Task Recovery

任务执行后，runner executor 用 `when` 表达式匹配 Action output 字段，构造恢复任务并经
`addTasks` 返回，引擎机械插入。恢复是任务完成方式的一部分，不是失败后的补救：显式匹配与
任务成败无关，成功任务的输出命中 `when: promise=FAIL` 同样触发恢复。省略 `when` 的默认
handler 则只处理失败结果，包括 executor 在 Action 完成后形成的最终失败。

语法与作者可见语义（budget、first-match、`retrySelf`、人工 retry 开新一轮）见
[docs 的 recovery 节](../../docs/workflow-definition.md#recovery--失败恢复)。本篇定义
执行机制。

## 分工

- 引擎保持通用：只认识 stage / task / check / completed / failed，从不认识「恢复」。
  `recovery` 对引擎是透传的任务属性。
- recovery 配置从 YAML 到 runner 全程只读。剩余预算是配置之外的每 attempt 执行状态
  （`recoveryRemaining`），不是被改写的配置副本。
- 匹配发生在 runner executor：显式 `when` 匹配 Action output 的任意字段；最后一个省略
  `when` 的 handler 只匹配失败结果；Action 对恢复零感知。
- 恢复任务是真实的 Workflow 任务，出现在 graph、时间线与状态中。
- 触发恢复的任务以 completed 结束：它产出了后续工作。
- 恢复任务模板中的 `${{ failure.* }}` 由 runner 构造时就地展开，见
  [`task-dispatch.md`](task-dispatch.md)。

| 层 | 职责 |
|---|---|
| workflow YAML | 声明 `budget` 与 `handlers`（可选 `when`、`tasks`、`retrySelf`） |
| Action | 返回普通 output，零恢复感知 |
| runner executor | 先匹配显式 `when`，再为失败结果匹配默认 handler；显式 `null` 取满额 `budget`，数值 clamp 到声明范围；构造 `addTasks` |
| 引擎 | 机械插入 `addTasks`；把 `recoveryRemaining` 当不透明的每 attempt 状态透传；人工 retry 只从定义性字段重建 |

## 剩余预算（recoveryRemaining）

`recovery` 配置全程只读。「本轮还剩几次」是执行状态，放在配置之外的独立字段
`recoveryRemaining` 里随任务流转：

```
YAML budget:2 ──► TaskRun ──────────► WorkItem / dispatch ──► runner tryRecovery
                  Recovery(只读)                              null -> budget
                  RecoveryRemaining                                 │ 匹配且 remaining > 0
                        ▲                                           │
                        └── RuntimeTaskInput ◄────── addTasks ◄─────┘
                            (retrySelf: recovery 原样, recoveryRemaining = remaining - 1)
```

- 读写权威只有一个：runner `tryRecovery`。引擎对该字段只透传，从不读值。
- 显式 `null` 表示新一轮，取满额 `budget`；字段缺失视为畸形传输，按普通结果处理，
  不得重新打开预算。
- 引擎侧 `recoveryRemaining` 不属于任务定义：摄入 addTask 时作为旁路状态传给
  `TaskRun`（同 `causedByFeedbackId` 先例），不进 `TaskDefinition`。

## 人工 retry 开启新一轮

不变量：**budget 界定一轮连续自动恢复；人工 retry 开启新一轮，拿满额预算。**

机制：人工 retry（`RetryFailedTask`）经 `TaskRun.ToDefinition()` 只从定义性字段重建新
attempt。哪些字段构成「定义」在 `ToDefinition()` 一处收敛，执行状态
（`recoveryRemaining`）从结构上进不了重建路径。新 attempt 到 runner 时
`recoveryRemaining` 显式为 `null`，runner 按声明的 `budget` 初始化本轮，自动恢复循环
重新可用。失败的 attempt 及其已消耗的数值状态保持不变。

## Stage 重跑不复用 TaskRun 身份

`TaskRun` 的执行身份由 definition id、Stage attempt 与 task attempt 组成。首个 Stage
attempt 保持现有的 `{definitionId}.{taskAttempt}` 格式；从第二次起使用
`{definitionId}.s{stageAttempt}.{taskAttempt}`。例如首次 build task 是 `T-001.1`；同一
Stage 内人工 retry 是 `T-001.2`；重跑 build 后的首次 task 是 `T-001.s2.1`。

`rerun-from-stage` 丢弃旧 Stage 的可见 task 历史，但不能让 Stage attempt 倒退或让新的
TaskRun 复用旧 identity。这样默认以 Work ID 命名的 Workflow AgentSession 总是一个新的
逻辑 Session，不会继承已失效 attempt 的 physical binding 或工作目录。显式 `session`
名称仍由 workflow 定义负责其复用语义。

## Runner executor 流程

```
result = action.execute()
output = parseJSON(result.output)
if recoveryRemaining is absent:
    return ordinary result
remaining = recoveryRemaining is null
    ? recovery.budget
    : clamp(recoveryRemaining, 0, recovery.budget)
handler = recovery.handlers.find(h => h.when && matchesWhen(h.when, output))
    ?? (result.failed ? recovery.handlers.find(h => h.when is absent) : null)

if handler && remaining > 0:
    addTasks = handler.tasks with their own full recoveryRemaining
        + (retrySelf ? retryTask(recovery unchanged, recoveryRemaining = remaining - 1) : [])
    return completed + addTasks

if result.success:
    return completed
return failed
```

默认 handler 最多一个且必须最后，因而不会遮蔽显式匹配。它在 executor 已形成失败结果后
匹配，所以工作区、分支等 Action 之后的最终检查失败也能走同一条恢复路径。负值 clamp 到
0，超过声明的值 clamp 到声明值。命中 handler 消耗一次额度；未命中不消耗。声明永不改写。

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

- `completed` + `addTasks`：引擎把任务插入当前 stage。
- `completed`（无 `addTasks`）：正常完成。
- `failed`：workflow 失败。

## 引擎侧行为

```
result.completed
  → mark task completed
  → addTasks non-empty? → AddRuntimeTaskAttempts
  → Advance

result.failed
  → mark task failed → stage failed → workflow failed
```
