## Requirements

### Requirement: Issue 工作流状态机

Issue SHALL 按照定义的状态机流转。

#### Scenario: 完整工作流
- **WHEN** Issue 从 draft 开始
- **THEN** 按以下顺序流转：
  - draft → designing
  - designing → waiting-design-review
  - waiting-design-review → implementing (用户批准后)
  - implementing → waiting-review
  - waiting-review → merging (用户批准后)
  - merging → done

#### Scenario: 用户可以暂停
- **WHEN** Issue 在任何阶段
- **AND** 用户执行 `crawlph issue pause <number>`
- **THEN** Issue 进入 paused 状态
- **AND** 当前 agent 被终止

#### Scenario: 用户可以恢复
- **WHEN** Issue 处于 paused 状态
- **AND** 用户执行 `crawlph issue resume <number>`
- **THEN** Issue 从暂停点继续执行

### Requirement: 每个 Issue 对应一个 PR

Issue SHALL 对应一个 PR（单 PR 模式）。

#### Scenario: PR 创建
- **WHEN** designing 阶段完成
- **THEN** 创建一个新的 PR
- **AND** PR 初始只包含设计文档

#### Scenario: PR 更新
- **WHEN** implementing 阶段进行中
- **THEN** 代码被添加到同一个 PR
- **AND** PR 最终包含设计文档 + 完整实现

### Requirement: 用户在检查点介入

用户 SHALL 在关键检查点介入审查。

#### Scenario: 设计审查检查点
- **WHEN** designing 阶段完成
- **THEN** Issue 进入 waiting-design-review
- **AND** 等待用户执行 `crawlph pr approve` 才能继续

#### Scenario: 实现审查检查点
- **WHEN** implementing 阶段完成
- **THEN** Issue 进入 waiting-review
- **AND** 等待用户执行 `crawlph pr approve` 才能合并

### Requirement: PR 合并后标记 Issue 完成

PR 合并后 SHALL 自动标记 Issue 为 done。

#### Scenario: PR 合并
- **WHEN** PR 被合并
- **THEN** Issue 的 GitHub Label 更新为 `crawlph:stage/done`
- **AND** Issue 被关闭（可选）

### Requirement: Issue 操作基于当前项目

所有 Issue 操作 SHALL 基于当前项目上下文。

#### Scenario: 列出 Issues
- **WHEN** 用户执行 `crawlph issue list`
- **THEN** 返回当前项目的 Issues
- **AND** 显示当前项目名称

#### Scenario: 启动 Issue
- **WHEN** 用户执行 `crawlph issue start <number>`
- **THEN** 在当前项目的 repo 中启动处理
