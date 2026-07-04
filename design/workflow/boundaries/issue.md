# 边界：Workflow ↔ Issue（Profile 归属与依赖方向）

本文是 [`context-map.md`](../../context-map.md) 关系 #1（Workflow↔Issue）的具体展开，聚焦：**依赖怎么走、哪些东西真的放错了**。profile 的内容/合并/加载见 [`profile.md`](../profile.md)。

> 相关但不重叠：调度见 [`scheduling.md`](../scheduling.md)、任务派发见 [`task-dispatch.md`](../task-dispatch.md)、协调见 [`issue-coordination.md`](../issue-coordination.md)。

## 目标

**单向依赖 `Issue → Workflow`，Workflow 不依赖 Issue。** Workflow 引擎不知道"issue"——只操作抽象的 workflow run、只消费解析后的 `WorkflowDefinition` + 变量。

## 现状：大部分已经对了

- **Workflow 领域模型纯净**：`Workflow/Domain/` 零 Issue 引用。`WorkflowDefinition`（结构类型）+ 引擎都在 Workflow。
- **workflow profile 留在 Issue/Project 是对的**：profile = template 选择 + variables，本就是各上下文自己的配置数据，不是 Workflow 的领域。

**唯一的真问题**：默认 `WorkflowDefinition` 的**内容**（`MohistWorkflow` + `mohist-local.workflow.yaml`）错放在 `Issue/Services/WorkflowProfiles/`，导致反向依赖。

## 归属判定

| 概念 | 是什么 | 归属 | 现状 |
|---|---|---|---|
| `WorkflowDefinition` | 结构类型（stages/tasks/checks）+ 引擎 | **Workflow** | ✓ 已在 `Workflow/Domain/Definition/` |
| workflow profile | template 选择 + variables（per project/issue 配置） | **Issue / Project 自己** | ✓ 留原处（本就是它们的配置） |
| prompts | project 级 prompt 库（内置 .prompt 兜底） | **Project Space**（project-scoped） | 见 [`prompt-management.md`](../../prompt-management.md)，不属 profile |
| 默认 `WorkflowDefinition` 内容 | 出厂默认工作流（yaml） | **应用配置层**（composition root） | ❌ 错放在 Issue，要搬 |
| 投影 | Issue 解读 workflow 运行状态（attention 等） | **Issue**（只读消费） | ✓ 留 Issue |

## 反向依赖与修复

`Workflow/Services/ProjectWorkflowProfileManager.cs` 引 `Issue.Services.WorkflowProfiles`，**只为拿 `MohistWorkflow.Definition`**。把默认定义挪到应用配置层，这条反向引用即消失 → 双向变单向 `Issue→Workflow` ✓。

（另外两个 Manager 的现状：`IssueWorkflowProfileManager` 不引用 Issue 类型，干净。`WorkflowProfileManager` 引用了 `IssueStore.Deserialize`（`Infrastructure.Data.Issue`）和 `IssueWorkflowProfile` 行类型（`Issue.Services.WorkflowProfiles`）——但读的是 issue 级 workflow profile 配置（profile id、template、variables），这按上文归属判定本就归 Issue，属配置层可接受越界，不触碰 WorkflowRun 决策，不造成领域污染。）

## 现状偏差（迁移项）

本文是目标态。需要收敛的偏差，范围比一开始以为的窄：

- **搬（唯一跨上下文的搬迁）**：`MohistWorkflow.cs` + `mohist-local.workflow.yaml` → **应用配置层**。
- **Issue 内部清理（不跨上下文）**：`IIssueWorkflowProfile` 把"profile 配置"和"投影（`ProjectWorkflowState`）"焊在一个接口里，应拆开——两者都留 Issue，只是各管各的。
- **可选后续**：三个 profile Manager 现都在 `Workflow/Services/`，管理的是 per-context 的 profile 数据。除上述反向引用外它们不碰 Issue 领域；是否挪回各上下文，视后续重构成本而定，非本边界必需。

## 范围外

Session 读侧 → `Issue.Services` / `Runner.Services` / `Workflow.Services` 的反向依赖是 **Session 上下文**的问题（session 查询/报告要 issue/runner/workflow 上下文做富化与跨域组装），不在本边界。Session 现为独立子域（见 [`domain-analysis.md`](../../domain-analysis.md) Session 小节），其读侧已迁回 `Sessions/Services/`，但跨域报告需求（活动 feed、用量成本）仍反向依赖业务上下文——解法是抽离 AgentOps 报告上下文，见 [`domain-analysis.md`](../../domain-analysis.md) 现状偏差项。
