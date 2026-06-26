### Requirement: grain 零处引用 WorkflowDefinition

WorkflowGrain SHALL NOT 在任何处引用 `WorkflowDefinition` 整体对象。整模板选择（issue-custom > issue-ref > project-default > system-default 的级联）SHALL 封装在 profileManager 内部，`WorkflowDefinition` SHALL 降为 profileManager 的内部细节。

#### Scenario: grain 不持有整体 definition

- **WHEN** WorkflowGrain 需要 stage 规格、结构或审批配置
- **THEN** grain SHALL 通过 profileManager 提供的窄 API 获取，SHALL NOT 持有或传递整体 `WorkflowDefinition`
- **AND** `LoadEffectiveDefinitionAsync` SHALL 被删除

#### Scenario: 模板选择封装在 profileManager

- **WHEN** 需要确定生效模板
- **THEN** 选择级联 SHALL 在 profileManager 内部执行
- **AND** grain SHALL NOT 直接执行或感知级联过程

### Requirement: profileManager 提供 LoadStageSpecsAsync 窄 API

profileManager SHALL 提供 `LoadStageSpecsAsync(runId, stageId) → StageDefinition`，内部执行模板选择级联并切出指定 stage 的规格。该 API SHALL 替代 stage-init、`ProcessCheckResult`、`TryScheduleCheckRepair`、`GetSequentialLockResource` 这四个对 definition 的 over-fetch 点。

#### Scenario: 按 stage 取规格

- **WHEN** grain 需要某 stage 的规格
- **THEN** grain SHALL 调用 `LoadStageSpecsAsync(runId, stageId)`
- **AND** profileManager SHALL 内部跑级联 + 切出该 stage 并返回 `StageDefinition`
- **AND** grain SHALL NOT 通过整体 definition 索引 `Stages[stageId]`

### Requirement: profileManager 提供 LoadStructureAsync 窄 API

profileManager SHALL 提供 `LoadStructureAsync(runId) → stage 序列 + approval flags`，供 `Create` 使用。

#### Scenario: 取工作流结构

- **WHEN** grain 创建工作流时需要结构
- **THEN** grain SHALL 调用 `LoadStructureAsync(runId)`
- **AND** 返回值 SHALL 包含 stage 序列与 approval flags，而 NOT 整体 definition

### Requirement: profileManager 提供 LoadApprovalConfigAsync 窄 API

profileManager SHALL 提供 `LoadApprovalConfigAsync(runId) → ApprovalConfig`，供 `RequestChanges` 使用。

#### Scenario: 取审批配置

- **WHEN** grain 处理 RequestChanges 需要审批配置
- **THEN** grain SHALL 调用 `LoadApprovalConfigAsync(runId)`
- **AND** 返回值 SHALL 为 `ApprovalConfig`，而 NOT 整体 definition

### Requirement: 热重载通过每次重跑级联保持新鲜

每次调用 `LoadStageSpecsAsync` SHALL 重跑模板选择级联，使 profile 变更对后续 stage init 生效，从而保持热重载。级联仍 SHALL 全量执行（与现状频率相当）。

#### Scenario: 后续 stage 见到 profile 变更

- **WHEN** profile 结构（增删 task）在工作流运行期间变更
- **THEN** 该变更 SHALL 通过下次 `LoadStageSpecsAsync` 重跑级联被新进入的 stage 看到
- **AND** 值层（with / variables）SHALL 在 dispatch 时由调用方 fresh 解析
- **AND** 正在运行的 stage 结构 SHALL NOT 被改动
