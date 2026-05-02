## MODIFIED Requirements

### Requirement: Changes tab 合并 Files 和 Commits 视图

Issue 详情页 SHALL 将 Files 和 Commits 两个 tab 合并为单个 **Changes** section，内含两个子视图切换按钮：**Files changed**（默认）和 **Commits**。该 section SHALL 在所有 workflow stages 中显示，不受 `DIFF_STAGES` 限制。

#### Scenario: 默认显示 Files changed 子视图

- **WHEN** 用户打开任意 stage 的 issue 详情页
- **AND** issue 有文件变更
- **THEN** Changes section 默认显示 Files changed 子视图
- **AND** 子视图切换按钮高亮 "Files changed"

#### Scenario: 切换到 Commits 子视图

- **WHEN** 用户在 Changes section 内点击 "Commits" 子视图按钮
- **THEN** 显示 Commits 列表视图
- **AND** 子视图切换按钮高亮 "Commits"

#### Scenario: 子视图状态在刷新后保持

- **WHEN** 用户已切换到 Commits 子视图
- **AND** 页面数据刷新（如 SSE 事件触发）
- **THEN** 仍然停留在 Commits 子视图

#### Scenario: Backlog stage 显示空状态

- **WHEN** issue 处于 Backlog stage
- **THEN** Changes section 显示 "No changes yet" 空状态
- **AND** 不显示 Files/Commits 子视图切换按钮

#### Scenario: Explore stage 显示变更

- **WHEN** issue 处于 Explore stage
- **AND** agent 已创建或修改文件
- **THEN** Changes section 显示完整的 Files/Commits 子视图
- **AND** 功能与 Plan/Build/Check/Done stage 相同

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
- **THEN** Changes section 显示 "No changes yet" 空状态提示

#### Scenario: 二进制文件显示占位

- **WHEN** Files changed 视图中包含二进制文件变更
- **THEN** 该文件条目显示 "Binary file, no diff available"
- **AND** 不展示 inline diff
