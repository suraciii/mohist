## ADDED Requirements

### Requirement: Changes tab 合并 Files 和 Commits 视图

Issue 详情页 SHALL 将 Files 和 Commits 两个 tab 合并为单个 **Changes** tab，内含两个子视图切换按钮：**Files changed**（默认）和 **Commits**。

#### Scenario: 默认显示 Files changed 子视图

- **WHEN** 用户在 issue 详情页点击 Changes tab
- **THEN** 默认显示 Files changed 子视图
- **AND** 子视图切换按钮高亮 "Files changed"

#### Scenario: 切换到 Commits 子视图

- **WHEN** 用户在 Changes tab 内点击 "Commits" 子视图按钮
- **THEN** 显示 Commits 列表视图
- **AND** 子视图切换按钮高亮 "Commits"

#### Scenario: 子视图状态在刷新后保持

- **WHEN** 用户已切换到 Commits 子视图
- **AND** 页面数据刷新（如 SSE 事件触发）
- **THEN** 仍然停留在 Commits 子视图

### Requirement: Files changed 视图展示逐文件 diff

Files changed 子视图 SHALL 展示 issue 分支相对 base 分支的完整 diff，顶部显示总览统计（总文件数、总 additions、总 deletions），下方逐文件列出变更，每个文件可展开查看 inline diff。

#### Scenario: 显示总览统计

- **WHEN** issue 有文件变更
- **THEN** Files changed 视图顶部显示 "N files changed · +M -D" 格式的统计
- **AND** M 和 D 使用 `git diff --numstat` 的精确计数

#### Scenario: 逐文件列表展示

- **WHEN** issue 有 3 个文件变更
- **THEN** 显示 3 个文件条目，每个包含文件路径和 `+N` `-N` 统计
- **AND** 文件默认折叠，仅显示路径和统计

#### Scenario: 点击展开文件 inline diff

- **WHEN** 用户点击某个文件条目
- **THEN** 该文件下方展开 inline diff 内容
- **AND** diff 使用标准绿（addition）/红（deletion）/灰（context）行级高亮

#### Scenario: 无 worktree 时显示空状态

- **WHEN** issue 无 worktree
- **THEN** Changes tab 显示 "No changes yet" 空状态提示

#### Scenario: 二进制文件显示占位

- **WHEN** Files changed 视图中包含二进制文件变更
- **THEN** 该文件条目显示 "Binary file, no diff available"
- **AND** 不展示 inline diff

### Requirement: Files changed 视图支持文件路径搜索

Files changed 子视图 SHALL 提供文件路径搜索/过滤输入框，允许用户按路径关键词过滤文件列表。

#### Scenario: 搜索过滤文件

- **WHEN** 用户在搜索框输入 "workflow"
- **THEN** 文件列表只显示路径中包含 "workflow" 的文件
- **AND** 总览统计不变（显示全部文件的统计）

#### Scenario: 清空搜索恢复全部文件

- **WHEN** 用户清空搜索框
- **THEN** 文件列表恢复显示所有变更文件

### Requirement: Commits 子视图展示 commit 列表

Commits 子视图 SHALL 展示 issue 分支上的 commit 列表，每个 commit 显示短 hash、message、相对时间和变更统计。

#### Scenario: 显示 commit 列表

- **WHEN** issue 的 worktree branch 有 commits
- **THEN** 显示 commit 列表，每行包含短 hash、message、相对时间、`+N` `-N` 变更统计
- **AND** commits 按时间倒序排列

#### Scenario: 点击 commit 展开查看 diff

- **WHEN** 用户点击某个 commit 条目
- **THEN** 该条目下方展开显示该 commit 的 diff 内容
- **AND** diff 使用语法高亮（新增行绿色，删除行红色）

### Requirement: Commits 视图自动折叠噪音 commit

Commits 子视图 SHALL 识别噪音 commit（`chore(tasks)`、`WIP`、`chore: commit remaining` 等前缀），将其默认折叠在 "Auto commits (N)" 分组下，用户可展开查看。

#### Scenario: 噪音 commit 默认折叠

- **WHEN** commit 列表中有 3 个 `chore(tasks)` commit 和 2 个功能 commit
- **THEN** 功能 commit 直接显示在列表中
- **AND** `chore(tasks)` commit 折叠在 "Auto commits (3)" 分组下

#### Scenario: 展开噪音 commit 分组

- **WHEN** 用户点击 "Auto commits (3)" 分组
- **THEN** 展开显示所有噪音 commit
- **AND** 每个噪音 commit 可进一步点击展开查看 diff

#### Scenario: 无噪音 commit 时不显示分组

- **WHEN** 所有 commit 都不是噪音 commit
- **THEN** 不显示 "Auto commits" 分组
- **AND** 所有 commit 直接显示在列表中
