## MODIFIED Requirements

### Requirement: CLI 可以创建和管理多个本地项目

CLI SHALL 通过 Server API 支持创建、列出、切换和删除本地项目。

#### Scenario: 创建项目
- **WHEN** 用户执行 `mo project create <name>`
- **THEN** CLI 发送 POST /api/projects 请求到 Server
- **AND** Server 在 `~/.mohist/mohist.db` 中创建项目记录
- **AND** CLI 显示 "Project '<name>' created"

#### Scenario: 列出项目
- **WHEN** 用户执行 `mo project list`
- **THEN** CLI 发送 GET /api/projects 请求到 Server
- **AND** CLI 显示所有项目列表
- **AND** 当前项目用 `*` 标记

#### Scenario: 切换当前项目
- **WHEN** 用户执行 `mo project use <name>`
- **THEN** CLI 发送 PATCH /api/config 请求到 Server
- **AND** Server 更新 config 表中的 `currentProjectId`
- **AND** CLI 显示 "Switched to project '<name>'"

#### Scenario: 删除项目
- **WHEN** 用户执行 `mo project remove <name>`
- **AND** 项目没有 issues
- **THEN** Server 删除项目记录
- **AND** CLI 显示 "Project '<name>' removed"
- **WHEN** 项目有 issues
- **THEN** CLI 返回错误 "Cannot remove project with issues. Delete issues first."
