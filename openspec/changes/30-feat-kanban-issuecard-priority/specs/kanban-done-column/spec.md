## ADDED Requirements

### Requirement: Done 列默认折叠

Kanban 看板的 Done 列 SHALL 默认只显示最近的 5 个 issue。当 Done 列中的 issue 数量超过 5 个时，SHALL 在列表底部显示 "N more" 按钮（N 为隐藏的 issue 数量）。

#### Scenario: Done 列 issue 少于等于 5 个

- **WHEN** Done 列包含 3 个 issue
- **THEN** 全部 3 个 issue 正常显示
- **AND** 不显示 "N more" 按钮

#### Scenario: Done 列 issue 超过 5 个

- **WHEN** Done 列包含 12 个 issue
- **THEN** 默认显示最近的 5 个 issue
- **AND** 底部显示 "7 more" 按钮

### Requirement: Done 列展开全部

用户点击 "N more" 按钮后，SHALL 展开 Done 列显示所有 issue，并将按钮文本切换为 "Show less"。

#### Scenario: 点击展开

- **WHEN** 用户点击 "7 more" 按钮
- **THEN** Done 列展开显示全部 12 个 issue
- **AND** 按钮文本变为 "Show less"

#### Scenario: 点击收起

- **WHEN** Done 列处于展开状态
- **AND** 用户点击 "Show less" 按钮
- **THEN** Done 列折叠回最近 5 个 issue
- **AND** 按钮文本变回 "7 more"

### Requirement: Done 列排序

Done 列中的 issue SHALL 按更新时间倒序排列（最近更新的在前），确保默认显示的 5 个是最近完成的 issue。

#### Scenario: 按 updatedAt 排序

- **WHEN** Done 列包含 issue A（updatedAt: 1天前）、issue B（updatedAt: 5天前）、issue C（updatedAt: 2天前）
- **THEN** 显示顺序为 A → C → B
