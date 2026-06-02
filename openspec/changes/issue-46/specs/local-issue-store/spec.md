## MODIFIED Requirements

### Requirement: CLI 可以创建和管理本地 Issues

CLI SHALL 支持创建、查看、更新和关闭本地 Issues。Issue persistence SHALL store only the selected project repository reference, or the current default project repository reference when no repository is specified, and SHALL NOT persist a full mutable repository configuration snapshot as issue-owned authority.

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

#### Scenario: 创建 Issue 时存储显式 repository 引用
- **WHEN** the user creates an issue with an explicit project repository selection
- **THEN** the local issue store SHALL persist only the selected repository reference
- **AND** it SHALL NOT persist repository `path`, `remote`, `baseBranch`, or `isDefault` as issue-owned configuration fields

#### Scenario: 创建 Issue 时绑定默认 repository 引用
- **WHEN** the user creates an issue without an explicit repository selection
- **THEN** the local issue store SHALL persist the current default project repository reference
- **AND** later project default changes SHALL NOT rewrite the stored issue repository reference automatically

### Requirement: local issue store preserves repository reference compatibility for older issues
The local issue store SHALL migrate or interpret older issue rows that embedded repository snapshots so that stale `isDefault`, `path`, `remote`, and `baseBranch` values cannot override the current project repository configuration.

#### Scenario: Legacy issue snapshot is interpreted as a repository reference
- **WHEN** an existing issue row contains an embedded repository snapshot from an older schema
- **THEN** the store SHALL derive or preserve a stable repository reference from that snapshot
- **AND** issue reads SHALL resolve repository details from the current project repository configuration

#### Scenario: Legacy snapshot no longer matches a project repository
- **WHEN** an older issue row contains repository snapshot data that cannot be resolved to one current project repository
- **THEN** the store SHALL preserve the issue row
- **AND** repository-dependent reads SHALL surface a repository configuration problem instead of using the stale snapshot values
