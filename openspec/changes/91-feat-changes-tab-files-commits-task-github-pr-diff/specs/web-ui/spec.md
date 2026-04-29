## MODIFIED Requirements

### Requirement: Web UI Issue Detail page Changes tab 替代 Files/Commits tabs

Issue 详情页 SHALL 将 Files 和 Commits 两个独立 tab 合并为单个 Changes tab。Changes tab 内提供两个子视图切换：Files changed（默认）和 Commits。原 Files 和 Commits tab 入口 SHALL 被移除。

#### Scenario: Changes tab 替代原有两个 tab

- **WHEN** 用户查看 issue 详情页
- **THEN** 不显示独立的 "Files" 和 "Commits" tab
- **AND** 显示单个 "Changes" tab

#### Scenario: Changes tab 默认展示 Files changed

- **WHEN** 用户点击 Changes tab
- **THEN** 默认展示 Files changed 子视图
- **AND** Files changed 子视图使用 DiffViewer 组件渲染 inline diff

#### Scenario: Changes tab 内切换到 Commits

- **WHEN** 用户在 Changes tab 内点击 Commits 子视图按钮
- **THEN** 展示 Commits 列表（复用 issue-commits-view 组件，增加噪音过滤）
