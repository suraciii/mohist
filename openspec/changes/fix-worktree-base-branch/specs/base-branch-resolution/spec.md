## ADDED Requirements

### Requirement: Base branch 通过多级探测解析

`detectBaseBranch()` SHALL 按以下优先级依次探测并返回第一个成功的结果：

1. `git symbolic-ref refs/remotes/origin/HEAD` → 提取分支名
2. `git rev-parse --verify origin/main` → 返回 `'main'`
3. `git rev-parse --verify origin/master` → 返回 `'master'`
4. `git rev-parse --abbrev-ref HEAD` → 当前分支名（非 detached HEAD 时）
5. 硬编码 `'main'` 作为最终 fallback

#### Scenario: origin/HEAD 指向 master

- **WHEN** 项目 `origin/HEAD` 指向 `refs/remotes/origin/master`
- **THEN** `detectBaseBranch()` 返回 `'master'`

#### Scenario: origin/HEAD 不存在但 origin/main 存在

- **WHEN** `git symbolic-ref refs/remotes/origin/HEAD` 失败
- **AND** `origin/main` remote-tracking ref 存在
- **THEN** `detectBaseBranch()` 返回 `'main'`

#### Scenario: origin/HEAD 和 origin/main 均不存在，但 origin/master 存在

- **WHEN** `origin/HEAD` 和 `origin/main` 均不存在
- **AND** `origin/master` remote-tracking ref 存在
- **THEN** `detectBaseBranch()` 返回 `'master'`

#### Scenario: 所有远程探测均失败，回退到 HEAD 分支

- **WHEN** `origin/HEAD`、`origin/main`、`origin/master` 均不存在
- **AND** 当前 HEAD 不处于 detached 状态
- **THEN** `detectBaseBranch()` 返回当前 HEAD 分支名

#### Scenario: 所有探测均失败，硬编码 fallback

- **WHEN** 项目不是 git 仓库或所有探测均失败
- **THEN** `detectBaseBranch()` 返回 `'main'`

### Requirement: Base branch 消费者使用项目存储值

所有需要 base branch 的模块 SHALL 使用 `project.baseBranch`（来自 DB）作为分支名来源，不独立检测。

#### Scenario: CLI diff 命令使用项目 baseBranch

- **WHEN** 用户执行 `mo issue diff <number>`
- **THEN** CLI 从 API 获取项目信息
- **AND** 使用 `project.baseBranch` 作为 `git diff` 的基准分支

#### Scenario: API diff 端点使用 origin/ 前缀

- **WHEN** API 的 diff 端点构造 `git diff` 命令
- **THEN** 使用 `origin/${project.baseBranch}` 作为基准分支引用
- **AND** 确保 diff 对比的是 remote-tracking ref 而非可能不存在的本地分支

#### Scenario: propose 端点传入项目 baseBranch

- **WHEN** propose 端点创建 worktree
- **THEN** 将 `project.baseBranch` 传入 `worktreeManager.create()`
