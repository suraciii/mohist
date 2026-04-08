## ADDED Requirements

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
