### Requirement: fresh stage 初始化位于独立单一职责初始化器

fresh stage 初始化（原 `WorkflowGrain.InitializeFreshStagesAsync`）SHALL 驻留在独立的初始化器 `WorkflowStageInitializer` 中，位于 `packages/server/src/Mohist.Server/Workflow/Grains/`，与既有 `WorkflowReadModel` 同区域。`WorkflowGrain` SHALL NOT 在自身内部内联该实现，SHALL 通过对该初始化器的委托调用访问 fresh stage 初始化能力。

#### Scenario: 初始化实现不在 grain 内联

- **WHEN** 检查 fresh stage 初始化（对 `StageStarted` 事件的物化循环）的实现位置
- **THEN** 该实现 SHALL 位于 `WorkflowStageInitializer`
- **AND** `WorkflowGrain` SHALL NOT 在自身内部内联其实现
- **AND** grain 的 `CommitAsync` SHALL 通过一次对初始化器的委托调用访问该能力

### Requirement: 初始化仍在 CommitAsync 内、持久化保存之前执行

`WorkflowStageInitializer` SHALL 在 `WorkflowGrain.CommitAsync` 内被调用，且 SHALL 在 `SaveRunAsync`（持久化保存）之前执行。初始化产生的合并事件 SHALL 替换原始事件传给后续保存与发布，使 StageStarted 与其物化结果在同一批事务内落库。该执行时机 SHALL 与抽取前逐字一致。

#### Scenario: 初始化先于持久化保存

- **WHEN** `CommitAsync(events)` 被调用
- **THEN** SHALL 先调用 `WorkflowStageInitializer` 物化 fresh stage
- **AND** SHALL 在物化完成之后才执行持久化保存
- **AND** 物化合并后的事件 SHALL 成为后续保存与发布所用的最终事件列表

#### Scenario: 初始化跳过无 run 的提交

- **WHEN** `CommitAsync` 在 `_run` 为 null 时被调用
- **THEN** 初始化 SHALL 直接返回原始事件且不访问任何 stage spec

### Requirement: StageStarted ⟹ Initialized 不变量与物化循环语义保持

`WorkflowStageInitializer` SHALL 维持不变量 `StageStarted ⟹ Initialized`：每个被持久化或经 `NextWork` 暴露的 `StageStarted` 事件 SHALL 对应一个已初始化的 stage run。初始化器 SHALL 扫描事件中尚未初始化的 `StageStarted`，经 `WorkflowProfileManager.LoadStageSpecsAsync`（以 GrainKey、stage、projectId、issueId 为参数，projectId / issueId 为空时传 null）加载 spec，调用 `run.InitializeStage(stageDef.Tasks, stageDef.Checks)`，并把产生的 init 事件并入事件列表。循环 SHALL 终止于「无新增可物化的 `StageStarted`」——因为每次 `InitializeStage` → `Advance` 可能跳过一个空 stage 并为下一个 stage 发出新的 `StageStarted`，该新事件也 MUST 被初始化。已处理的 stage SHALL 用集合去重以防重复初始化。

#### Scenario: 未初始化的 StageStarted 被物化

- **WHEN** 事件列表含一条 `StageStarted(stage)` 且对应 stage run 的 `Initialized` 为 false
- **THEN** 初始化器 SHALL 加载该 stage 的 spec
- **AND** SHALL 调用 `run.InitializeStage(stageDef.Tasks, stageDef.Checks)`
- **AND** SHALL 把产生的 init 事件并入事件列表

#### Scenario: 级联 StageStarted 被完整物化

- **WHEN** 一次 `InitializeStage` 触发的 `Advance` 跳过空 stage 并为下一个 stage 发出新的 `StageStarted`
- **THEN** 初始化器 SHALL 在下一轮循环中物化该新的 `StageStarted`
- **AND** 循环 SHALL 在再无未初始化 `StageStarted` 时终止

#### Scenario: 已处理 stage 不重复初始化

- **WHEN** 同一 stage 在事件列表中出现多次 `StageStarted`
- **THEN** 初始化器 SHALL 仅对其物化一次
- **AND** SHALL 用去重集合跟踪已处理的 stage

#### Scenario: stage spec 加载参数与抽取前一致

- **WHEN** 初始化器为某 stage 加载 spec
- **THEN** SHALL 以 GrainKey、stage、projectId（空白则 null）、issueId（空白则 null）为参数调用 `WorkflowProfileManager.LoadStageSpecsAsync`
- **AND** projectId / issueId SHALL 取自 run metadata annotations，与抽取前一致

### Requirement: 事件合并顺序与发布契约不变

初始化器返回的合并事件列表 SHALL 保持「原始事件在前、init 事件追加其后」的顺序，使 `CommitAsync` 在单批中提交以保留下游订阅者观察到的事件顺序。抽取 SHALL NOT 改变事件发布顺序、`[Reentrant]` 并发语义、持久化 JSON blob + ETag 契约、ETag 冲突时 `DeactivateOnIdle()` 重载路径，SHALL NOT 在 run 突变中途引入新的 async 让出点。

#### Scenario: 合并事件保留原始顺序

- **WHEN** 初始化器把 init 事件并入事件列表
- **THEN** 原始事件相对顺序 SHALL 保持不变
- **AND** init 事件 SHALL 追加在原始事件之后
- **AND** `CommitAsync` SHALL 以该合并列表作为最终发布事件

#### Scenario: 持久化与并发契约不变

- **WHEN** 在抽取后检查 grain 的持久化 JSON blob + ETag、`[Reentrant]` 并发语义、事件发布顺序
- **THEN** 三者 SHALL 与抽取前逐字一致
- **AND** 抽取 SHALL NOT 在初始化循环中途引入原 grain 不存在的新 async 让出点（`LoadStageSpecsAsync` 的既有 await 除外）

### Requirement: 既有 Workflow/Grain 行为守护 spec 不加修改通过

抽取 SHALL NOT 改变 fresh stage 初始化、空 stage 自动跳过、stage 推进、`StageStarted ⟹ Initialized` 不变量等可观察行为。`packages/server/tests/Mohist.Server.Tests/Specs/Workflow/Grain/` 下既有的 stage-init / advance / happy-path 相关 spec 文件 SHALL 不加修改地全部通过。

#### Scenario: stage 初始化与推进相关 spec 全部通过

- **WHEN** 在抽取后运行 `Specs/Workflow/Grain/` 下的全部 spec（含 `StageInitEagerSpecs`、`AdvanceSpecs`、`HappyPathSpecs`、`ApprovalGateSpecs`、`StatusSpecs` 等）
- **THEN** 所有 spec SHALL 通过
- **AND** 无任何 spec SHALL 被弱化、跳过或改写以适配结构改动
