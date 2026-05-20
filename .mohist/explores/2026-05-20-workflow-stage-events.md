# Workflow Stage Events

## 探索背景
- Mohist 正在把内置流程改造成用户可定制的 workflow 定义。
- `stage.on`、task runtime result events、`onSuccess.emit` 需要表达用户定义的 stage 语义事件，不能和 Mohist 内部 runtime event 混在一起。
- 真实场景包括：review 修复后重新 review、Plan artifact 修复后重新 self-review、rebase 后重置 merge-ready。

## 关键发现
- Workflow internal event 是引擎事实，例如 task-completed、check-recorded、approval-requested。它们用于日志、状态投影和调度，不应该成为用户建模业务流程的主轴。
- Stage event 是 workflow 作者定义的语义事件，例如 `code.changed`、`plan.artifacts.changed`、`docs.updated`。它表达“这个语义发生后，哪些 task/check/approval 失效”。
- Task 可以自由抛出自定义 stage event。预定义全部 event 会破坏通用 workflow 的目标。
- Task 自身 contract 应声明它可能 raise 哪些事件，workflow YAML 不应该在每个 task 上重复声明 task 能力。否则用户会误以为事件是否发生由 YAML 控制，而不是由 task runtime 的事实判断控制。
- YAML task 级 `emits` 不适合作为 capability allowlist。更好的语义是 `onSuccess.emit`：用户显式配置“这个 task 成功后，workflow 视为额外事件发生”。
- 实际发生的事件应该来自 task result 与 YAML `onSuccess.emit` 的合并。Workflow engine 只按当前 stage 的 `on` 规则响应事件；未被 `stage.on` 消费的事件可以作为 task evidence 保留。
- Mohist 可以为内置 task handler 判断真实事实，例如 agent/rebase handler 可决定是否返回 `code.changed`。但这个判断属于 task runtime，不属于 workflow engine 的通用规则。
- `on.<event>` 下不能只放裸的 `tasks/checks/approval` 字段，否则会丢失“事件发生后执行什么动作”的语义。`reset` 应该是动作名，而不是 `checks-and-approval` 这样的枚举值。
- `reset: checks-and-approval` 同时混合了 action 和 target shortcut，读起来像自然语言，但会和 `checks: all`、`approval: true` 重复，并让 parser 需要维护额外枚举。更清晰的形态是 `reset: { tasks, checks, approval }`。

## 可视化

```text
Task contract / YAML hook        Task runtime result              Stage policy
┌────────────────────┐          ┌────────────────────┐           ┌────────────────────┐
│ task contract:     │          │ events:            │           │ on:                │
│   raises code...   │  raise   │   - code.changed   │  handle   │   code.changed:    │
│ onSuccess.emit:    │ ───────▶ │   - docs.updated   │ ────────▶ │     reset:         │
│                    │          │                    │           │       checks: all  │
└────────────────────┘          └────────────────────┘           └────────────────────┘

Task-owned capability / hook     Actual semantic event            Workflow reaction
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
- Task capability 不放在 workflow YAML 的 task 定义里，而放在 task use/handler contract 里。
- `task result.events` 是 task runtime 实际 raised events。
- `task.onSuccess.emit` 是 workflow 作者配置的成功后额外事件，不表示 task 自身事实判断。
- Engine 不需要 `when`。事实判断由 task handler 或外部 task 自己负责。
- Engine 不应该依赖 YAML task capability allowlist。是否允许某个 task runtime raise 某事件，应由 task contract/handler 层保证。Engine 只负责记录 result events 并按 stage `on` 响应。
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
- 从 workflow YAML task definition 中移除 capability `emits` 用法。
- 在 task definition 增加 `onSuccess.emit`，用于成功后无条件追加 workflow stage events。
- 在 task runtime contract/catalog 中声明内置 task 可能 raise 的事件，例如 `mohist/agent` / `mohist/rebase` 可 raise `code.changed`。
- 修改 WorkflowRun：task completion 时合并 task result events 与 `onSuccess.emit`，并不再用 YAML `emits` 校验。
- 更新默认 workflow：`fix-review-findings`、`fix-check-health`、`fix-merge-readiness` 不再声明 `emits: [code.changed]`；`fix-plan-review` 改为 `onSuccess.emit: [plan.artifacts.changed]`。
- 更新测试，覆盖 task result event、onSuccess.emit、旧 `emits` capability 移除后的 YAML 输出。
