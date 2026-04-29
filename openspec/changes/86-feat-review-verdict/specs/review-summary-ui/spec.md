## ADDED Requirements

### Requirement: Review Summary 面板展示结构化 Verdict Badge

Review Summary 面板 SHALL 在顶部展示 verdict badge 作为最突出的视觉元素。Badge SHALL 显示 "PASS" (绿色) 或 "FAIL" (红色)，让用户一眼看到审查结论。

#### Scenario: Verdict 为 PASS 时显示绿色 badge

- **WHEN** `approvalState.output.verdict` 值为 `"PASS"`
- **THEN** Review Summary 面板顶部显示绿色 badge，文字为 "PASS"
- **AND** badge 为面板中视觉权重最高的元素

#### Scenario: Verdict 为 FAIL 时显示红色 badge

- **WHEN** `approvalState.output.verdict` 值为 `"FAIL"`
- **THEN** Review Summary 面板顶部显示红色 badge，文字为 "FAIL"
- **AND** badge 为面板中视觉权重最高的元素

#### Scenario: Verdict 为 null 或缺失时显示未知状态

- **WHEN** `approvalState.output.verdict` 为 `null` 或 `undefined`
- **THEN** Review Summary 面板显示灰色 badge，文字为 "REVIEW"
- **AND** 面板不显示维度状态网格

### Requirement: Review Summary 面板展示维度状态网格

Review Summary 面板 SHALL 在 verdict badge 下方展示各审查维度的通过/失败状态。维度 SHALL 从 `approvalState.output.dimensions` 数组读取。

#### Scenario: 维度状态网格展示

- **WHEN** `approvalState.output.dimensions` 数组存在且非空
- **THEN** 面板展示各维度名称，每个维度显示绿色 (PASS) 或红色 (FAIL) 状态标记
- **AND** 状态为 FAIL 的维度 SHALL 同时展示该维度的具体问题描述（从 `dimensions[i].issues` 读取）

#### Scenario: 维度数据缺失时跳过网格

- **WHEN** `approvalState.output.dimensions` 为 `undefined`、`null` 或空数组
- **THEN** 面板跳过维度状态网格，直接显示完整报告折叠区

### Requirement: Review Summary 面板提供可展开的完整报告

Review Summary 面板 SHALL 在维度网格下方提供 "View Full Report" 折叠区域，点击后展开显示完整 markdown 渲染的审查报告。报告内容 SHALL 从 `approvalState.output.reviewReport` 或 `approvalState.output.selfReviewNotes` 读取。

#### Scenario: 点击展开完整报告

- **WHEN** 用户点击 "View Full Report" 按钮
- **THEN** 展开区域显示完整审查报告，内容以 markdown 渲染（不是纯文本）
- **AND** 按钮文字变为 "Hide Full Report"

#### Scenario: 再次点击折叠报告

- **WHEN** 报告已展开，用户点击 "Hide Full Report"
- **THEN** 报告区域折叠隐藏
- **AND** 按钮文字恢复为 "View Full Report"

#### Scenario: 无报告内容时不显示展开按钮

- **WHEN** `approvalState.output` 中既没有 `reviewReport` 也没有 `selfReviewNotes` 且没有 comments
- **THEN** 不显示 "View Full Report" 按钮
