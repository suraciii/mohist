## Requirements

### Requirement: CLI 可以创建和管理多个本地项目

CLI SHALL 通过 Server API 支持创建、列出、切换和删除本地项目。

#### Scenario: 创建项目
- **WHEN** 用户执行 `mohist project create <name>`
- **THEN** CLI 发送 POST /api/projects 请求到 Server
- **AND** Server 在 `~/.mohist/mohist.db` 中创建项目记录
- **AND** CLI 显示 "Project '<name>' created"

#### Scenario: 列出项目
- **WHEN** 用户执行 `mohist project list`
- **THEN** CLI 发送 GET /api/projects 请求到 Server
- **AND** CLI 显示所有项目列表
- **AND** 当前项目用 `*` 标记

#### Scenario: 切换当前项目
- **WHEN** 用户执行 `mohist project use <name>`
- **THEN** CLI 发送 PATCH /api/config 请求到 Server
- **AND** Server 更新 config 表中的 `currentProjectId`
- **AND** CLI 显示 "Switched to project '<name>'"

#### Scenario: 切换到不存在的项目
- **WHEN** 用户执行 `mohist project use <name>`
- **AND** 项目不存在
- **THEN** server 返回 404 错误
- **AND** 当前 project 上下文保持不变

#### Scenario: 删除项目
- **WHEN** 用户执行 `mohist project remove <name>`
- **AND** 项目没有 issues
- **THEN** Server 删除项目记录
- **AND** CLI 显示 "Project '<name>' removed"
- **WHEN** 项目有 issues
- **THEN** CLI 返回错误 "Cannot remove project with issues. Delete issues first."

### Requirement: ProjectService 使用单一数据源

ProjectService SHALL 不维护内存 currentProjectId 字段，每次通过 configRepo 读取当前 project。

#### Scenario: ProjectService.getCurrent() 从 configRepo 读取
- **WHEN** ProjectService.getCurrent() 被调用
- **THEN** 从 configRepo 读取 currentProjectId
- **AND** 根据 configRepo 的值查找并返回 project

#### Scenario: 删除 ProjectService 内存 currentProjectId
- **WHEN** ProjectService 源码被检查
- **THEN** 不存在 `private currentProjectId: string | null` 字段

### Requirement: 前端通过 React Query mutation 管理项目状态变更

WebUI SHALL 使用 TanStack React Query 的 `useMutation` hook 封装项目创建、删除和切换操作，mutation 成功后 SHALL invalidate `['projects']` query cache。

#### Scenario: 创建项目 mutation 成功后刷新列表
- **WHEN** `createProject` mutation 成功
- **THEN** 自动 invalidate `['projects']` query
- **AND** 项目列表自动重新获取

#### Scenario: 删除项目 mutation 成功后刷新列表
- **WHEN** `deleteProject` mutation 成功
- **THEN** 自动 invalidate `['projects']` query
- **AND** 项目列表自动重新获取

### Requirement: Project 记录主干分支

Project 模型 SHALL 包含 `baseBranch` 字段，记录该项目的主干分支名称。

#### Scenario: 创建项目时自动检测 baseBranch

- **WHEN** 用户通过 API `POST /api/projects` 创建项目
- **AND** 请求未指定 `baseBranch` 参数
- **THEN** 系统执行 `git symbolic-ref refs/remotes/origin/HEAD` 检测默认分支
- **AND** 将解析出的分支名（如 `main`）存入 `base_branch` 列
- **AND** 返回的 Project 对象包含 `baseBranch` 字段

#### Scenario: 创建项目时手动指定 baseBranch

- **WHEN** 用户通过 API `POST /api/projects` 创建项目
- **AND** 请求包含 `baseBranch` 参数值为 `"develop"`
- **THEN** 系统使用 `"develop"` 作为 `baseBranch`，不执行自动检测
- **AND** 返回的 Project 对象 `baseBranch` 为 `"develop"`

#### Scenario: 自动检测失败时回退默认值

- **WHEN** 用户创建项目
- **AND** 项目路径不是 git 仓库或无 origin remote
- **AND** 请求未指定 `baseBranch`
- **THEN** 系统 SHALL 使用 `"main"` 作为 `baseBranch` 默认值

#### Scenario: 更新项目 baseBranch

- **WHEN** 用户通过 `PATCH /api/projects/:id` 请求更新 `baseBranch`
- **THEN** 系统更新数据库中的 `base_branch` 列
- **AND** 返回更新后的 Project 对象

#### Scenario: 已有项目 migration 自动填充 baseBranch

- **WHEN** 数据库从 schema version 7 迁移到 version 8
- **THEN** 系统 SHALL 对每个已有 project 执行 baseBranch 自动检测
- **AND** 将检测结果写入 `base_branch` 列
- **AND** 检测失败时使用 `"main"` 作为默认值

### Requirement: CLI 支持指定 baseBranch

CLI `mo project create` 命令 SHALL 支持 `--base-branch` 选项。

#### Scenario: 使用 --base-branch 创建项目

- **WHEN** 用户执行 `mo project create myproj --base-branch develop`
- **THEN** CLI 发送 `POST /api/projects` 请求，包含 `baseBranch: "develop"`
- **AND** 项目创建后显示 `baseBranch: develop`

#### Scenario: 不指定 --base-branch 创建项目

- **WHEN** 用户执行 `mo project create myproj`
- **THEN** CLI 发送 `POST /api/projects` 请求，不包含 `baseBranch` 字段
- **AND** 服务端自动检测并填充 baseBranch
