## ADDED Requirements

### Requirement: Result Banner 始终可见且为面板最醒目元素

Review 审批面板 SHALL 在顶部渲染一个 Result Banner，占据面板最突出的视觉位置。Banner 根据 `approvalState.output.result` 的值显示不同的颜色、图标和文字。

#### Scenario: Review result 为 PASS

- **WHEN** issue 处于 Review stage 审批状态
- **AND** `approvalState.output.result` 为 `"PASS"`（不区分大小写）
- **THEN** Banner 显示绿色背景、大号 checkmark 图标
- **AND** 文字为 "All checks passed"
- **AND** Banner 下方显示通过比例（如 "5/5 dimensions passed"），数据来自 `approvalState.output.dimensions` 中 status 为 PASS 的数量与总数之比

#### Scenario: Review result 为 FAIL

- **WHEN** issue 处于 Review stage 审批状态
- **AND** `approvalState.output.result` 为 `"FAIL"`（不区分大小写）
- **THEN** Banner 显示红色背景、大号 X 图标
- **AND** 文字为 "N issues found"（N 为 FAIL 维度中 issues 数组的总条目数）
- **AND** Banner 下方列出所有 FAIL 维度的名称，以 ` · ` 分隔（如 "Correctness · Tests · Spec"）

#### Scenario: Review result 未知或缺失

- **WHEN** issue 处于 Review stage 审批状态
- **AND** `approvalState.output.result` 不存在、为 `null`、或既非 `"PASS"` 也非 `"FAIL"`
- **THEN** Banner 显示灰色背景
- **AND** 文字为 "Review required"

### Requirement: Issue Summary 按 PASS/FAIL 状态分层展示维度

Banner 下方 SHALL 展示 Issue Summary 区域，对 FAIL 和 PASS 维度做视觉分层：FAIL 维度默认展开为独立卡片，PASS 维度折叠为一行摘要。

#### Scenario: FAIL 维度展开为问题卡片

- **WHEN** `approvalState.output.dimensions` 中存在 `status` 为 `"FAIL"` 的维度
- **THEN** 每个 FAIL 维度渲染为独立卡片
- **AND** 卡片标题包含红色圆点图标和维度名称（如 "🔴 Correctness"）
- **AND** 卡片内容列出该维度的 `issues` 数组中每个条目，以 bullet point 形式展示
- **AND** 卡片默认展开（不需要用户点击）

#### Scenario: PASS 维度折叠为一行摘要

- **WHEN** `approvalState.output.dimensions` 中存在 `status` 为 `"PASS"` 的维度
- **THEN** 所有 PASS 维度折叠为一行
- **AND** 显示绿色 checkmark 图标，后跟维度名称以 ` · ` 分隔（如 "✅ Complexity · Security"）

#### Scenario: dimensions 数据不可用

- **WHEN** `approvalState.output.dimensions` 不存在或为空数组
- **AND** `approvalState.output.result` 为 `"FAIL"`
- **THEN** Issue Summary 区域显示 "Issues found. View full report for details."
- **AND** 不渲染维度卡片

#### Scenario: dimensions 数据不可用且 result 为 PASS

- **WHEN** `approvalState.output.dimensions` 不存在或为空数组
- **AND** `approvalState.output.result` 为 `"PASS"`
- **THEN** Issue Summary 区域显示 "All checks passed"
- **AND** 不渲染维度卡片

### Requirement: Action Area 根据 result 提供差异化操作

Result Banner 和 Issue Summary 下方 SHALL 渲染 Action Area，按钮组合和样式根据 review result 动态变化。

#### Scenario: result 为 PASS 时的 Action Area

- **WHEN** `approvalState.output.result` 为 `"PASS"`
- **THEN** Action Area 显示单个绿色主按钮 "Approve & Done"
- **AND** 点击该按钮调用 `POST /api/issues/:number/approve`
- **AND** 按钮下方显示两个文字链接："View Report →" 和 "View Files →"
- **AND** "View Files →" 点击后滚动到 Issue 详情页的 Files/Commits tab 并切换到 files 视图

