## MODIFIED Requirements

### Requirement: 前端 ApprovalState 类型包含 output 字段

前端 `ApprovalState` 接口 SHALL 包含 `output` 字段，类型为以下结构化对象：

```typescript
interface ApprovalOutput {
  stage: string;
  issueNumber: number;
  selfReviewNotes?: string;
  reviewReport?: string;
  verdict?: 'PASS' | 'FAIL' | null;
  dimensions?: Array<{
    name: string;
    status: 'PASS' | 'FAIL';
    issues?: string[];
  }>;
  artifacts?: Array<{
    name: string;
    path: string;
    content?: string;
  }>;
}
```

与后端 `ApprovalState.output` 对齐。

#### Scenario: approvalState.output 从 API 响应传递到组件

- **WHEN** API 返回 issue 数据，其中 `approvalState.output` 包含 `{ verdict: "FAIL", dimensions: [...], reviewReport: "..." }`
- **THEN** 前端 `ApprovalState` 类型正确接收 `output` 字段
- **AND** 组件可通过 `issue.approvalState.output.verdict` 访问审查结论
- **AND** 组件可通过 `issue.approvalState.output.dimensions` 访问维度状态

#### Scenario: approvalState 无 output

- **WHEN** API 返回 issue 数据，`approvalState.output` 为 `null` 或 `undefined`
- **THEN** `issue.approvalState.output` 为 falsy
- **AND** 审查面板 fallback 到 comments 显示

### Requirement: 审批面板展示 approvalState.output 审查报告

审批面板 SHALL 从 `approvalState.output` 读取审查内容并展示给用户。展示方式 SHALL 根据 `output.stage` 区分：

- **Plan stage**: 展示 self-review 结论和设计文档预览（参见 `stage-differentiated-approval` spec）
- **Review stage**: 展示结构化 Review Summary（verdict badge + 维度网格 + 可展开报告，参见 `review-summary-ui` spec）

当 `approvalState.output` 不可用时，SHALL fallback 到 `lastAgentComment.body`（comments）。

#### Scenario: approvalState.output 包含 review stage 结构化数据

- **WHEN** issue 处于审批状态（`isApprovalGate` 为 true）
- **AND** `approvalState.output.stage` 为 `"review"`
- **THEN** 审批面板展示 Review Summary 组件（verdict badge + 维度状态网格 + 可展开完整报告）

#### Scenario: approvalState.output 包含 plan stage 数据

- **WHEN** issue 处于审批状态
- **AND** `approvalState.output.stage` 为 `"plan"`
- **THEN** 审批面板展示设计文档预览和 self-review 结论

#### Scenario: approvalState.output 为空但有 comments

- **WHEN** issue 处于审批状态
- **AND** `approvalState.output` 为空或 undefined
- **AND** issue 有 comments
- **THEN** 审批面板展示最新 agent comment 的 body 作为报告内容（纯文本 fallback）

#### Scenario: approvalState.output 和 comments 均为空

- **WHEN** issue 处于审批状态
- **AND** `approvalState.output` 为空
- **AND** issue 没有 comments
- **THEN** 审批面板只显示审批动作按钮，不显示报告区域

### Requirement: Approve 按钮不依赖 comments

Approve 按钮的显示条件 SHALL 只依赖 `isApprovalGate`（issue 处于审批阶段 + status 为 active + agent 未在该 issue 上运行），不依赖 `lastAgentComment` 或 comments 的存在。

#### Scenario: 审批状态但无 comments 时显示审批按钮

- **WHEN** issue 的 `approvalState.status` 为 `awaiting`
- **AND** issue 没有 comments
- **THEN** 审批面板显示阶段对应的审批动作按钮（参见 `stage-differentiated-approval` spec）

#### Scenario: 审批状态且有 comments 时显示审批按钮

- **WHEN** issue 的 `approvalState.status` 为 `awaiting`
- **AND** issue 有 comments
- **THEN** 审批面板显示阶段对应的审批动作按钮
- **AND** 审查报告区域展示报告内容
