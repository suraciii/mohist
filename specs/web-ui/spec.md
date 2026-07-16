## MODIFIED Requirements

### Requirement: WebUI 支持通过对话框创建项目

WebUI SHALL provide `CreateProjectDialog` for creating a Mohist project scope with its initial repository name, Git URL, and optional base branch. The dialog SHALL NOT ask for, validate, browse, or submit a local filesystem path. 创建成功后 SHALL 自动切换到新项目并刷新项目列表。

#### Scenario: 成功创建项目
- **WHEN** 用户在 Header 下拉菜单点击 "New Project"
- **AND** 在对话框中输入名称 "my-project"
- **AND** 输入初始仓库名称、Git URL 和 base branch
- **AND** 点击 "Create"
- **THEN** 发送 `POST /api/projects` 请求（body: `{name, repository: {name, gitUrl, baseBranch}}`）
- **AND** the request SHALL NOT include `path`
- **AND** 项目列表自动刷新
- **AND** 当前项目自动切换到新创建的项目

#### Scenario: 创建项目名称已存在
- **WHEN** 用户输入已存在的项目名称
- **AND** 点击 "Create"
- **THEN** 后端返回 409 错误
- **AND** 对话框显示错误提示 "Project name already exists"
- **AND** 对话框保持打开状态

#### Scenario: 初始仓库字段为空（验证失败）
- **WHEN** 用户只输入名称 and no initial repository name or Git URL is supplied
- **AND** 点击 "Create"
- **THEN** the frontend SHALL NOT submit the create request
- **AND** it SHALL NOT display a local-path validation message

### Requirement: 前端 API client 补齐项目管理方法

`api.ts` SHALL provide `createProject`, `deleteProject`, and `useProject` methods for `POST /api/projects`, `DELETE /api/projects/:name`, and `POST /api/projects/:name/use`. `createProject` SHALL submit the initial repository declaration and SHALL NOT submit local filesystem paths.

#### Scenario: createProject 调用
- **WHEN** 调用 `api.createProject({name: "x", repository: {name: "main", gitUrl: "https://example.com/x.git"}})`
- **THEN** 发送 `POST /api/projects` 请求，body 包含 Project 名称和初始 repository declaration
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

**Migration**: Project creation uses a repository-backed dialog with Git URL and base branch fields. Workflow execution directories are runner-created workspaces and are not selected by the user.

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

### Requirement: WorkflowProfilesSection renders full multi-line descriptions

The Web UI `WorkflowProfilesSection` SHALL render each profile's full multi-line `description` in the profile list view, not just a one-line truncation. The description SHALL be visually distinct from the profile name and ID.

#### Scenario: Profile list shows description
- **WHEN** a user views the Workflow Profiles section in Settings
- **THEN** each profile card SHALL display the profile's display name, description, and ID
- **AND** the description SHALL render with preserved line breaks
- **AND** the description SHALL be visually more prominent than the profile ID

#### Scenario: Profile detail shows full description
- **WHEN** a user clicks into a specific profile
- **THEN** the detail view SHALL display the profile's full multi-line description at the top
- **AND** the stages summary and YAML definition SHALL appear below the description
- **AND** the description SHALL use readable formatting (not monospaced pre-formatted text unless the user explicitly views raw YAML)

#### Scenario: Profile with short description
- **WHEN** a profile has only a single-line description
- **THEN** the card and detail view SHALL still render it without truncation or formatting issues

### Requirement: WorkflowProfilesSection description is read before YAML editor

The profile detail view SHALL present the description as the primary readable metadata, with the raw YAML definition as secondary reference material that users can scroll to if needed.

#### Scenario: Description appears above YAML
- **WHEN** a user views a profile detail
- **THEN** the description SHALL appear at the top of the detail view
- **AND** the YAML editor/viewer SHALL appear in a section below
- **AND** the YAML section SHALL be clearly labeled as "Definition (YAML)" to distinguish it from the human-readable metadata

#### Scenario: First-time viewer understands the profile
- **WHEN** a user sees a profile for the first time
- **THEN** they SHALL be able to understand what the profile is for from the description alone
- **AND** they SHALL NOT need to read the raw YAML to make a selection decision
