## ADDED Requirements

### Requirement: Issue priority 数据模型

系统 SHALL 为每个 Issue 存储一个结构化优先级字段，值为 `p0`、`p1`、`p2`、`p3`、`p4` 之一，默认 `p2`。

#### Scenario: Issue 默认优先级
- **WHEN** 创建 Issue 时未指定 priority
- **THEN** Issue 的 priority 为 `p2`

#### Scenario: Issue 指定优先级
- **WHEN** 创建 Issue 时指定 priority 为 `p0`
- **THEN** Issue 的 priority 为 `p0`

#### Scenario: 优先级值校验
- **WHEN** 设置 Issue priority 为非 `p0`–`p4` 的值（如 `p5` 或 `high`）
- **THEN** 系统 SHALL 拒绝该值并返回错误

### Requirement: Priority 数据库迁移

系统 SHALL 通过 schema migration v14 添加 `priority` 列到 issues 表，并从现有 `priority:*` labels 中提取优先级。

#### Scenario: 添加 priority 列
- **WHEN** 数据库迁移到 schema version 14
- **THEN** issues 表新增 `priority` 列（TEXT, NOT NULL, DEFAULT 'p2'）
- **AND** 新增索引 `idx_issues_project_priority` on `issues(project_id, priority)`

#### Scenario: 从 priority:* labels 迁移
- **WHEN** 数据库迁移到 schema version 14
- **AND** 一个 issue 的 labels 包含 `priority:critical` 或 `priority:p0`
- **THEN** 该 issue 的 priority 设为 `p0`
- **AND** `priority:critical` 和 `priority:p0` label 从 labels 数组中移除

#### Scenario: Label 映射规则
- **WHEN** 迁移遇到 priority:* labels
- **THEN** 按以下规则映射：`priority:critical` / `priority:p0` → `p0`，`priority:high` / `priority:p1` → `p1`，`priority:medium` / `priority:p2` → `p2`，`priority:low` / `priority:p3` → `p3`，`priority:backlog` / `priority:p4` → `p4`

#### Scenario: 无 priority labels 的 issue
- **WHEN** 数据库迁移到 schema version 14
- **AND** 一个 issue 没有 `priority:*` labels
- **THEN** 该 issue 的 priority 为默认值 `p2`（列定义的 DEFAULT）

### Requirement: Priority 排序规则

Issue 列表 SHALL 支持按优先级排序，优先级高的在前（p0 < p1 < p2 < p3 < p4）。

#### Scenario: 默认排序包含优先级
- **WHEN** 查询 issue 列表且未指定排序
- **THEN** 结果按 priority ASC 排序
- **AND** 相同 priority 内按 number ASC 排序

#### Scenario: 相同优先级排序
- **WHEN** 两个 issue 的 priority 均为 `p2`
- **AND** issue #3 和 issue #5
- **THEN** issue #3 排在 issue #5 前面
