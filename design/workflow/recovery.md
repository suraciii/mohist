---
purpose: "Task recovery：runner 侧 action 驱动的恢复机制，取代 engine 侧 onFailure 编排。"
style: ["极简，只给目标态。"]
---

# Task Recovery

Task 执行遇到可恢复失败时，runner executor 用 `when` 表达式匹配 action output 字段，匹配 task 顶级 `recovery.handlers`，构造 recovery tasks，server 机械插入。

Recovery 是 task 完成的一部分，不是失败后的补救。Workflow engine 不理解 recovery 语义。

> 相关：action input/output 契约见 [`actions.md`](actions.md)；dispatch 见 [`task-dispatch.md`](task-dispatch.md)；report 链路见 [`scheduling.md`](scheduling.md)。

## 设计原则

- **Workflow engine 是通用执行引擎**：只理解 stage / task / check / completed / failed。不认识 `recovery`。
- **Recovery 是 task 顶级属性**：budget 和 handlers 在 `recovery` 下，与 `with`、`artifacts`、`setVars` 并列。engine 透传，不解释。action 不感知 `recovery`。
- **Recovery 匹配在 runner 侧**：runner 用 `when` 表达式（`field=value`）匹配 action output 的任意字段。action 只管正常返回 output，零 recovery 知识。
- **Recovery tasks 的定义可用户自定义**：用户在 `recovery.handlers` 里声明每个 handler 对应的 recovery tasks、with、retrySelf。
- **Recovery tasks 是真实 workflow tasks**：在 graph / timeline / status 中可见、可调度，与普通 task 无区别。
- **Recovery 视作 completed**：当前 task 产出了 recovery tasks 作为后续工作，它的使命完成了。

## recovery 结构

`recovery` 是 task 顶级属性，与 `with` 并列。不放入 `with`，因为 action 不消费它——消费者是 runner executor。

```yaml
- id: merge-pr
  uses: mohist/merge-github-pr
  with:
    prNumber: ${{ vars.github.pr.number }}
    method: squash
  recovery:
    budget: 2                                # 允许的自动恢复次数
    handlers:                                # 有序，first-match
      - when: errorCode=base-moved           # 匹配 output.errorCode
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
      - when: errorCode=pr-checks-failed
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
- `handlers[].when`：`field=value` 表达式，匹配 action output 的任意字段（如 `errorCode=base-moved`、`promise=FAIL`、`failureKind=conflict`）。
- `handlers[].tasks`：用户自定义 recovery tasks（uses / with 可自定义）。
- `handlers[].retrySelf`：runner 构造 budget-1 的 self-retry task 追加到 tasks 末尾。
- runner 单独渲染 `recovery`（与 `artifacts` 同理），再 dispatch。
- action 只收 `with`，不收 `recovery`。
- 手动 retry 从 task definition 取初始 `recovery`（含初始 budget），自然重置。

## 分工

| 层 | 职责 |
|---|---|
| workflow YAML (`recovery`) | 定义 budget、handlers（when 匹配条件、tasks、retrySelf） |
| action | 正常返回 output（含 `errorCode`），不感知 recovery |
| runner executor | 用 `matchesWhen(when, output)` 匹配 `handlers[].when` 表达式、检查 budget、构造 addTasks |
| engine | 机械插入 addTasks |

## when 匹配语义

`when` 是 `field=value` 表达式，匹配 action output 的任意字段：

```yaml
when: errorCode=base-moved       # output.errorCode == "base-moved"
when: promise=FAIL               # output.promise == "FAIL"
when: failureKind=conflict       # output.failureKind == "conflict"
```

匹配逻辑：解析 `when` 为 `field` 和 `expected`，检查 `String(output[field]) === expected`。

不限定字段名。每个 action 用自己的字段（`errorCode`、`failureKind`、`promise` 等），workflow YAML 的 `when` 表达式用对应字段。action 不感知 recovery，不设特殊字段。

marker 驱动的失败（如 `failIf: <promise>FAIL</promise>`）不需要中介：acp-agent 的 output 天然包含 `promise` 字段，`when: promise=FAIL` 直接匹配。

## WorkResult 扩展

Runner executor 构造好 addTasks 后，通过 WorkResult 返回给 engine：

```json
{
  "status": "completed",
  "message": "Merge failed (base-moved); recovery scheduled",
  "addTasks": [
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
      "with": { "prNumber": 13, "method": "squash" },
      "recovery": { "budget": 1, "handlers": [ ... ] }
    }
  ]
}
```

规则：

- `status: completed` + `addTasks`：task 完成，engine 插入这些 tasks 到当前 stage。
- `status: completed`（无 `addTasks`）：普通完成。
- `status: failed`：task 失败，workflow failed。
- retry self 是 `addTasks` 里的一个普通 task，runner executor 构造时 `recovery.budget` 递减。engine 不区分。

## Server 行为

```
result.status == "completed"
  → 标记 task completed
  → result.addTasks 非空？
      → 是：插入为当前 stage 的真实 workflow tasks（AddRuntimeTasks）
      → 否：无操作
  → Advance

