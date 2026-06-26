### Requirement: work item → dispatch 的翻译在调用方一侧完成

出方向翻译（将 work item 渲染为 runner 可执行 dispatch）SHALL 在调用方（RunnerGrain 组合的 translator）完成，而 NOT 在 WorkflowGrain 内。`WorkflowDispatchBuilder` 与 `PrepareWorkAsync` / `MakeDispatchAsync` SHALL 迁移到调用方。

#### Scenario: grain 不构建 dispatch

- **WHEN** 调用方从 WorkflowGrain 拉取到一个 work item
- **THEN** 调用方 SHALL 自行将 work item 翻译为 dispatch 后执行
- **AND** WorkflowGrain SHALL NOT 调用 `WorkflowDispatchBuilder` 或 `MakeDispatchAsync`

#### Scenario: 翻译输入来源

- **WHEN** 调用方执行出方向翻译
- **THEN** 翻译所需输入（变量 / prompt 来自 profileManager、历史输出 / feedback 来自持久化 projection）SHALL 由调用方直接获取
- **AND** SHALL NOT 依赖 WorkflowGrain 的独占内存态

### Requirement: runner 格式 → 域结果的解析在调用方一侧完成

入方向翻译（runner 原始 `WorkResult` → 域结果 `TaskOutcome` / `CheckOutcome`）SHALL 在调用方完成。`ProcessTaskResult` / `ProcessCheckResult` / `ResolveRepairTasks` 的"runner 格式 → 域结果"解析 SHALL 迁移到调用方。

#### Scenario: grain 不解析 runner 格式

- **WHEN** 调用方从 runner 进程收到原始 `WorkResult`
- **THEN** 调用方 SHALL 将其解析为域结果后再上报 WorkflowGrain
- **AND** WorkflowGrain SHALL NOT 承担 runner 结果解析

### Requirement: grain 不承担执行面准备工作

WorkflowGrain SHALL NOT 承担变量解析、上下文装配、prompt 加载或 dispatch 构建（出方向），SHALL NOT 承担 runner 结果解析（入方向）。这些 SHALL 被视为执行面关注，归属调用方。

#### Scenario: 执行面关注移出控制面

- **WHEN** 审视 WorkflowGrain 职责
- **THEN** 变量解析、上下文装配、prompt 加载、dispatch 构建、runner 结果解析 SHALL 全部不在 grain 内
- **AND** 这些职责 SHALL 落在调用方一侧

### Requirement: artifact 绑定归属执行面

artifact 绑定倾向在调用方完成：调用方绑定后把引用放进 `TaskOutcome`。最终归属 SHALL 在实施时确认，但 WorkflowGrain 内不 SHALL 直接承担 artifact 绑定（除非实施确认其属控制面）。

#### Scenario: 调用方绑定 artifact

- **WHEN** 一个 task 产出 artifacts
- **THEN** 调用方 SHALL 完成绑定并将引用放进 `TaskOutcome`
- **AND** WorkflowGrain SHALL 只消费引用，而 NOT 直接执行绑定（除非实施明确确认归属控制面）
