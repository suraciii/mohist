---
purpose: "Task recovery：runner 侧 action 驱动的恢复机制，取代 engine 侧 onFailure 编排。"
style: ["极简，只给目标态。"]
---

# Task Recovery

Task 执行遇到可恢复失败时，runner executor 根据 action output 的 `errorCode` 匹配 `with.recovery.handlers`，构造 recovery tasks，server 机械插入。

Recovery 是 task 完成的一部分，不是失败后的补救。Workflow engine 不理解 recovery 语义。

> 相关：action input/output 契约见 [`actions.md`](actions.md)；dispatch 见 [`task-dispatch.md`](task-dispatch.md)；report 链路见 [`scheduling.md`](scheduling.md)。

## 设计原则

- **Workflow engine 是通用执行引擎**：只理解 stage / task / check / completed / failed。不认识 `recovery`。
- **Recovery 相关的一切都是 `with.recovery`**：budget 和 handlers 都在 `with.recovery` 下，engine 透传，不解释。
- **Recovery 匹配在 runner 侧**：runner 用 action output 的 `errorCode` 匹配 `handlers[].when`。action 只管正常返回 output，零 recovery 知识。
- **Recovery tasks 的定义可用户自定义**：用户在 `with.recovery.handlers` 里声明每个 handler 对应的 recovery tasks、with、retrySelf。
- **Recovery tasks 是真实 workflow tasks**：在 graph / timeline / status 中可见、可调度，与普通 task 无区别。
- **Recovery 视作 completed**：当前 task 产出了 recovery tasks 作为后续工作，它的使命完成了。

## with.recovery 结构

所有 recovery 配置收拢在 `with.recovery` 下。

```yaml
- id: merge-pr
  uses: mohist/merge-github-pr
  with:
    prNumber: ${{ vars.github.pr.number }}
    method: squash
    recovery:
      budget: 2                                # 允许的自动恢复次数
      handlers:                                # 有序，first-match
        - when: base-moved                     # 匹配 output.errorCode
          tasks:
            - id: recover:rebase
              uses: mohist/rebase
              with:
                baseBranch: ${{ repository.baseBranch }}
                remote: origin
            - id: recover:push
              uses: mohist/push
              with:
                source: ${{ workspace.branch }}
                target: ${{ workspace.branch }}
                remote: origin
          retrySelf: true
        - when: pr-checks-failed
          tasks:
            - id: recover:fix-pr-checks
              uses: mohist/acp-agent
              with:
                prompt: ${{ prompts.fix-pr-checks }}
                agent: ${{ vars.agent }}
            - id: recover:push
              uses: mohist/push
              with:
                source: ${{ workspace.branch }}
                target: ${{ workspace.branch }}
                remote: origin
          retrySelf: true
```

- `recovery.budget`：允许的自动恢复次数。不声明时默认 0。
- `recovery.handlers`：有序列表，first-match。
- `handlers[].when`：字符串，匹配 action output 的 `errorCode` 字段。
- `handlers[].tasks`：用户自定义 recovery tasks（uses / with 可自定义）。
- `handlers[].retrySelf`：runner 构造 budget-1 的 self-retry task 追加到 tasks 末尾。
- engine 展开整个 `with`（含嵌套 `recovery`）后 dispatch 给 runner。
- action 只读自己需要的字段（如 `prNumber`、`method`），忽略 `recovery`。
- 手动 retry 从 task definition 取 `with`（含初始 budget），自然重置。

## 分工

| 层 | 职责 |
|---|---|
| workflow YAML (`with.recovery`) | 定义 budget、handlers（when 匹配条件、tasks、retrySelf） |
| action | 正常返回 output（含 `errorCode`），不感知 recovery |
| runner executor | 匹配 `handlers[].when` against `output.errorCode`、检查 budget、构造 recoveryTasks |
| engine | 机械插入 recoveryTasks |

## when 匹配语义

`when` 是字符串，匹配 action output 的 `errorCode` 字段：

```yaml
when: base-moved       # output.errorCode == "base-moved"
```

`errorCode` 是 action output 的标准字段（见 [`actions.md`](actions.md) "Error Code" 章节）。

marker 驱动的失败（如 `failIf: <promise>FAIL</promise>`）同样走此约定：marker 匹配时 runner 在 output 里设 `errorCode`，handler 的 `when` 匹配它。

## WorkResult 扩展

Runner executor 构造好 recoveryTasks 后，通过 WorkResult 返回给 engine：

```json
{
  "status": "completed",
  "message": "Merge failed (base-moved); recovery scheduled",
  "recoveryTasks": [
    {
      "id": "recover:rebase",
      "title": "Rebase after base moved",
      "uses": "mohist/rebase",
      "with": { "baseBranch": "master", "remote": "origin" }
    },
    {
      "id": "recover:push",
      "title": "Push",
      "uses": "mohist/push",
      "with": { "source": "feature", "target": "feature", "remote": "origin" }
    },
    {
      "id": "merge-pr",
      "title": "Merge GitHub PR",
      "uses": "mohist/merge-github-pr",
      "with": { "prNumber": 13, "method": "squash", "recovery": { "budget": 1, "handlers": [ ... ] } }
    }
  ]
}
```

