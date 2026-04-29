## ADDED Requirements

### Requirement: 手动触发 skill 执行

系统 SHALL 提供 `run(skillName, projectId)` 方法，接受 skill 名称和项目 ID，在项目的 worktree 中启动一次 ACP session 执行该 skill 的 prompt。

#### Scenario: 成功触发执行

- **WHEN** 调用 `run("analyze-codebase", projectId)`
- **AND** skill `analyze-codebase` 存在
- **THEN** 系统创建一条 `skill_runs` 记录，status=`running`
- **AND** EventBus emit `skill_started` 事件
- **AND** 使用 skill 的 prompt 调用 `runAcpSession()`，cwd 为项目路径
- **AND** 返回 skill run 记录（含 `id`）

#### Scenario: skill 不存在

- **WHEN** 调用 `run("nonexistent", projectId)`
- **AND** 数据库中无该名称的 skill
- **THEN** 抛出错误 "Skill not found: nonexistent"

### Requirement: Skill 执行使用 ACP session

系统 SHALL 复用现有 `runAcpSession()` 执行 skill prompt。传入参数：`cwd` 为项目根路径，`task` 为 skill 的 prompt，`eventBus` 用于事件推送，`timeout` 默认 30 分钟。

#### Scenario: ACP session 成功完成

- **WHEN** skill 的 ACP session 正常结束
- **THEN** `skill_runs` 记录的 status 更新为 `completed`
- **AND** `output` 字段存储 ACP 返回的 text
- **AND** EventBus emit `skill_completed` 事件

#### Scenario: ACP session 超时

- **WHEN** skill 的 ACP session 超过 timeout
- **THEN** `skill_runs` 记录的 status 更新为 `failed`
- **AND** `error` 字段存储超时错误信息
- **AND** EventBus emit `skill_failed` 事件

#### Scenario: ACP session 执行失败

- **WHEN** `runAcpSession()` 返回 `success: false`
- **THEN** `skill_runs` 记录的 status 更新为 `failed`
- **AND** `error` 字段存储 ACP 返回的 error 信息
- **AND** EventBus emit `skill_failed` 事件

### Requirement: Skill 执行完成后创建 Issue

skill 执行成功后（ACP 返回 `success: true`），系统 SHALL 从 ACP 输出中提取 title 和 body，调用 `IssueService.create()` 创建一个 Issue（stage=`backlog`，status=`active`），使 Issue 进入 Pipeline 供用户审查。

#### Scenario: ACP 输出包含有效内容

- **WHEN** skill 执行成功，ACP text 非空
- **THEN** 系统使用 ACP text 的第一行（去除 `#` 前缀）作为 Issue title
- **AND** 使用完整 ACP text 作为 Issue body
- **AND** Issue stage 为 `backlog`
- **AND** Issue labels 包含 `skill-generated`

#### Scenario: ACP 输出为空

- **WHEN** skill 执行成功但 ACP text 为空
- **THEN** 系统创建 Issue，title 为 `Skill result: <skill-name>`
- **AND** body 为 `Executed skill <skill-name> with no output.`

#### Scenario: Issue 创建失败不影响 run 记录

- **WHEN** IssueService.create() 抛出异常
- **THEN** skill run 记录 status 仍更新为 `completed`
- **AND** `error` 字段记录 Issue 创建失败的错误信息

### Requirement: Skill run 记录存储执行历史

系统 SHALL 在 SQLite `skill_runs` 表中存储每次执行记录，字段包含：`id`（主键）、`skill_id`（外键）、`project_id`、`status`（`running`/`completed`/`failed`）、`output`（text，可为 NULL）、`error`（text，可为 NULL）、`issue_id`（创建的 Issue ID，可为 NULL）、`started_at`、`completed_at`（可为 NULL）。

#### Scenario: 执行开始时创建记录

- **WHEN** skill 执行被触发
- **THEN** 插入一条 `skill_runs` 记录，status=`running`，started_at 为当前时间

#### Scenario: 执行完成时更新记录

- **WHEN** skill 执行完成（成功或失败）
- **THEN** 更新 status 为 `completed` 或 `failed`
- **AND** 更新 output/error/issue_id/completed_at 字段

### Requirement: 查询 skill 执行历史

系统 SHALL 提供 `getRuns(skillId)` 方法，返回指定 skill 的所有执行记录，按 `started_at` 降序排列。

#### Scenario: 查询有历史的 skill

- **WHEN** 调用 `getRuns(skillId)`
- **AND** 该 skill 有 3 次执行记录
- **THEN** 返回 3 条记录，最新执行的排在最前

#### Scenario: 查询无历史的 skill

- **WHEN** 调用 `getRuns(skillId)`
- **AND** 该 skill 从未执行过
- **THEN** 返回空数组

### Requirement: Skill 执行异步运行不阻塞 API

`run()` 方法 SHALL 立即返回 skill run 记录（含 `id`），ACP session 在后台异步执行。API 调用者不需要等待执行完成。

#### Scenario: 触发后立即返回

- **WHEN** 调用 `run("analyze-codebase", projectId)`
- **THEN** 方法立即返回 skill run 记录（status=`running`）
- **AND** ACP session 在后台继续执行