#### Scenario: result 为 FAIL 时的 Action Area

- **WHEN** `approvalState.output.result` 为 `"FAIL"`
- **THEN** Action Area 按以下顺序显示按钮：
  1. "Send back for fixes" — 红色主按钮
  2. "Add instructions..." — 可展开区域，内含 textarea 和 "Send with instructions" 按钮
  3. "Approve anyway" — 灰色次按钮
- **AND** 不显示双击确认机制
- **AND** 不使用 "Force Approve" 作为按钮文字

#### Scenario: result 未知时的 Action Area

- **WHEN** `approvalState.output.result` 不存在、为 `null`、或既非 `"PASS"` 也非 `"FAIL"`
- **THEN** Action Area 显示蓝色主按钮 "Approve & Continue"
- **AND** 显示 "Send back with notes..." 可展开区域，内含 textarea 和 "Send" 按钮

### Requirement: Send back for fixes 发送结构化问题摘要

点击 "Send back for fixes" 按钮 SHALL 通过 `POST /api/issues/:number/messages` 发送聚焦的问题摘要，而非完整 review 报告。

#### Scenario: dimensions 数据可用时发送问题摘要

- **WHEN** 用户点击 "Send back for fixes"
- **AND** `approvalState.output.dimensions` 存在且包含 FAIL 维度
- **THEN** 前端从 dimensions 中提取 `status` 为 `"FAIL"` 的维度
- **AND** 组装消息格式为 `### DimensionName\nissue1\nissue2`，各维度之间以双换行分隔
- **AND** 消息前缀为 "Please fix the following issues:\n\n"
- **AND** 通过 `POST /api/issues/:number/messages` 发送组装后的消息
- **AND** 按钮显示 loading 状态直到 API 响应

#### Scenario: dimensions 数据不可用时 fallback 到报告提取

- **WHEN** 用户点击 "Send back for fixes"
- **AND** `approvalState.output.dimensions` 不存在或为空
- **THEN** 前端从 `approvalState.output.reviewReport` 中尝试提取 "Fix Suggestions" 部分
- **AND** 如果提取成功，发送 "Please fix the following issues:\n\n" + 提取内容
- **AND** 如果提取失败，fallback 发送完整 `reviewReport`
- **AND** 通过 `POST /api/issues/:number/messages` 发送

#### Scenario: 无 reviewReport 时发送通用消息

- **WHEN** 用户点击 "Send back for fixes"
- **AND** `approvalState.output.dimensions` 不存在
- **AND** `approvalState.output.reviewReport` 也不存在或为空
- **THEN** 发送通用消息 "The review found issues that need to be addressed. Please review and fix all problems."
- **AND** 通过 `POST /api/issues/:number/messages` 发送

#### Scenario: API 调用失败时显示错误

- **WHEN** "Send back for fixes" 的 API 调用返回错误
- **THEN** Action Area 内显示错误消息
- **AND** 按钮恢复可点击状态
- **AND** 不清空或改变面板状态

### Requirement: Add instructions 可展开文本区域

"Add instructions..." SHALL 提供可展开的 textarea，允许用户在 send back 时附加自定义指令。

#### Scenario: 展开和收起 instructions 区域

- **WHEN** 用户点击 "Add instructions..." 文字链接
- **THEN** 展开显示 textarea（3 行高度）和 "Send with instructions" 按钮
- **AND** 再次点击收起 textarea 区域

#### Scenario: 发送 instructions 时附加问题摘要

- **WHEN** 用户在 textarea 中输入文本
- **AND** 点击 "Send with instructions" 按钮
- **THEN** 前端组装消息，包含用户输入的指令文本
- **AND** 如果 dimensions 数据可用，附加 FAIL 维度的问题摘要作为参考
- **AND** 通过 `POST /api/issues/:number/messages` 发送组合消息
- **AND** 发送成功后清空 textarea 并收起区域

