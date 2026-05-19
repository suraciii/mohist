# Workflow task execution plan

## 探索背景

- Mohist 正在把 workflow 改造成用户可定制的通用定义，目标体验参考 GitHub Actions / Azure Pipelines。
- 当前 default workflow 仍把普通 task 的执行方式写在 `taskExecutionPolicies` 中，例如 `agentSessionRef: plan-artifacts`，用户需要同时阅读 task 列表和 policy 列表才能理解一个 step。
- 用户追问 GitHub Actions runner 的实现方式：它是否也把 execution policy 暴露在 workflow 定义中。

## 关键发现

- GitHub Actions 的用户定义以 step 为中心：step 自己声明 `uses` 或 `run`，并通过 `with` / `env` / `if` / timeout 等字段表达执行参数。
- `actions/runner` 内部消费的不是原始 YAML，而是服务端传入的 job message。runner 会把 message steps 归一化为内部 `ActionStep` / `IActionRunner`，`StepsRunner` 只执行已经准备好的 step queue。
- 这说明 GitHub Actions 的产品模型不是“用户维护一张 execution policy 表”，而是“用户写自包含 step，系统编译出内部执行计划”。
- Mohist 现在的 custom workflow 已经有 `task.uses` 和 `task.with.session` 的产品形态，但 default workflow 仍手写 `taskExecutionPolicies`，造成两套模型并存。

## 可视化

```text
GitHub Actions

workflow yaml
  jobs[].steps[]
    uses/run + with/env/if
        |
        v
server/runner job message
  ActionStep + inputs + condition + timeout
        |
        v
StepsRunner / ActionRunner
  execute prepared step queue
```

```text
Mohist target shape

workflow definition
  stages[].tasks[]
    uses: mohist/agent
    with.session: plan-artifacts
        |
        v
compileWorkflowDefinition
  validate uses/with
  infer execution kind
  materialize taskExecutionPolicies
        |
        v
ConfigDrivenStageRunner
  execute compiled plan
```

## 决策与结论

- `agentSessionRef` 不应该成为用户主要编辑面的字段；它是 `mohist/agent` runner 的内部 dispatch metadata。
- 用户定义应写成 `uses: mohist/agent` + `with.session`，与 GitHub Actions 的 `uses` + `with` 模型一致。
- `taskExecutionPolicies` 仍可作为 compiled runtime metadata 保留，避免大规模改 runner/projection/persistence。
- 生成普通静态 task policy 的责任应集中在 `compileWorkflowDefinition`，避免 default workflow、custom workflow、runner 各自推导 execution kind 和 session ref。
- stage/workflow 级策略仍有价值，例如 approval、repair、invalidation、delivery freeze；这些不是某个 task runner 的参数，不应强行塞进 task。

## 开放问题

- 未来是否把 compiled execution plan 从 `StageDefinition.taskExecutionPolicies` 拆成显式 `compiledExecutionPlan`，避免同一个类型同时表示 source definition 和 compiled definition。
- 是否为 `with` 增加按 `uses` 分发的 schema validator，例如 `mohist/agent` 允许 `session/prompt/promptFile`，`mohist/merge` 允许 `strategy/targetBranch`。
