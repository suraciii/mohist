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
