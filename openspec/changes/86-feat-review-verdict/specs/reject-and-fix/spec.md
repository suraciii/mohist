## ADDED Requirements

### Requirement: Send back for fixes 一键打回

Review stage 审批面板 SHALL 提供 "Send back for fixes" 按钮，一键将 review 报告作为修复指令注入 agent session 并 resume agent。

#### Scenario: 点击 Send back for fixes

- **WHEN** 用户点击 "Send back for fixes" 按钮
- **THEN** 系统调用 `POST /api/issues/:number/messages`
- **AND** message body 包含格式化的修复指令，前缀为 "Review found issues that need fixing. Please address the following:\n\n"，后附完整 review 报告内容（从 `approvalState.output.reviewReport` 读取）
- **AND** agent 自动 resume 并开始新的 LLM loop 处理修复
- **AND** 按钮显示 loading 状态直到 API 响应

#### Scenario: Send back for fixes 但无 review report

- **WHEN** 用户点击 "Send back for fixes" 按钮
- **AND** `approvalState.output.reviewReport` 为空或不存在
- **THEN** 系统发送通用修复消息 "Review found issues that need fixing. Please review and fix all identified problems."
- **AND** agent 自动 resume

#### Scenario: Send back for fixes API 失败

- **WHEN** 用户点击 "Send back for fixes" 按钮
- **AND** API 返回错误
- **THEN** 按钮恢复可点击状态
- **AND** 面板显示错误提示信息

### Requirement: Send back with instructions 自定义指令打回

Review stage 审批面板 SHALL 提供 "Send back with instructions" 功能，允许用户输入自定义修复指令，与 review 报告一起发送给 agent。

#### Scenario: 输入自定义指令并发送

- **WHEN** 用户在 "Send back with instructions" 文本框中输入 "Fix the SQL injection in login.ts"
- **AND** 点击发送按钮
- **THEN** 系统调用 `POST /api/issues/:number/messages`
- **AND** message body 包含用户指令，格式为 "User feedback:\n{user message}\n\nReview report for reference:\n{review report}"
- **AND** agent 自动 resume 并基于用户指令 + review 报告进行修复

#### Scenario: 指令为空时禁用发送

- **WHEN** "Send back with instructions" 文本框为空或仅含空白
- **THEN** 发送按钮处于 disabled 状态
- **AND** 不发送 API 请求

### Requirement: Force Approve 需要二次确认

Review stage 审批面板的 "Force Approve" 按钮 SHALL 需要二次确认才能执行，防止用户误操作。

#### Scenario: 第一次点击 Force Approve

- **WHEN** 用户点击 "Force Approve" 按钮
- **THEN** 按钮文字变为 "Confirm Force Approve"
- **AND** 按钮样式变为更醒目的警告样式
- **AND** 不执行 approve 操作

#### Scenario: 二次确认 Force Approve

- **WHEN** 按钮已变为 "Confirm Force Approve" 状态
- **AND** 用户在 3 秒内再次点击
- **THEN** 执行 approve 操作（调用 approve API）
- **AND** 按钮恢复初始状态

#### Scenario: 二次确认超时

- **WHEN** 按钮已变为 "Confirm Force Approve" 状态
- **AND** 用户 3 秒内未点击
- **THEN** 按钮自动恢复为初始 "Force Approve" 状态

### Requirement: Plan stage Send back with notes

Plan stage 审批面板 SHALL 提供 "Send back with notes" 功能，允许用户输入反馈意见并发送给 agent 重新规划。

#### Scenario: Plan stage 输入反馈并发送

- **WHEN** 用户在 Plan stage 审批面板的文本框中输入反馈
- **AND** 点击发送按钮
- **THEN** 系统调用 `POST /api/issues/:number/messages`
- **AND** message body 包含用户反馈，前缀为 "User feedback on plan:\n"
- **AND** agent 自动 resume 并基于用户反馈重新规划
