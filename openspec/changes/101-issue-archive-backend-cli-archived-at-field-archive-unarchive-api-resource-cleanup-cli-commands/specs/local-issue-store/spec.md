## MODIFIED Requirements

### Requirement: CLI 可以创建和管理本地 Issues

CLI SHALL 支持创建、查看、更新和关闭本地 Issues。

#### Scenario: 创建 Issue
- **WHEN** 用户执行 `mo issue create "title" [-l label]...`
- **THEN** Server 在当前项目中创建 Issue
- **AND** CLI 显示 Issue 编号（如 `my-app#1`）
- **AND** Issue stage 为 `draft`
- **AND** Issue status 为 `active`
- **AND** Issue number 在项目内递增
- **AND** Issue labels 包含指定的 labels

#### Scenario: 创建 Issue 时没有当前项目
- **WHEN** 用户执行 `mo issue create "title"`
- **AND** 没有设置当前项目
- **THEN** CLI 返回错误 "No project selected. Use 'mo project use <name>' first."

#### Scenario: 列出 Issues
- **WHEN** 用户执行 `mo issue list [--stage <stage>] [--status <status>] [-l label]`
- **THEN** CLI 显示当前项目的 Issues
- **AND** 按 `updated_at` 降序排列
- **AND** 显示编号、标题、stage、status、labels
- **AND** 默认排除 archived_at IS NOT NULL 的 issue

#### Scenario: 列出已归档 Issues
- **WHEN** 用户执行 `mo issue list --archived`
- **THEN** CLI 只显示 archived_at IS NOT NULL 的 issue
- **AND** 显示中包含归档时间

#### Scenario: 列出所有 Issues（含归档）
- **WHEN** 用户执行 `mo issue list --all`
- **THEN** CLI 显示所有 issue，包括已归档的
- **AND** 已归档 issue 标注 `(archived)` 标记

#### Scenario: 查看 Issue 详情
- **WHEN** 用户执行 `mo issue show <id>`
- **AND** `<id>` 是 number 或 `project#number` 格式
- **THEN** CLI 显示 Issue 详情（title, body, stage, status, labels）和所有 comments
- **AND** 如果 issue 已归档，显示归档时间

#### Scenario: 更新 Issue 标题
- **WHEN** 用户执行 `mo issue update <id> --title "new title"`
- **THEN** Server 更新 Issue 标题
- **AND** Server 更新 `updated_at`

#### Scenario: 更新 Issue body
- **WHEN** 用户执行 `mo issue update <id> --body "new body"`
- **THEN** Server 更新 Issue body
- **AND** Server 更新 `updated_at`

#### Scenario: 添加 Label
- **WHEN** 用户执行 `mo issue update <id> -l +bug`
- **THEN** Server 添加 `bug` label
- **AND** 如果 label 已存在，忽略（不报错）

#### Scenario: 移除 Label
- **WHEN** 用户执行 `mo issue update <id> -l -bug`
- **THEN** Server 移除 `bug` label
- **AND** 如果 label 不存在，忽略（不报错）

#### Scenario: 关闭 Issue
- **WHEN** 用户执行 `mo issue close <id>`
- **THEN** Server 设置 status 为 `blocked`（或后续定义的 closed 状态）
- **AND** Server 更新 `updated_at`

#### Scenario: 重新打开 Issue
- **WHEN** 用户执行 `mo issue reopen <id>`
- **THEN** Server 设置 status 为 `active`
- **AND** Server 更新 `updated_at`

### Requirement: 数据库扩展

系统 SHALL 扩展现有 SQLite schema。

#### Scenario: Issues 表扩展
- **WHEN** 数据库迁移到 schema version 2
- **THEN** issues 表新增 `labels` 列（TEXT，JSON 数组）
- **AND** 现有 issues 的 labels 默认为 `[]`

#### Scenario: Comments 表创建
- **WHEN** 数据库迁移到 schema version 2
- **THEN** 创建 comments 表
- **AND** comments 表包含 id, issue_id, body, created_at 字段

#### Scenario: Issues 表新增 archived_at 列
- **WHEN** 数据库执行 archive migration
- **THEN** issues 表新增 `archived_at` 列（TEXT, DEFAULT NULL）
- **AND** 现有 issues 的 archived_at 默认为 NULL

## ADDED Requirements

### Requirement: IssueRepo 归档查询方法

IssueRepo SHALL 提供归档相关的查询和变更方法。

#### Scenario: archive 标记归档
- **WHEN** 调用 `issueRepo.archive(issueId)`
- **THEN** 设置该 issue 的 `archived_at` 为当前 ISO 时间戳
- **AND** 更新 `updated_at`

#### Scenario: unarchive 取消归档
- **WHEN** 调用 `issueRepo.unarchive(issueId)`
- **THEN** 设置该 issue 的 `archived_at` 为 NULL
- **AND** 更新 `updated_at`

#### Scenario: findArchived 查询已归档 issue
- **WHEN** 调用 `issueRepo.findArchived(projectId)`
- **THEN** 返回该项目中 `archived_at IS NOT NULL` 的所有 issue
- **AND** 按 `archived_at` 降序排列

#### Scenario: findAll 默认排除已归档
- **WHEN** 调用 `issueRepo.findAll(projectId)` 不带额外选项
- **THEN** 返回 `archived_at IS NULL` 的 issue
- **AND** 行为与旧版一致（现有调用方无需修改）

#### Scenario: findAll includeArchived
- **WHEN** 调用 `issueRepo.findAll(projectId, { includeArchived: true })`
- **THEN** 返回所有 issue（包含已归档的）

#### Scenario: findAll archivedOnly
- **WHEN** 调用 `issueRepo.findAll(projectId, { archivedOnly: true })`
- **THEN** 只返回 `archived_at IS NOT NULL` 的 issue
