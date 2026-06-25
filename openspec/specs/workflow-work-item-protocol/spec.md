### Requirement: Work item 是 WorkflowGrain 唯一的出方向公开协议

WorkflowGrain 对外暴露的协议 SHALL 是域语义的 work item，而非 dispatch。work item 携带声明与未解析的模板，SHALL NOT 携带解析后的值、dispatch id 或渲染好的执行上下文。协议方法从 `PollWorkAsync(runnerId) → WorkDispatch?` 变更为返回域 `WorkItem?` 的形式。

#### Scenario: work item 不含 dispatch 信息

- **WHEN** 调用方向 WorkflowGrain 拉取下一个工作
- **THEN** 返回的 work item SHALL 仅携带 stage、work id、title、uses、未解析的模板 with、声明的 artifacts/setVars（Task 变体）或待办 check 项（Checks 变体）
- **AND** work item SHALL NOT 包含 dispatch id、解析后的变量值、渲染好的执行上下文或已加载的 prompt

#### Scenario: 协议方法不再返回 WorkDispatch

- **WHEN** 调用方拉取工作
- **THEN** WorkflowGrain SHALL 返回域 `WorkItem?`（task/checks 变体或 null）
- **AND** SHALL NOT 返回 `WorkDispatch`

### Requirement: work item 仅有 task 与 checks 两个变体

work item SHALL 收敛为两个变体：`Task`（stage、id、title、uses、未解析模板 with、声明的 artifacts/setVars）与 `Checks`（stage、待办 check 项）。`WorkflowWork.StageInit` 变体 SHALL 被删除。

#### Scenario: 调用方永远只见 task 与 checks

- **WHEN** WorkflowGrain 产出下一步工作
- **THEN** 返回的 work item SHALL 是 `Task` 或 `Checks` 变体之一
- **AND** SHALL NOT 是 stage-init 或任何与 stage 初始化对应的变体

### Requirement: 入方向协议接收域结果而非 runner 原始输出

WorkflowGrain 的入方向协议 SHALL 接收域结果（`TaskOutcome` / `CheckOutcome`），而非 runner 的原始 `WorkResult`。`ReportResultAsync(runnerId, workId, WorkResult)` SHALL 被接收域结果的等价方法取代。

#### Scenario: 接收 TaskOutcome 域结果

- **WHEN** 一个 task 完成
- **THEN** WorkflowGrain SHALL 接收形如 `TaskOutcome(workId, passed|failed, output, artifacts)` 的域结果
- **AND** SHALL NOT 接收 runner 原始 `WorkResult`

#### Scenario: 接收 CheckOutcome 域结果

- **WHEN** 一个 stage 的 check 完成
- **THEN** WorkflowGrain SHALL 接收形如 `CheckOutcome(stage, results)` 的域结果

### Requirement: 失败结果只有 passed 与 failed，原因归入 detail

入方向的 task 结果 SHALL 只有 `passed` 与 `failed` 两种状态。超时与 runner-lost SHALL 是失败的"原因"（detail），而 NOT 独立的状态值。

#### Scenario: 超时表现为失败

- **WHEN** 一个 task 因执行超时而失败
- **THEN** 上报的域结果 SHALL 状态为 `failed`
- **AND** 超时事实 SHALL 体现在结果的 detail 中，而 NOT 作为一个独立的"timeout"状态

#### Scenario: runner 丢失表现为失败

- **WHEN** 一个 task 因 runner 丢失而被合成失败
- **THEN** 上报的域结果 SHALL 状态为 `failed`
- **AND** SHALL NOT 在状态字段上与普通失败区分

### Requirement: stage-init eager 化且永不对调用方可见

stage-init SHALL 改为 eager：进入一个 stage 时即完成初始化。`StageStarted` 事件 SHALL 蕴含该 stage 已完成 init。第一个 stage 在 `Start` 时初始化；后续 stage 在上一个 stage 完成、`Advance()` 进入它时初始化。

#### Scenario: 进入 stage 即初始化

- **WHEN** 工作流进入一个 stage（首个 stage 在 Start 时，后续 stage 在 Advance 进入时）
- **THEN** grain SHALL load fresh definition 并调用域 `InitializeStage(stageId, tasks, checks)`，使该 stage 在同一次提交内完成初始化
- **AND** `StageStarted` 事件 SHALL 蕴含该 stage 已 init

#### Scenario: 调用方永不可见 stage-init

- **WHEN** 调用方拉取工作
- **THEN** `NextWork()` SHALL 只见已初始化的 stage
- **AND** SHALL 只返回 task/checks，SHALL NOT 返回 stage-init 工作

#### Scenario: 统一提交前初始化步骤

- **WHEN** 任一触发转换（Start / ReportResult / Retry / Rerun）产出含 `StageStarted` 的事件
- **THEN** grain SHALL 通过一个统一的提交前步骤处理"事件里的 StageStarted → init"
- **AND** SHALL NOT 在四处分别重复编写初始化逻辑

#### Scenario: profile 结构变更只对新进入的 stage 生效

- **WHEN** workflow profile 的结构（增删 task）在工作流运行期间变更
- **THEN** 变更 SHALL 只对**新进入的 stage** 生效
- **AND** SHALL NOT 改动正在运行的 stage 的结构

### Requirement: server 与 runner 的 work item 镜像同一形状

work item SHALL 成为域一等概念。server 的 work item 与 runner（TypeScript）的 `WorkItem` SHALL 镜像同一形状，而 NOT 让 runner 镜像 dispatch 的反序列化产物。

#### Scenario: 跨端形状一致

- **WHEN** runner 接收到一个工作
- **THEN** runner TS 的 `WorkItem` 类型 SHALL 与 server 的 work item 形状一致
- **AND** SHALL NOT 是 dispatch 序列化结果的镜像
