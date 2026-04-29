## MODIFIED Requirements

### Requirement: Commits 标签页展示提交历史

Issue 详情页 Changes tab 内的 Commits 子视图 SHALL 展示 worktree branch 相对 base branch 的 commit 列表，每个条目显示短 hash、commit message（首行）、相对时间和文件变更统计（+/-）。噪音 commit（`chore(tasks)`、`WIP`、`chore: commit remaining`）SHALL 默认折叠在 "Auto commits (N)" 分组下。

#### Scenario: 有 commits 时显示列表

- **WHEN** 用户在 Changes tab 内切换到 Commits 子视图
- **AND** 该 issue 的 worktree branch 有 commits
- **THEN** 显示 commit 列表，每行包含：短 hash（mono 字体）、message、相对时间、`+N` / `-N` 变更统计
- **AND** commits 按时间倒序排列

#### Scenario: 无 commits 时显示空状态

- **WHEN** 用户在 Changes tab 内查看 Commits 子视图
- **AND** 该 issue 无 worktree 或无 commits
- **THEN** 显示空状态提示 "No commits yet"

#### Scenario: Commits 子视图显示 commit 数量

- **WHEN** issue 详情页加载且 Changes tab 可见
- **THEN** Commits 子视图按钮显示 commit 数量，如 "Commits (3)"

#### Scenario: 子视图状态保持

- **WHEN** 用户切换到 Commits 子视图
- **AND** 触发页面数据刷新（如 SSE 事件）
- **THEN** 子视图选择保持不变（不自动跳回 Files changed）

#### Scenario: 噪音 commit 默认折叠

- **WHEN** commit 列表包含 `chore(tasks)`、`WIP` 或 `chore: commit remaining` 前缀的 commit
- **THEN** 这些 commit 折叠在 "Auto commits (N)" 分组下
- **AND** 其余 commit 直接显示在列表中

#### Scenario: 展开噪音 commit 分组

- **WHEN** 用户点击 "Auto commits (N)" 分组
- **THEN** 展开显示所有被折叠的噪音 commit
- **AND** 每个噪音 commit 可进一步点击展开查看 diff

#### Scenario: 无噪音 commit 时不显示分组

- **WHEN** 所有 commit 都不匹配噪音模式
- **THEN** 不显示 "Auto commits" 分组
- **AND** 所有 commit 直接平铺在列表中

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
