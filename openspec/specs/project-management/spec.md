## Requirements

### Requirement: Server 可以创建和管理多个项目

Server SHALL 支持创建、列出、切换和删除项目。

#### Scenario: 创建项目
- **WHEN** 用户执行 `crawlph project create <name> --repo <owner/repo>`
- **THEN** Server 在 `~/.crawlph/projects.json` 中注册项目
- **AND** Server 创建项目配置
- **AND** Server 可选地在 GitHub repo 创建 Labels

#### Scenario: 列出项目
- **WHEN** 用户执行 `crawlph project list`
- **THEN** Server 返回所有已注册的项目列表
- **AND** 每个项目显示名称、repo、状态

#### Scenario: 切换当前项目
- **WHEN** 用户执行 `crawlph project use <name>`
- **THEN** Server 更新 `~/.crawlph/config.json` 中的 `currentProject`
- **AND** 后续命令默认使用该项目

#### Scenario: 删除项目
- **WHEN** 用户执行 `crawlph project remove <name>`
- **THEN** Server 从 `projects.json` 中移除项目
- **AND** 不删除项目目录中的配置文件

### Requirement: Server 可以基于目录自动检测项目

Server SHALL 能够根据当前工作目录自动识别项目。

#### Scenario: 在项目目录下执行命令
- **WHEN** 当前目录包含 `.crawlph/config.json`
- **THEN** Server 自动使用该项目
- **AND** 无需用户显式切换

#### Scenario: 不在项目目录下
- **WHEN** 当前目录不包含 `.crawlph/config.json`
- **AND** 用户未设置全局当前项目
- **THEN** Server 返回错误提示用户切换项目

### Requirement: 项目与 GitHub repo 一一对应

每个项目 SHALL 对应一个 GitHub repo。

#### Scenario: 创建重复项目
- **WHEN** 用户尝试创建已存在的项目名称
- **THEN** Server 返回错误 "Project already exists"

#### Scenario: 关联重复 repo
- **WHEN** 用户尝试关联已被其他项目使用的 repo
- **THEN** Server 返回警告，但允许创建
