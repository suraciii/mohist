# Workflow Stage Events

## 探索背景
- Mohist 正在把内置流程改造成用户可定制的 workflow 定义。
- `stage.on` 和 `task.emits` 需要表达用户定义的 stage 语义事件，不能和 Mohist 内部 runtime event 混在一起。
- 真实场景包括：review 修复后重新 review、Plan artifact 修复后重新 self-review、rebase 后重置 merge-ready。

## 关键发现
- Workflow internal event 是引擎事实，例如 task-completed、check-recorded、approval-requested。它们用于日志、状态投影和调度，不应该成为用户建模业务流程的主轴。
- Stage event 是 workflow 作者定义的语义事件，例如 `code.changed`、`plan.artifacts.changed`、`docs.updated`。它表达“这个语义发生后，哪些 task/check/approval 失效”。
- Task 可以自由抛出自定义 stage event。预定义全部 event 会破坏通用 workflow 的目标。
- `emits` 不应该表示“task 完成后一定触发这些事件”，而应该表示 task 被允许抛出的事件集合。
- 实际发生的事件应该来自 task result。Workflow engine 校验 task result events 是否属于 task definition 的 `emits`，再按当前 stage 的 `on` 规则响应。
- Mohist 可以为内置 task handler 判断真实事实，例如 agent/rebase handler 可决定是否返回 `code.changed`。但这个判断属于 task runtime，不属于 workflow engine 的通用规则。
- `on.<event>` 下不能只放裸的 `tasks/checks/approval` 字段，否则会丢失“事件发生后执行什么动作”的语义。`reset` 应该是动作名，而不是 `checks-and-approval` 这样的枚举值。
- `reset: checks-and-approval` 同时混合了 action 和 target shortcut，读起来像自然语言，但会和 `checks: all`、`approval: true` 重复，并让 parser 需要维护额外枚举。更清晰的形态是 `reset: { tasks, checks, approval }`。

## 可视化

```text
Task definition                 Task runtime result              Stage policy
┌────────────────────┐          ┌────────────────────┐           ┌────────────────────┐
│ emits:             │          │ events:            │           │ on:                │
│   - code.changed   │  allow   │   - code.changed   │  handle   │   code.changed:    │
│   - docs.updated   │ ───────▶ │                    │ ────────▶ │     reset:         │
│                    │          │                    │           │       checks: all  │
└────────────────────┘          └────────────────────┘           └────────────────────┘

Declared capability             Actual semantic event            Workflow reaction
```

```yaml
on:
  code.changed:
    reset:
      tasks:
        - ai-review
      checks: all
      approval: true
```

```text
stage event ─────▶ action ─────▶ targets
code.changed      reset          ai-review task
                                 all checks
                                 approval
```

## 决策与结论
- 用户可自定义 stage event 名称。
- `stage.on` 只响应当前 stage 显式声明的 event。
- `task.emits` 是 capability declaration。
- `task result.events` 是实际 raised events。
- Engine 不需要 `when`。事实判断由 task handler 或外部 task 自己负责。
- Engine 应拒绝未声明的 event，避免 task 绕过 workflow contract。
- `stage.on.<event>.reset` 是当前需要支持的唯一 reaction action。暂不引入 `then`、多 action pipeline 或复杂条件。
- `reset` 的目标应显式声明在 action block 内：
  - `tasks`: 需要重新执行的 task ids。
  - `checks`: `all` 或 check id list。
  - `approval`: 是否清除当前 stage approval。
- 默认 workflow 应从：
  - `reset: checks-and-approval`
  - `tasks: [...]`
  - `checks: all`
  - `approval: true`
  
  改为：
  - `reset: { tasks: [...], checks: all, approval: true }`

## 下一步
- 调整 `StageEventPolicy` 类型，把 `reset` 从枚举/shortcut 改为 reset target object。
- 更新 workflow parser/inspector/YAML serializer，让 `reset` block 成为用户输入和内置默认 workflow 的标准形态。
- 更新 `compileInvalidationPolicyFromStageEvents`，从 `eventPolicy.reset.tasks/checks/approval` 推导 invalidation targets。
- 删除或迁移旧的 `reset: checks-and-approval`、顶层 `tasks/checks/approval` 路径；本轮改造目标不保留兼容路径。
- 更新默认 workflow YAML/TS source。
- 更新测试，覆盖 parser diagnostics、compile 后 invalidation、默认 workflow YAML 输出。
