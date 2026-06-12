## MODIFIED Requirements

### Requirement: CLI 可以创建和管理多个本地项目

CLI SHALL 通过 Server API 支持创建、列出、切换和删除 Mohist 项目。Project SHALL be a Mohist scope for issues, workflow configuration, and repository references, and SHALL NOT store or expose a local filesystem path, effective path, checkout path, or equivalent local execution directory.

#### Scenario: 创建项目
- **WHEN** 用户执行 `mohist project create <name>`
- **THEN** CLI 发送 POST /api/projects 请求到 Server
- **AND** the request body SHALL NOT include `path`, `effectivePath`, or any local checkout field
- **AND** Server 在 `~/.mohist/mohist.db` 中创建项目记录 without storing a local filesystem path
- **AND** CLI 显示 "Project '<name>' created"

#### Scenario: 列出项目
- **WHEN** 用户执行 `mohist project list`
- **THEN** CLI 发送 GET /api/projects 请求到 Server
- **AND** CLI 显示所有项目列表
- **AND** 当前项目用 `*` 标记
- **AND** listed projects SHALL NOT include path, effective path, or local checkout fields

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

### Requirement: Project 记录主干分支

Project 模型 SHALL NOT contain `baseBranch` as a project-local checkout branch field. Repository references SHALL carry base branch metadata, and Project creation SHALL NOT inspect a local project path or git repository to infer branch information.

#### Scenario: 创建项目不自动检测 baseBranch

- **WHEN** 用户通过 API `POST /api/projects` 创建项目
- **AND** 请求未指定 repository metadata
- **THEN** 系统 SHALL create the Project without executing git commands
- **AND** the returned Project object SHALL NOT include `path`, `effectivePath`, or project-level `baseBranch`

#### Scenario: 创建项目时手动指定 baseBranch

- **WHEN** 用户通过 API `POST /api/projects` 创建项目
- **AND** 请求包含 project-level `baseBranch` 参数值为 `"develop"`
- **THEN** 系统 SHALL reject or ignore project-level `baseBranch` according to the public validation contract
- **AND** repository base branch configuration SHALL be accepted only through repository configuration

#### Scenario: 自动检测失败时回退默认值

- **WHEN** 用户创建项目
- **AND** no repository has been configured
- **THEN** 系统 SHALL NOT inspect a local project path
- **AND** 系统 SHALL NOT infer a project-level base branch default from the filesystem

#### Scenario: 更新项目 baseBranch

- **WHEN** 用户通过 `PATCH /api/projects/:id` 请求更新 project-level `baseBranch`
- **THEN** 系统 SHALL reject or ignore the project-level branch field according to the public validation contract
- **AND** repository branch updates SHALL use repository configuration APIs

#### Scenario: 已有项目 migration 自动填充 baseBranch

- **WHEN** 数据库 schema is initialized for this change
- **THEN** 系统 SHALL NOT perform legacy migration that derives project path or project-level base branch from old path-only data
- **AND** existing path-only project configuration is not preserved

## ADDED Requirements

### Requirement: Repository references use Git URL metadata
Repository configuration SHALL contain `name`, `gitUrl`, `baseBranch`, and `isDefault`. Repository domain models, API read models, persisted rows, dispatch variables, and UI-facing data SHALL NOT contain `path`, `remote`, `resolvedPath`, or equivalent local checkout fields.

#### Scenario: Add repository with Git URL
- **WHEN** a user adds a repository to a project with `name`, `gitUrl`, `baseBranch`, and `isDefault`
- **THEN** Mohist SHALL persist and return those repository metadata fields
- **AND** Mohist SHALL NOT persist or return a local path, remote alias field, or resolved local execution path

#### Scenario: Reject path-only repository
- **WHEN** a user adds or updates a repository without `gitUrl`
- **THEN** Mohist SHALL reject the request with a validation error
- **AND** Mohist SHALL NOT treat a local path as a repository address

### Requirement: Default repository is not created from project path
Project creation SHALL NOT create a default repository from a local project path. A default repository SHALL be selected only from explicit repository metadata that includes a Git URL.

#### Scenario: Create project without repository
- **WHEN** a user creates a project with only a project name
- **THEN** Mohist SHALL create the project scope
- **AND** Mohist SHALL NOT create a repository whose address is derived from a local project path

#### Scenario: Configure default repository explicitly
- **WHEN** a user configures a repository with `isDefault = true`
- **THEN** Mohist SHALL make that repository the default for issue workflows
- **AND** the default repository SHALL be identified by `gitUrl` and `baseBranch`