#### Scenario: textarea 为空时禁用发送

- **WHEN** instructions textarea 为空或仅包含空白字符
- **THEN** "Send with instructions" 按钮处于 disabled 状态

### Requirement: Approve anyway 无需二次确认

"Approve anyway" 按钮 SHALL 允许用户单次点击即批准 FAIL 的 review，不要求双击确认或等待超时。

#### Scenario: 单击 Approve anyway 执行批准

- **WHEN** 用户点击 "Approve anyway" 按钮
- **THEN** 立即调用 `POST /api/issues/:number/approve`
- **AND** 按钮显示 loading 状态
- **AND** 不显示确认对话框或要求二次点击

#### Scenario: Approve anyway API 失败

- **WHEN** approve API 调用返回错误
- **THEN** Action Area 显示错误消息
- **AND** 按钮恢复可点击状态

### Requirement: Full Report 以 Modal 展示

点击 "View Report →" 链接 SHALL 打开 Modal 覆盖层展示完整 review 报告，而非在面板内展开。

#### Scenario: 打开 Full Report Modal

- **WHEN** 用户点击 "View Report →" 链接
- **THEN** 渲染全屏遮罩层 + 居中面板（宽度为视口的 80%，最大宽度不限）
- **AND** Modal 顶部显示 Result Badge（与 Result Banner 同色的精简版）
- **AND** Modal 主体以 Markdown 格式渲染 `approvalState.output.reviewReport` 的完整内容
- **AND** 遮罩层使用半透明黑色背景

#### Scenario: 关闭 Full Report Modal

- **WHEN** 用户点击 Modal 外部遮罩层、或点击 Modal 内的关闭按钮、或按 Escape 键
- **THEN** Modal 关闭
- **AND** 回到审批面板，面板状态（result banner、issue summary、action area）保持不变
- **AND** 上下文不丢失（textarea 内容、展开状态等保留）

#### Scenario: 无 reviewReport 时的 Modal

- **WHEN** 用户点击 "View Report →"
- **AND** `approvalState.output.reviewReport` 不存在或为空
- **THEN** Modal 显示 "No detailed report available"
- **AND** 如果存在 `approvalState.output.selfReviewNotes`，则在下方显示该内容

### Requirement: 审批面板提取为独立组件

Review 审批面板 SHALL 从 `IssueDetailPage.tsx` 中提取为独立的 `ReviewSummary` 和 `ReviewApprovalPanel` 组件。

#### Scenario: IssueDetailPage 使用 ReviewApprovalPanel

- **WHEN** issue 处于 Review stage 的审批状态（`approvalState.status === 'awaiting'` 且 `stage === 'review'`）
- **THEN** `IssueDetailPage` 渲染 `<ReviewApprovalPanel>` 组件
- **AND** 将 `approvalState.output` 作为 prop 传入
- **AND** 将 `issueNumber`、`onViewFiles` 回调作为 prop 传入
- **AND** 不再渲染旧的 Review Report 文本框（lines 781-791）和旧的 Approval Required 按钮（lines 794-889）和旧的 Send Message 区域（lines 891-926）

#### Scenario: Plan stage 审批不受影响

- **WHEN** issue 处于 Plan stage 的审批状态
- **THEN** 仍使用原有的审批面板 UI（"Approve & Continue" 按钮 + Send Message textarea）
- **AND** 不渲染 ReviewApprovalPanel

#### Scenario: ReviewApprovalPanel 组件内部结构

- **WHEN** `ReviewApprovalPanel` 渲染
- **THEN** 内部按顺序渲染 `<ReviewSummary>`（Result Banner + Issue Summary）和 Action Area
- **AND** `<ReviewSummary>` 接收 `output` prop（`approvalState.output`）
- **AND** Action Area 内部根据 `output.result` 值切换按钮组合
