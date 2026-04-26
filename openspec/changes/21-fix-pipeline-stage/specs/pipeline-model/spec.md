## ADDED Requirements

### Requirement: Pipeline stages are re-entrant with checkpoint semantics

每个 pipeline stage SHALL 支持幂等重入：当 stage 因中断被重新执行时，已完成的子步骤（通过 checkpoint 记录）SHALL 被跳过，stage 从第一个未完成的子步骤继续。

#### Scenario: Plan stage 重入跳过已完成的 rounds

- **WHEN** issue stage 为 `plan` 且 pipeline 开始执行 plan stage
- **AND** checkpoint 记录 `completed_steps: ["proposal", "specs"]`，`next_step: "design"`
- **THEN** plan stage 跳过 proposal 和 specs round
- **AND** 从 design round 开始执行
- **AND** 已完成的 artifact 文件（proposal.md, specs/）保留不被清除

#### Scenario: Build stage 重入跳过已完成的 tasks

- **WHEN** issue stage 为 `build` 且 pipeline 开始执行 build stage
- **AND** checkpoint 记录已完成的 task ID 列表
- **THEN** build stage 跳过已完成的 tasks
- **AND** 仅执行剩余的 pending tasks
- **AND** 已完成的 task 的代码改动保留

#### Scenario: Checkpoint 在 stage 首次进入时创建

- **WHEN** pipeline 首次进入某个 stage
- **AND** 无该 issue + stage 的 checkpoint 记录
- **THEN** 系统创建初始 checkpoint（`completed_steps: []`，`next_step` 为第一个子步骤）
- **AND** 正常执行所有子步骤

### Requirement: Stage 进入时优先检查磁盘 artifact 而非仅依赖 checkpoint

当 checkpoint 和磁盘状态不一致时，系统 SHALL 以磁盘 artifact 的实际存在性为准进行验证。

#### Scenario: Checkpoint 记录已完成但 artifact 不存在

- **WHEN** checkpoint 记录 `proposal` 已完成
- **AND** 磁盘上 `proposal.md` 不存在
- **THEN** 系统将 `proposal` 视为未完成
- **AND** 重新执行 proposal round

#### Scenario: Checkpoint 未记录但 artifact 已存在

- **WHEN** checkpoint 无 `proposal` 的完成记录
- **AND** 磁盘上 `proposal.md` 已存在
- **THEN** 系统将 `proposal` 视为已完成
- **AND** 更新 checkpoint 记录
- **AND** 跳过 proposal round
