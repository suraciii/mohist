# Workflow Task Runs

## 探索背景
- Mohist 的默认 review stage 会循环执行 `ai-review -> review-passed -> fix-review-findings -> ai-review`。
- 这里不应该原地 reset 已完成的 `ai-review`。
- 也不需要引入新的用户侧领域概念；本质只是同一个 task definition 可以产生多个 `TaskRun`。

## 关键发现
- 用户定义的是 task definition，例如 `ai-review`。
- 运行时发生的是 task run：第一次 `ai-review` 已经真实完成，代码变化后需要再产生一个新的 `TaskRun`。
- 旧 `TaskRun` 是历史证据；新 `TaskRun` 是当前要执行的工作。
- 原地 reset 会把历史、恢复、artifact 证据混在一起；追加新的 `TaskRun` 更贴近真实开发流程。

## 可视化

```text
YAML definition
└─ task: ai-review

Runtime task runs
├─ ai-review    completed
│  └─ review.md was valid for old code
├─ review-passed failed
├─ fix-review-findings completed
│  └─ emits code.changed
├─ ai-review:1  pending/running/completed
│  └─ must produce fresh review.md
└─ review-passed pending/pass/fail
```

## 决策与结论
- 事件失效不应该 reset existing `TaskRun`，而应该 append 一个新的 `TaskRun`。
- YAML 不需要暴露运行时 task run id；用户仍然只定义 `ai-review`。
- Engine 从 workflow definition 和 event policy 推导下一次 task run。
- 当前用户可见状态仍然是三个简单状态：未运行、失败、成功；旧 task run 用于审计和恢复。
- 当前流程推进只看每个 task definition 的最新 task run；旧 task run 不应继续决定 stage 是否失败、是否可运行 check、是否可完成。

## 实施方向
- 继续保留 base task id 解析边界：`ai-review:1` 对应用户定义的 `ai-review`。
- invalidation append 新 `TaskRun`，例如 `ai-review:1`，而不是把 `ai-review` 原地 reset。
- checks 的完整性判断应以每个 definition 的最新 task run 为准，而不是要求所有历史 task run 都成功。
- checkpoint/artifact restore 应以 task run id 为边界；新的 task run 不应复用旧 task run 的 checkpoint。
- Workflow event 中可以携带 runtime task run id；需要按用户定义 task 匹配副作用时，消费者应回到 base task id。

## 开放问题
- check 是否也要同步改成追加新的 check run。长期应该做，但第一步可以先 reset check state，让当前 visible check 重新运行。
- UI 是否要显示历史 task run。短期不必作为主界面概念，debug/日志可以显示。
