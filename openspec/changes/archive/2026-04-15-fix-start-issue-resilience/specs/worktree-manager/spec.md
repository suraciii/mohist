## MODIFIED Requirements

### Requirement: WorktreeManager 管理每个 Issue 的隔离工作区

WorktreeManager SHALL 为每个 Issue 创建独立的 git worktree 作为隔离工作区，基于项目的 `baseBranch` 对应的远程分支创建。

#### Scenario: 创建 worktree

- **WHEN** 用户执行 `mo issue start <number>`
- **THEN** 系统执行智能 fetch（30 分钟内 fetch 过则跳过）
- **AND** 系统在 `~/.mohist/projects/{projectName}/worktrees/issue-{N}/` 创建 git worktree
- **AND** 创建对应的分支 `mo/issue-{N}`，基于 `origin/<baseBranch>`（baseBranch 为项目配置的主干分支）
- **AND** 返回 worktree 路径供 Agent 使用
- **AND** `{projectName}` 经过 slug 处理（小写、空格转 `-`、去除特殊字符）

#### Scenario: origin/<baseBranch> 不存在时回退到本地分支

- **WHEN** 用户执行 `mo issue start <number>`
- **AND** `origin/<baseBranch>` 不存在（如分支被删除或配置错误）
- **AND** 本地存在 `<baseBranch>` 分支
- **THEN** 系统基于本地 `<baseBranch>` 分支创建 worktree
- **AND** 记录警告日志："Using local branch '<baseBranch>', remote not found"

#### Scenario: origin/<baseBranch> 和本地分支都不存在

- **WHEN** 用户执行 `mo issue start <number>`
- **AND** `origin/<baseBranch>` 不存在
- **AND** 本地 `<baseBranch>` 分支也不存在
- **THEN** 系统返回错误："Branch '<baseBranch>' not found locally or on origin"
- **AND** 不创建 worktree

#### Scenario: worktree 已存在

- **WHEN** 用户执行 `mo issue start <number>`
- **AND** `~/.mohist/projects/{projectName}/worktrees/issue-{N}/` 已存在
- **THEN** 系统复用现有 worktree
- **AND** 不创建新分支

#### Scenario: 项目非 git 仓库

- **WHEN** 用户执行 `mo issue start <number>`
- **AND** 项目不是 git 仓库
- **THEN** 系统返回错误提示 "Project is not a git repository"
- **AND** 不创建 worktree

#### Scenario: 无 origin remote 时回退到本地 baseBranch

- **WHEN** 用户执行 `mo issue start <number>`
- **AND** 项目没有 origin remote（纯本地仓库）
- **AND** 本地存在 `<baseBranch>` 分支
- **THEN** 系统基于本地 `<baseBranch>` 分支创建 worktree
- **AND** 如果本地 `<baseBranch>` 分支不存在，返回错误

#### Scenario: smartFetch 网络失败时重试

- **WHEN** 智能 fetch 执行 `git fetch origin`
- **AND** fetch 因网络错误失败（如 TLS 握手失败、连接超时）
- **THEN** 系统最多重试 2 次（共 3 次尝试），每次间隔递增（1s、2s）
- **AND** 如果重试成功，正常写入 fetch 缓存时间戳
- **AND** 继续创建 worktree

#### Scenario: smartFetch 全部重试失败后降级

- **WHEN** 智能 fetch 的 3 次尝试全部失败
- **THEN** 系统记录警告日志（包含错误信息）
- **AND** 不写入 fetch 缓存时间戳（下次 start 会再次尝试 fetch）
- **AND** 继续执行后续 worktree 创建流程（使用本地已有的 remote-tracking ref）
- **AND** 如果本地也不存在对应 remote-tracking ref，由后续分支检查逻辑返回清晰错误
