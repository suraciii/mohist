## MODIFIED Requirements

### Requirement: CLI can create and manage repository-backed Projects

CLI SHALL 通过 Server API 支持创建、列出、切换和删除 Mohist 项目。Project SHALL be a Mohist scope for issues, workflow configuration, and repository references, and SHALL NOT store or expose a local filesystem path, effective path, checkout path, or equivalent local execution directory.

#### Scenario: Create a Project from a Git path
- **WHEN** a user runs `mo project create <name> --path <repository-path>`
- **THEN** the CLI resolves the Git work tree, origin Git URL, and base branch locally
- **AND** sends `POST /api/projects` with `{ name, repository: { name, gitUrl, baseBranch } }`
- **AND** the request body SHALL NOT include `path`, `effectivePath`, or any local checkout field
- **AND** Server creates the Project and its default repository in one operation
- **AND** CLI displays "Project '<name>' created"

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

### Requirement: Project repository references carry branch metadata

Project models SHALL NOT contain project-level `baseBranch` or local checkout fields. Repository references SHALL carry the Git URL and base branch metadata; `mo project create --path` resolves that metadata locally before calling the API.

#### Scenario: Project creation requires repository metadata

- **WHEN** a client sends `POST /api/projects` without a complete `repository` declaration
- **THEN** the API SHALL reject the request
- **AND** no Project SHALL be created

#### Scenario: Repository metadata updates remain repository-scoped

- **WHEN** a user changes a base branch
- **THEN** the user SHALL use the repository metadata API or `mo repo update`
- **AND** project-level branch fields SHALL be rejected or ignored according to the public validation contract

## ADDED Requirements

### Requirement: Repository references use Git URL metadata
Repository configuration SHALL contain `name`, `gitUrl`, `baseBranch`, and server-derived `isDefault`. Creation requests use `setDefault` to select a new repository; repository domain models, API read models, persisted rows, dispatch variables, and UI-facing data SHALL NOT contain `path`, `remote`, `resolvedPath`, or equivalent local checkout fields.

#### Scenario: Add repository with Git URL
- **WHEN** a user adds a repository to a project with `name`, `gitUrl`, `baseBranch`, and optional `setDefault`
- **THEN** Mohist SHALL persist and return those repository metadata fields
- **AND** Mohist SHALL NOT persist or return a local path, remote alias field, or resolved local execution path

#### Scenario: Reject path-only repository
- **WHEN** a user adds or updates a repository without `gitUrl`
- **THEN** Mohist SHALL reject the request with a validation error
- **AND** Mohist SHALL NOT treat a path field as a repository address; a Runner-visible local path may be supplied only as the `gitUrl` value

### Requirement: Project creation creates its default repository atomically
Project creation SHALL create one default repository from the resolved repository metadata. The CLI path remains bootstrap input and is not included in the API request.

#### Scenario: Reject project creation without repository
- **WHEN** a user creates a project with only a project name
- **THEN** Mohist SHALL reject the request
- **AND** Mohist SHALL NOT create a repository or project scope

#### Scenario: Configure a new default repository explicitly
- **WHEN** a user adds a repository with `setDefault = true`
- **THEN** Mohist SHALL make that repository the default for issue workflows
- **AND** the default repository SHALL be identified by `gitUrl` and `baseBranch`
