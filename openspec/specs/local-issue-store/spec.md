# OpenSpec Capability: local-issue-store

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

#### Scenario: 查看 Issue 详情
- **WHEN** 用户执行 `mo issue show <id>`
- **AND** `<id>` 是 number 或 `project#number` 格式
- **THEN** CLI 显示 Issue 详情（title, body, stage, status, labels）和所有 comments

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

### Requirement: CLI 可以为 Issue 添加评论

CLI SHALL 支持追加式评论。

#### Scenario: 添加评论
- **WHEN** 用户执行 `mo issue comment <id> "comment text"`
- **THEN** Server 创建 comment 记录
- **AND** CLI 显示 "Comment added to <project#number>"

### Requirement: CLI 可以列出所有使用过的 Labels

CLI SHALL 显示项目中使用过的所有 labels。

#### Scenario: 列出 Labels
- **WHEN** 用户执行 `mo label list`
- **THEN** CLI 显示当前项目中所有使用过的 labels
- **AND** 按 label 名称排序

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

### Requirement: Issue 显示格式

Issue 在 CLI 输出中 SHALL 使用 `project#number` 格式。

#### Scenario: Issue 列表显示
- **WHEN** CLI 显示 Issue 列表
- **THEN** 每个 Issue 显示为 `project#number: title`

#### Scenario: Issue 详情显示
- **WHEN** CLI 显示 Issue 详情
- **THEN** 标题行显示 `project#number: title`

### Requirement: Issue model metadata storage

The local issue store SHALL persist issue-level model metadata as nullable fields: `model` for the issue default and `stageModels` for per-stage overrides. Missing or null values SHALL mean no issue-level override and SHALL NOT materialize inherited global defaults into the issue row.

#### Scenario: Store per-issue stage model overrides

- **WHEN** an issue is created or updated with `stageModels: { "build": "anthropic/claude-sonnet-4-20250514" }`
- **THEN** subsequent issue reads return `stageModels.build = "anthropic/claude-sonnet-4-20250514"`
- **AND** the persisted value is stored in the issue row as nullable JSON text

#### Scenario: Clear per-issue stage model overrides

- **WHEN** an issue is updated with `stageModels: null` or an empty override map
- **THEN** subsequent issue reads return no per-stage issue overrides
- **AND** model resolution can fall back to global stage models

#### Scenario: Malformed stored stage model JSON

- **WHEN** an existing issue row contains malformed `stage_models` JSON
- **THEN** issue reads succeed
- **AND** the issue is returned without per-stage issue overrides

