## MODIFIED Requirements

### Requirement: CLI 可以创建和管理本地 Issues

CLI SHALL 支持创建、查看、更新和关闭本地 Issues。Issue labels SHALL be key-value pairs of the form `key=value`, governed by the `issue-labels` capability. The CLI label parameter (`-l`) SHALL accept `key=value` tokens to set a label and SHALL accept a key (prefixed to denote removal) to remove a label by key.

#### Scenario: 创建 Issue
- **WHEN** 用户执行 `mo issue create "title" [-l key=value]...`
- **THEN** Server 在当前项目中创建 Issue
- **AND** CLI 显示 Issue 编号（如 `my-app#1`）
- **AND** Issue stage 为 `backlog`
- **AND** Issue status 为 `active`
- **AND** Issue number 在项目内递增
- **AND** Issue labels 包含指定的 key-value 对，每个 key 至多一个 value

#### Scenario: 创建 Issue 时没有当前项目
- **WHEN** 用户执行 `mo issue create "title"`
- **AND** 没有设置当前项目
- **THEN** CLI 返回错误 "No project selected. Use 'mo project use <name>' first."

#### Scenario: 列出 Issues
- **WHEN** 用户执行 `mo issue list [--stage <stage>] [--status <status>] [-l key=value]`
- **THEN** CLI 显示当前项目的 Issues
- **AND** 按 `updated_at` 降序排列
- **AND** 显示编号、标题、stage、status、labels（以 `key=value` 形式）

#### Scenario: 查看 Issue 详情
- **WHEN** 用户执行 `mo issue show <id>`
- **AND** `<id>` 是 number 或 `project#number` 格式
- **THEN** CLI 显示 Issue 详情（title, body, stage, status, labels 以 `key=value` 形式）和所有 comments

#### Scenario: 更新 Issue 标题
- **WHEN** 用户执行 `mo issue update <id> --title "new title"`
- **THEN** Server 更新 Issue 标题
- **AND** Server 更新 `updated_at`

#### Scenario: 更新 Issue body
- **WHEN** 用户执行 `mo issue update <id> --body "new body"`
- **THEN** Server 更新 Issue body
- **AND** Server 更新 `updated_at`

#### Scenario: 设置 Label（按 key）
- **WHEN** 用户执行 `mo issue update <id> -l stream=frontend`
- **THEN** Server 将 key `stream` 的 value 设为 `frontend`（upsert 语义）
- **AND** 如果该 key 已有不同 value，新 value 覆盖旧 value
- **AND** 非法的 key 或空 value 被拒绝，并给出清晰错误

#### Scenario: 移除 Label（按 key）
- **WHEN** 用户执行 `mo issue update <id> -l -stream`
- **THEN** Server 按 key `stream` 移除该 label
- **AND** 如果该 key 不存在，忽略（不报错）

#### Scenario: 关闭 Issue
- **WHEN** 用户执行 `mo issue close <id>`
- **THEN** Server 设置 status 为 `blocked`（或后续定义的 closed 状态）
- **AND** Server 更新 `updated_at`

#### Scenario: 重新打开 Issue
- **WHEN** 用户执行 `mo issue reopen <id>`
- **THEN** Server 设置 status 为 `active`
- **AND** Server 更新 `updated_at`

### Requirement: CLI 可以列出所有使用过的 Labels

CLI SHALL 显示当前项目中所有使用过的 label keys（即分类维度）。返回的列表 SHALL 为去重后的 key 集合，并按 key 名称排序。

#### Scenario: 列出 Labels
- **WHEN** 用户执行 `mo label list`
- **THEN** CLI 显示当前项目中所有使用过的 label keys（去重）
- **AND** 按 key 名称排序

### Requirement: 数据库扩展

系统 SHALL 扩展现有 SQLite schema。The local issue store SHALL persist issue-level start prerequisites as relationships from an Issue to its prerequisite issues, and SHALL provide reads needed to compute prerequisite delivery state and reject circular prerequisite declarations。Issue labels SHALL be persisted as a key-value map (JSON object) within the Issue's serialized aggregate state, governed by the `issue-labels` capability. This change SHALL NOT introduce a dedicated `labels` column or any schema migration.

#### Scenario: Issue label storage
- **WHEN** an Issue's labels are persisted or read
- **THEN** labels are stored as a key-value map (JSON object) inside the Issue's serialized aggregate state
- **AND** no dedicated `labels` column or schema migration is introduced for this change
- **AND** an Issue with no labels persists as the empty object `{}`

#### Scenario: Legacy flat labels are discarded on load
- **WHEN** an existing Issue's serialized state contains labels as a JSON array (the legacy flat `string[]` form)
- **THEN** the store deserializes the Issue with an empty label map instead of throwing
- **AND** no historical flat-label-to-key-value migration is performed

#### Scenario: Comments 表创建
- **WHEN** 数据库迁移到 schema version 2
- **THEN** 创建 comments 表
- **AND** comments 表包含 id, issue_id, body, created_at 字段

#### Scenario: Start prerequisite records are persisted
- **WHEN** Issue #201 records Issue #200 as a prerequisite issue
- **THEN** the local issue store persists the relationship from Issue #201 to Issue #200
- **AND** subsequent Issue reads can return Issue #201 with that start prerequisite

#### Scenario: Circular declaration can be evaluated from stored prerequisites
- **WHEN** the system evaluates whether Issue #200 may record Issue #201 as a prerequisite issue
- **THEN** the local issue store SHALL provide enough prerequisite lookup data to detect whether the declaration would make Issue #200 require itself before start
- **AND** the store SHALL NOT require parsing issue body text to answer that question
