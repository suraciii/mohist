## ADDED Requirements

### Requirement: Plan Stage 审批面板展示设计文档预览

当 issue 处于 Plan stage 的审批状态时，审批面板 SHALL 展示变更目录中的设计文档清单和概要，帮助用户评估设计方案是否合理。

#### Scenario: Plan stage 展示设计文档列表

- **WHEN** issue 处于审批状态且 `issue.stage` 为 `plan`
- **AND** `approvalState.output.stage` 为 `"plan"`
- **THEN** 审批面板展示以下文档清单（如果存在）：proposal.md、design.md、specs/ 目录下的 spec 文件、tasks.json
- **AND** 每个文档显示文件名，点击可展开查看内容

#### Scenario: Plan stage 无设计文档

- **WHEN** issue 处于 Plan stage 审批状态
- **AND** `approvalState.output` 中没有 `artifacts` 字段
- **THEN** 审批面板显示 self-review 结论（从 `approvalState.output.selfReviewNotes` 读取）
- **AND** 显示 fallback 提示 "Design artifacts not available for preview"

#### Scenario: Plan stage 自审查结论作为辅助参考

- **WHEN** Plan stage 审批面板展示设计文档
- **AND** `approvalState.output.selfReviewNotes` 存在
- **THEN** 面板在文档列表下方显示 "Self-Review Notes" 折叠区域
- **AND** 默认折叠，用户可点击展开查看

### Requirement: Review Stage 审批面板展示结构化审查结果

当 issue 处于 Review stage 的审批状态时，审批面板 SHALL 展示 review-summary-ui 组件（verdict badge + 维度网格 + 可展开报告）。

#### Scenario: Review stage 展示结构化审查

- **WHEN** issue 处于审批状态且 `issue.stage` 为 `review`
- **AND** `approvalState.output.stage` 为 `"review"`
- **THEN** 审批面板展示 Review Summary 组件（verdict badge + 维度状态网格 + 可展开完整报告）
- **AND** 不显示 Plan stage 的设计文档预览

#### Scenario: Review stage 引导查看代码变更

- **WHEN** issue 处于 Review stage 审批状态
- **THEN** 面板显示 "View Code Changes" 链接/按钮
- **AND** 点击后切换到 Issue 详情页的 Files/Commits tab

### Requirement: 审批面板动作按钮根据阶段和 Verdict 差异化

审批面板的动作按钮 SHALL 根据当前阶段和 verdict 提供不同的操作选项。

#### Scenario: Plan stage 动作按钮

- **WHEN** issue 处于 Plan stage 审批状态
- **THEN** 面板显示 "Approve & Build" 按钮
- **AND** 面板显示 "Send back with notes" 按钮（textarea + send）

#### Scenario: Review stage 且 Verdict PASS 动作按钮

- **WHEN** issue 处于 Review stage 审批状态
- **AND** `approvalState.output.verdict` 为 `"PASS"`
- **THEN** 面板显示 "Approve & Done" 按钮
- **AND** 面板不显示 "Send back for fixes" 按钮

#### Scenario: Review stage 且 Verdict FAIL 动作按钮

- **WHEN** issue 处于 Review stage 审批状态
- **AND** `approvalState.output.verdict` 为 `"FAIL"`
- **THEN** 面板显示 "Send back for fixes" 按钮（一键 reject + 自动带 review 报告）
- **AND** 面板显示 "Send back with instructions" 按钮（textarea + reject + 用户消息 + review 报告）
- **AND** 面板显示 "Force Approve" 按钮（需要二次确认）

#### Scenario: Review stage Verdict 未知时的动作按钮

- **WHEN** issue 处于 Review stage 审批状态
- **AND** `approvalState.output.verdict` 为 `null` 或 `undefined`
- **THEN** 面板显示 "Approve & Continue" 按钮
- **AND** 面板显示 "Send back with notes" 按钮
