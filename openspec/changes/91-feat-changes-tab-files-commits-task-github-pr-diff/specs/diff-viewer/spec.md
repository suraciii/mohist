## ADDED Requirements

### Requirement: DiffViewer 解析并渲染 unified diff

DiffViewer 组件 SHALL 接收 unified diff 字符串，解析为行级数组，并渲染为逐行高亮的 diff 视图。每行 SHALL 根据类型着色：新增行（`+`）绿色背景、删除行（`-`）红色背景、上下文行灰色/默认色。行号 SHALL 显示原始文件和新文件的双列行号。

#### Scenario: 渲染包含新增和删除行的 diff

- **WHEN** DiffViewer 接收包含 `+` 和 `-` 行的 unified diff 字符串
- **THEN** 新增行显示绿色背景，左侧显示新文件行号
- **AND** 删除行显示红色背景，左侧显示原始文件行号
- **AND** 上下文行显示默认背景，左侧同时显示原始和新文件行号

#### Scenario: 渲染多文件 diff

- **WHEN** DiffViewer 接收包含多个文件变更的 unified diff
- **THEN** 按文件分块渲染，每个文件块前显示 `--- a/...` 和 `+++ b/...` 头部
- **AND** 每个文件块内独立渲染 hunk 头（`@@ ... @@`）

#### Scenario: 处理空 diff

- **WHEN** DiffViewer 接收空字符串
- **THEN** 不渲染任何内容

### Requirement: DiffViewer 处理二进制文件

DiffViewer SHALL 识别二进制文件 diff（`Binary files ... differ` 或无 hunk 内容）并显示占位提示。

#### Scenario: 二进制文件显示占位

- **WHEN** DiffViewer 接收的文件条目标记为二进制（`Binary files ... differ`）
- **THEN** 显示 "Binary file, no diff available" 文本
- **AND** 不尝试解析行级 diff

### Requirement: DiffViewer 支持折叠展开

DiffViewer 在文件级别 SHALL 支持折叠/展开。默认状态下 diff 内容 SHALL 折叠，仅显示文件名和变更统计。

#### Scenario: 默认折叠文件 diff

- **WHEN** DiffViewer 渲染多文件 diff
- **THEN** 每个文件默认只显示文件路径和 `+N` `-N` 统计
- **AND** diff 内容隐藏

#### Scenario: 点击展开文件 diff

- **WHEN** 用户点击某个折叠的文件条目
- **THEN** 该文件的 diff 内容展开显示
- **AND** 其他文件不受影响

#### Scenario: 点击收起文件 diff

- **WHEN** 用户再次点击已展开的文件条目
- **THEN** 该文件的 diff 内容收起
