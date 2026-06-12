## MODIFIED Requirements

### Requirement: WebUI 支持通过对话框创建项目

WebUI SHALL provide `CreateProjectDialog` for creating a Mohist project scope from a project name. The dialog SHALL NOT ask for, validate, browse, or submit a local filesystem path. 创建成功后 SHALL 自动切换到新项目并刷新项目列表。

#### Scenario: 成功创建项目
- **WHEN** 用户在 Header 下拉菜单点击 "New Project"
- **AND** 在对话框中输入名称 "my-project"
- **AND** 点击 "Create"
- **THEN** 发送 `POST /api/projects` 请求（body: `{name}`）
- **AND** the request SHALL NOT include `path`
- **AND** 项目列表自动刷新
- **AND** 当前项目自动切换到新创建的项目

#### Scenario: 创建项目名称已存在
- **WHEN** 用户输入已存在的项目名称
- **AND** 点击 "Create"
- **THEN** 后端返回 409 错误
- **AND** 对话框显示错误提示 "Project name already exists"
- **AND** 对话框保持打开状态

#### Scenario: 路径字段为空（验证失败）
- **WHEN** 用户只输入名称 and no local path is selected
- **AND** 点击 "Create"
- **THEN** the frontend SHALL submit the create request because project path is not part of the model
- **AND** it SHALL NOT display "Path is required"

### Requirement: 前端 API client 补齐项目管理方法

`api.ts` SHALL provide `createProject`, `deleteProject`, and `useProject` methods for `POST /api/projects`, `DELETE /api/projects/:name`, and `POST /api/projects/:name/use`. `createProject` SHALL submit project scope metadata only and SHALL NOT submit local filesystem paths.

#### Scenario: createProject 调用
- **WHEN** 调用 `api.createProject({name: "x"})`
- **THEN** 发送 `POST /api/projects` 请求，body 为 `{name}`
- **AND** 返回创建的 `Project` 对象
- **AND** the returned object SHALL NOT include `path` or `effectivePath`

#### Scenario: deleteProject 调用
- **WHEN** 调用 `api.deleteProject("x")`
- **THEN** 发送 `DELETE /api/projects/x` 请求
- **AND** 返回成功消息

#### Scenario: useProject 调用
- **WHEN** 调用 `api.useProject("x")`
- **THEN** 发送 `POST /api/projects/x/use` 请求
- **AND** 返回更新的项目对象

## REMOVED Requirements

### Requirement: WebUI 提供搜索式目录浏览器
**Reason**: Project creation no longer accepts or stores a local project path, so a directory browser for selecting a project checkout would reintroduce the removed product model.

**Migration**: Project creation uses a name-only dialog. Repository configuration uses Git URL and base branch fields. Workflow execution directories are runner-created workspaces and are not selected by the user.

#### Scenario: 模糊搜索目录
- **WHEN** 用户创建或配置项目
- **THEN** Web UI SHALL NOT offer local directory fuzzy search for project path selection

#### Scenario: 路径输入逐段浏览
- **WHEN** 用户创建或配置项目
- **THEN** Web UI SHALL NOT browse filesystem paths for project checkout selection

#### Scenario: Tab 键路径补全
- **WHEN** 用户创建或配置项目
- **THEN** Web UI SHALL NOT provide path completion for project checkout selection

#### Scenario: 显示最近项目
- **WHEN** project creation opens
- **THEN** Web UI SHALL NOT show recent local project directories as selectable project paths

#### Scenario: 选择目录
- **WHEN** 用户 creates a project
- **THEN** no selected absolute local path SHALL be passed to the create project request

## ADDED Requirements

### Requirement: Repository settings configure Git sources only
Settings > Repositories SHALL show and edit repository `name`, `gitUrl`, `baseBranch`, and `isDefault`. The UI SHALL remove Local Path inputs and SHALL NOT present local path and Git URL as equivalent repository address choices.

#### Scenario: Add repository from settings
- **WHEN** a user opens Settings > Repositories and adds a repository
- **THEN** the form SHALL require Git URL and base branch inputs
- **AND** it SHALL NOT show a Local Path input
- **AND** the submitted request SHALL include `gitUrl` rather than `path` or `remote`

#### Scenario: Repository list omits local path
- **WHEN** Settings > Repositories renders existing repositories
- **THEN** each repository SHALL show Git URL, base branch, name, and default status
- **AND** no repository row SHALL show a local checkout path or resolved path

### Requirement: Web UI uses workspace terminology
Web UI SHALL use workspace terminology for workflow execution status, review data, cleanup, changed files, and file-content surfaces. User-facing labels SHALL NOT use worktree terminology for runner-managed execution directories.

#### Scenario: Review surfaces describe workspace data
- **WHEN** a user views issue diff, commits, file content, or changed-files pages
- **THEN** the UI SHALL describe unavailable or available execution data as workspace data
- **AND** it SHALL NOT tell users to inspect a git worktree under a project path

#### Scenario: Cleanup actions use workspace wording
- **WHEN** a user sees or triggers cleanup for workflow execution data
- **THEN** labels and confirmations SHALL refer to the workflow workspace
- **AND** they SHALL NOT refer to removing a git worktree
