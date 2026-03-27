## ADDED Requirements

### Requirement: 本地合并通过 CLI 端执行

本地合并 SHALL 由 CLI 在用户主工作区中执行 git merge，成功后再调 Server API 做状态转换。

#### Scenario: 用户批准合并

- **WHEN** 用户对 waiting-review 状态的 Issue 执行 `mo issue approve <number>`
- **THEN** CLI 先调用 `GET /api/issues/:number` 获取 Issue 详情（含 `projectPath`）
- **AND** CLI 在 `projectPath`（主工作区）执行 `git merge --no-ff mo/issue-{N}`
- **AND** 合并成功后 CLI 调用 Server API 将 Issue 状态转换为 Done
- **AND** 合并成功后 CLI 调用 `POST /api/issues/:number/cleanup` 让 Server 清理 worktree 和分支

#### Scenario: 合并有冲突

- **WHEN** git merge 遇到冲突
- **THEN** CLI 显示冲突信息和解决建议
- **AND** 不调用 Server API，Issue 状态保持 waiting-review
- **AND** worktree 保留，不清理

#### Scenario: 非 waiting-review 状态批准

- **WHEN** 用户对非 waiting-review 状态的 Issue 执行 `mo issue approve <number>`
- **THEN** 不执行 git merge
- **AND** 按原有逻辑调 Server API 处理状态转换

### Requirement: 用户可以查看 Issue 的代码变更

系统 SHALL 提供 diff 命令查看 Issue 分支相对主分支的变更。

#### Scenario: 查看变更

- **WHEN** 用户执行 `mo issue diff <number>`
- **THEN** CLI 调用 `GET /api/issues/:number` 获取 `projectPath`
- **AND** 在 `projectPath` 执行 `git diff main...mo/issue-{N}` 显示分支差异
- **AND** 输出格式为标准 git diff

#### Scenario: worktree 不存在

- **WHEN** 用户执行 `mo issue diff <number>`
- **AND** 对应的 `mo/issue-{N}` 分支不存在
- **THEN** 系统返回错误 "No worktree found for issue #{N}"

### Requirement: 用户可以查看 Agent 执行日志

系统 SHALL 提供日志命令查看 Issue 的 Agent 执行日志。

#### Scenario: 查看日志

- **WHEN** 用户执行 `mo issue logs <number>`
- **THEN** 系统显示 `~/.mohist/projects/{projectName}/logs/issue-{N}/` 目录下的日志内容
- **AND** 默认显示最后 50 行

#### Scenario: 实时跟踪日志

- **WHEN** 用户执行 `mo issue logs <number> --follow`
- **THEN** 系统持续输出新增日志，直到用户中断

#### Scenario: 无日志文件

- **WHEN** 用户执行 `mo issue logs <number>`
- **AND** 对应的日志目录不存在
- **THEN** 系统返回 "No logs found for issue #{N}"