result.status == "failed"
  → 标记 task failed → stage failed → workflow failed
```

Engine 不需要理解 `addTasks` 里 task 的 `uses`、`with` 等内容。插入逻辑复用现有 `AddRuntimeTasks`。

## Runner executor 行为

```
result = action.execute()

if result.success:
    return completed

output = parseJSON(result.output)
if !output:
    return failed

handler = recovery.handlers.find(h => matchesWhen(h.when, output))
if !handler:
    return failed

budget = recovery.budget ?? 0
if budget <= 0:
    return failed

# addTasks 从 handler 构造（engine dispatch 时 with 已展开，直接取值）
addTasks = handler.tasks

if handler.retrySelf:
    addTasks += constructRetrySelf(task, budget - 1)

return completed + addTasks
```

`retrySelf: true` 是 runner 的模板快捷方式：构造一个和原 task 相同 `uses`、相同 `with` 但 `recovery.budget - 1` 的 task（`recovery` 是顶级属性，递减不侵入 `with`）。用户不需要手写 retry task。

Recovery budget 的读取、检查、递减全部在 runner 侧完成。

## 与现有 onFailure 的对比

| | onFailure（旧） | recovery.handlers（新） |
|---|---|---|
| 匹配在哪 | server (engine) | runner (executor) |
| 匹配方式 | JSON path `output.errorCode` | `field=value` 表达式匹配 output 任意字段 |
| recovery task 定义 | `onFailure.cases.tasks` | `recovery.handlers[].tasks`（task 顶级） |
| budget 管理 | engine 状态 (RemainingRecoveries) | `recovery.budget`（task 顶级数据，runner 递减） |
| engine 理解的语义 | onFailure / cases / when / limit / retry | 只看 `addTasks` 有没有 |
| task 失败状态 | failed（即使后来 recovered） | completed（recovery 是完成的一部分） |
| action 参与 | 无（只返回 output） | 无（只返回 output） |

## 迁移

需要删除的 server 侧概念：

- `TaskFailureAction` / `TaskFailureCase`（domain definition）
- workflow YAML 的 `onFailure` 块
- `ResolveTaskFailureRecovery` / `BuildRecoverySequence` / `RecoverTaskFailure`（WorkflowGrain / WorkflowRun）
- `TaskRun.RemainingRecoveries` / `StageRun.RecoveryBudget`

需要新增的：

- `WorkResult` 增加 `addTasks` 字段（`List<RuntimeTaskInput>?`）
- `ReportResultAsync` 在 task completed 时检查 `addTasks`，非空则 `AddRuntimeTasks`
- runner executor 实现 recovery 逻辑（`matchesWhen` 表达式匹配、检查 budget、构造 addTasks）

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
      - when: errorCode=base-moved
        tasks:
          - id: recover:rebase
            uses: mohist/rebase
            with:
              baseBranch: ${{ repository.baseBranch }}
              remote: origin
              squash: false
          - id: recover:push
            uses: mohist/push
            with:
              source: ${{ workspace.branch }}
              target: ${{ workspace.branch }}
              remote: origin
              forceWithLease: true
        retrySelf: true
      - when: errorCode=pr-checks-failed
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

`mohist/local` 的 ai-review task：

```yaml
- id: ai-review
  uses: mohist/acp-agent
  with:
    prompt: ${{ prompts.review }}
    agent: ${{ vars.agent }}
  recovery:
    budget: 2
    handlers:
      - when: promise=FAIL
        tasks:
          - id: recover:fix-review-findings
            uses: mohist/acp-agent
            with:
              prompt: ${{ prompts.auto-fix }}
              agent: ${{ vars.agent }}
        retrySelf: true
```