规则：

- `status: completed` + `recoveryTasks`：task 完成，engine 插入这些 tasks 到当前 stage。
- `status: completed`（无 `recoveryTasks`）：普通完成。
- `status: failed`：task 失败，workflow failed。
- retry self 是 `recoveryTasks` 里的一个普通 task，runner executor 构造时 `recovery.budget` 递减。engine 不区分。

## Server 行为

```
result.status == "completed"
  → 标记 task completed
  → result.recoveryTasks 非空？
      → 是：插入为当前 stage 的真实 workflow tasks（AddRuntimeTasks）
      → 否：无操作
  → Advance

result.status == "failed"
  → 标记 task failed → stage failed → workflow failed
```

Engine 不需要理解 `recoveryTasks` 里 task 的 `uses`、`with` 等内容。插入逻辑复用现有 `AddRuntimeTasks`。

## Runner executor 行为

```
result = action.execute()

if result.success:
    return completed

if result has no output.errorCode:
    return failed

handler = recovery.handlers.find(h => h.when === output.errorCode)
if !handler:
    return failed

budget = recovery.budget ?? 0
if budget <= 0:
    return failed

# recoveryTasks 从 handler 构造（engine dispatch 时 with 已展开，直接取值）
recoveryTasks = handler.tasks

if handler.retrySelf:
    recoveryTasks += constructRetrySelf(task, budget - 1)

return completed + recoveryTasks
```

`retrySelf: true` 是 runner 的模板快捷方式：构造一个和原 task 相同 `uses`、相同 `with` 但 `recovery.budget - 1` 的 task。用户不需要手写 retry task。

Recovery budget 的读取、检查、递减全部在 runner 侧完成。

## 与现有 onFailure 的对比

| | onFailure（旧） | recovery.handlers（新） |
|---|---|---|
| 匹配在哪 | server (engine) | runner (executor) |
| 匹配方式 | JSON path `output.errorCode` | flat `output.errorCode == when` |
| recovery task 定义 | `onFailure.cases.tasks` | `with.recovery.handlers[].tasks` |
| budget 管理 | engine 状态 (RemainingRecoveries) | `with.recovery.budget`（数据，runner 递减） |
| engine 理解的语义 | onFailure / cases / when / limit / retry | 只看 `recoveryTasks` 有没有 |
| task 失败状态 | failed（即使后来 recovered） | completed（recovery 是完成的一部分） |
| action 参与 | 无（只返回 output） | 无（只返回 output） |

## 迁移

需要删除的 server 侧概念：

- `TaskFailureAction` / `TaskFailureCase`（domain definition）
- workflow YAML 的 `onFailure` 块
- `ResolveTaskFailureRecovery` / `BuildRecoverySequence` / `RecoverTaskFailure`（WorkflowGrain / WorkflowRun）
- `TaskRun.RemainingRecoveries` / `StageRun.RecoveryBudget`

需要新增的：

- `WorkResult` 增加 `recoveryTasks` 字段（`List<RuntimeTaskInput>?`）
- `ReportResultAsync` 在 task completed 时检查 `recoveryTasks`，非空则 `AddRuntimeTasks`
- runner executor 实现 recovery 逻辑（匹配 `handlers[].when`、检查 budget、构造 recoveryTasks）
- marker `failIf` 匹配时 runner 在 output 里设 `errorCode`

## 内置 workflow 变化

`mohist/github-pr` 的 merge-pr task：

```yaml
- id: merge-pr
  uses: mohist/merge-github-pr
  with:
    prNumber: ${{ vars.github.pr.number }}
    method: squash
    recovery:
      budget: 2
      handlers:
        - when: base-moved
          tasks:
            - id: recover:rebase
              uses: mohist/rebase
              with:
                baseBranch: ${{ repository.baseBranch }}
                remote: origin
                squash: false
                conflictMode: task
            - id: recover:push
              uses: mohist/push
              with:
                source: ${{ workspace.branch }}
                target: ${{ workspace.branch }}
                remote: origin
                forceWithLease: true
          retrySelf: true
        - when: pr-checks-failed
          tasks:
            - id: recover:fix-pr-checks
              uses: mohist/acp-agent
              with:
                prompt: ${{ prompts.fix-pr-checks }}
                agent: ${{ vars.agent }}
            - id: recover:push
              uses: mohist/push
              with:
                source: ${{ workspace.branch }}
                target: ${{ workspace.branch }}
                remote: origin
                forceWithLease: true
          retrySelf: true
```

`mohist/default` 的 ai-review task：

```yaml
- id: ai-review
  uses: mohist/acp-agent
  with:
    prompt: ${{ prompts.review }}
    agent: ${{ vars.agent }}
    recovery:
      budget: 2
      handlers:
        - when: review-failed
          tasks:
            - id: recover:fix-review-findings
              uses: mohist/acp-agent
              with:
                prompt: ${{ prompts.auto-fix }}
                agent: ${{ vars.agent }}
          retrySelf: true
```
