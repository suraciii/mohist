## ADDED Requirements

### Requirement: Issue 详情页 Changed Files 区域支持标签页切换

Issue 详情页的 Changed Files 区域 SHALL 提供 Files / Commits 标签页切换布局。Files 标签页显示原有的文件变更汇总，Commits 标签页显示 commit 历史。该区域仅在 Build、Review、Done 阶段显示。

#### Scenario: Changed Files 区域显示标签页

- **WHEN** 用户查看处于 Build/Review/Done 阶段的 issue 详情页
- **AND** 存在文件变更或 commits
- **THEN** Changed Files 区域顶部显示两个标签："Files" 和 "Commits (N)"
- **AND** 默认选中 "Files" 标签，显示原有文件变更列表

#### Scenario: 切换到 Commits 标签

- **WHEN** 用户点击 "Commits" 标签
- **THEN** 文件变更列表隐藏
- **AND** 显示 commits 列表视图

#### Scenario: 切换回 Files 标签

- **WHEN** 用户在 Commits 标签页点击 "Files" 标签
- **THEN** commits 列表隐藏
- **AND** 显示原有文件变更列表

#### Scenario: 无变更时区域隐藏

- **WHEN** issue 处于 Build/Review/Done 阶段
- **AND** 无文件变更且无 commits
- **THEN** Changed Files 区域整体隐藏
