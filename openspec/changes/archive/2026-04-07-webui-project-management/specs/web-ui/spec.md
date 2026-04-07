## ADDED Requirements

### Requirement: WebUI 支持通过对话框创建项目

WebUI SHALL 提供 `CreateProjectDialog` 组件，允许用户输入项目名称并通过 `DialogSelectDirectory` 选择工作目录路径来创建新项目。创建成功后 SHALL 自动切换到新项目并刷新项目列表。

#### Scenario: 成功创建项目
- **WHEN** 用户在 Header 下拉菜单点击 "New Project"
- **AND** 在对话框中输入名称 "my-project"
- **AND** 通过目录浏览器选择路径 "/home/user/repos/my-project"
- **AND** 点击 "Create"
- **THEN** 发送 `POST /api/projects` 请求（body: `{name, path}`）
- **AND** 项目列表自动刷新
- **AND** 当前项目自动切换到新创建的项目

#### Scenario: 创建项目名称已存在
- **WHEN** 用户输入已存在的项目名称
- **AND** 点击 "Create"
- **THEN** 后端返回 409 错误
- **AND** 对话框显示错误提示 "Project name already exists"
- **AND** 对话框保持打开状态

#### Scenario: 路径字段为空（验证失败）
- **WHEN** 用户只输入名称，未选择路径
- **AND** 点击 "Create"
- **THEN** 前端验证阻止提交
- **AND** 显示 "Path is required" 错误提示
- **AND** 不发送 API 请求

### Requirement: WebUI 提供搜索式目录浏览器

WebUI SHALL 提供 `DialogSelectDirectory` 组件，允许用户通过搜索、路径输入、Tab 补全和最近项目列表来选择目录。

#### Scenario: 模糊搜索目录
- **WHEN** 用户在搜索框中输入纯文本 "myapp"（不含 `/` 或 `~`）
- **THEN** 调用 `GET /api/fs/search?query=myapp&limit=50`
- **AND** 结果按 fuzzysort 相关度排序显示

#### Scenario: 路径输入逐段浏览
- **WHEN** 用户在搜索框中输入路径 "~/repos/my"
- **THEN** 解析为 HOME 起始的绝对路径
- **AND** 逐段调用 `GET /api/fs/list` 获取每级子目录
- **AND** 对最后一段 "my" 使用 fuzzysort 匹配

#### Scenario: Tab 键路径补全
- **WHEN** 用户输入 "~/repos/my-app" 并按 Tab
- **AND** 存在唯一匹配目录 "my-app-backend"
- **THEN** 搜索框自动补全为 "~/repos/my-app-backend/"

#### Scenario: 显示最近项目
- **WHEN** DialogSelectDirectory 打开
- **THEN** 顶部显示已创建的项目列表（最多 5 个），按最近更新时间排序

#### Scenario: 选择目录
- **WHEN** 用户点击某个目录项
- **THEN** DialogSelectDirectory 关闭
- **AND** 选中的绝对路径传递给调用方（CreateProjectDialog 的 path 字段）

### Requirement: WebUI 支持删除项目

WebUI SHALL 在 Header 项目下拉菜单中提供 "Delete Project" 操作，删除前 SHALL 弹出确认对话框。

#### Scenario: 成功删除项目
- **WHEN** 用户在 Header 下拉菜单点击 "Delete Project"
- **AND** 在确认对话框中点击 "Delete"
- **THEN** 发送 `DELETE /api/projects/:name` 请求
- **AND** 项目列表自动刷新
- **AND** 如果删除的是当前项目，切换到列表中第一个项目

#### Scenario: 删除最后一个项目
- **WHEN** 用户删除最后一个项目
- **AND** 在确认对话框中点击 "Delete"
- **THEN** 发送 `DELETE /api/projects/:name` 请求
- **AND** 项目列表为空
- **AND** 显示空状态引导页面

#### Scenario: 删除失败
- **WHEN** 后端返回错误（如项目不存在）
- **THEN** 显示错误提示信息

### Requirement: 无项目时显示空状态引导

WebUI SHALL 在没有项目时显示空状态引导页面，替代看板视图和 "Loading..." 文本。

#### Scenario: 首次访问无项目
- **WHEN** 用户打开 WebUI
- **AND** 项目列表为空
- **THEN** 显示空状态页面，包含提示文字 "No projects yet"
- **AND** 显示 "Create Project" 按钮

#### Scenario: 从空状态创建项目
- **WHEN** 用户在空状态页面点击 "Create Project"
- **THEN** 弹出 `CreateProjectDialog`
- **AND** 创建成功后自动切换到看板视图

### Requirement: 前端 API client 补齐项目管理方法

`api.ts` SHALL 添加 `createProject`、`deleteProject`、`useProject` 方法，分别对应后端 `POST /api/projects`、`DELETE /api/projects/:name`、`POST /api/projects/:name/use`。

#### Scenario: createProject 调用
- **WHEN** 调用 `api.createProject({name: "x", path: "/y"})`
- **THEN** 发送 `POST /api/projects` 请求，body 为 `{name, path}`
- **AND** 返回创建的 `Project` 对象

#### Scenario: deleteProject 调用
- **WHEN** 调用 `api.deleteProject("x")`
- **THEN** 发送 `DELETE /api/projects/x` 请求
- **AND** 返回成功消息

#### Scenario: useProject 调用
- **WHEN** 调用 `api.useProject("x")`
- **THEN** 发送 `POST /api/projects/x/use` 请求
- **AND** 返回更新的项目对象
