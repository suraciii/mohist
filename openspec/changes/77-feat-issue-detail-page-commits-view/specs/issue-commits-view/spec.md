## ADDED Requirements

### Requirement: Commits 标签页展示提交历史

Issue 详情页 Changed Files 区域 SHALL 提供 Files / Commits 标签页切换。Commits 标签页 SHALL 展示 worktree branch 相对 base branch 的 commit 列表，每个条目显示短 hash、commit message（首行）、相对时间和文件变更统计（+/-）。

#### Scenario: 有 commits 时显示列表

- **WHEN** 用户在 issue 详情页点击 "Commits" 标签
- **AND** 该 issue 的 worktree branch 有 commits
- **THEN** 显示 commit 列表，每行包含：短 hash（mono 字体）、message、相对时间、`+N` / `-N` 变更统计
- **AND** commits 按时间倒序排列

#### Scenario: 无 commits 时显示空状态

- **WHEN** 用户在 issue 详情页查看 Commits 标签
- **AND** 该 issue 无 worktree 或无 commits
- **THEN** 显示空状态提示 "No commits yet"

#### Scenario: Commits 标签页显示 commit 数量

- **WHEN** issue 详情页加载且处于可显示 diff 的 stage（Build/Review/Done）
- **THEN** Commits 标签页标题显示 commit 数量，如 "Commits (3)"

#### Scenario: 标签页状态保持

- **WHEN** 用户切换到 Commits 标签页
- **AND** 触发页面数据刷新（如 SSE 事件）
- **THEN** 标签页选择保持不变（不自动跳回 Files）

### Requirement: 单个 commit 可展开查看 diff

Commits 列表中每个 commit 条目 SHALL 可点击展开，展示该 commit 的完整 diff 内容。diff 内容 SHALL 使用语法高亮显示。

#### Scenario: 点击展开 commit diff

- **WHEN** 用户点击某个 commit 条目
- **THEN** 该条目下方展开显示 diff 内容
- **AND** diff 使用等宽字体和语法高亮（新增行绿色，删除行红色）

#### Scenario: 再次点击收起

- **WHEN** 用户再次点击已展开的 commit 条目
- **THEN** diff 区域收起

#### Scenario: 多个 commit 同时展开

- **WHEN** 用户依次点击两个不同的 commit 条目
- **THEN** 两个 commit 的 diff 同时展开显示

#### Scenario: diff 加载中

- **WHEN** 用户点击展开某个 commit
- **AND** diff 数据正在加载
- **THEN** 显示加载指示器

#### Scenario: diff 加载失败

- **WHEN** 用户点击展开某个 commit
- **AND** diff 请求返回错误
- **THEN** 显示错误提示 "Failed to load diff"
