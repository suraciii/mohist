# 边界：Workflow ↔ Issue（Profile 归属与依赖方向）

本文是 [`context-map.md`](../context-map.md) 关系 #1（Workflow↔Issue）的具体展开，聚焦：**依赖怎么走、哪些东西真的放错了**。profile 的内容/合并/加载见 [`workflow-profile.md`](../workflow-profile.md)。

> 相关但不重叠：调度见 `workflow-scheduling.md`、任务派发见 `workflow-task-dispatch.md`、协调见 `issue-workflow-coordination.md`。

## 目标

**单向依赖 `Issue → Workflow`，Workflow 不依赖 Issue。** Workflow 引擎不知道"issue"——只操作抽象的 workflow run、只消费解析后的 `WorkflowDefinition` + 变量。

## 现状：大部分已经对了

- **Workflow 领域模型纯净**：`Workflow/Domain/` 零 Issue 引用。`WorkflowDefinition`（结构类型）+ 引擎都在 Workflow。
- **workflow profile 留在 Issue/Project 是对的**：profile = template 选择 + variables，本就是各上下文自己的配置数据，不是 Workflow 的领域。

**唯一的真问题**：默认 `WorkflowDefinition` 的**内容**（`MohistWorkflow` + `mohist-default.workflow.yaml`）错放在 `Issue/Services/WorkflowProfiles/`，导致反向依赖。

## 归属判定

| 概念 | 是什么 | 归属 | 现状 |
|---|---|---|---|
| `WorkflowDefinition` | 结构类型（stages/tasks/checks）+ 引擎 | **Workflow** | ✓ 已在 `Workflow/Domain/Definition/` |
| workflow profile | template 选择 + variables（per project/issue 配置） | **Issue / Project 自己** | ✓ 留原处（本就是它们的配置） |
| prompts | 项目/issue 级 prompt 库 | **独立抽象**（workflow 与未来独立 Agent 共享） | 见 `prompt-management.md`，不属 profile |
| 默认 `WorkflowDefinition` 内容 | 出厂默认工作流（yaml） | **应用配置层**（composition root） | ❌ 错放在 Issue，要搬 |
| 投影 | Issue 解读 workflow 运行状态（attention 等） | **Issue**（只读消费） | ✓ 留 Issue |

## 反向依赖与修复

`Workflow/Services/ProjectWorkflowProfileManager.cs` 引 `Issue.Services.WorkflowProfiles`，**只为拿 `MohistWorkflow.Definition`**。把默认定义挪到应用配置层，这条反向引用即消失 → 双向变单向 `Issue→Workflow` ✓。

（另外两个 Manager——`WorkflowProfileManager`、`IssueWorkflowProfileManager`——不引用 Issue 类型，不造成领域污染。）

## 现状偏差（迁移项）

本文是目标态。需要收敛的偏差，范围比一开始以为的窄：

- **搬（唯一跨上下文的搬迁）**：`MohistWorkflow.cs` + `mohist-default.workflow.yaml` → **应用配置层**。
- **Issue 内部清理（不跨上下文）**：`IIssueWorkflowProfile` 把"profile 配置"和"投影（`ProjectWorkflowState`）"焊在一个接口里，应拆开——两者都留 Issue，只是各管各的。
- **可选后续**：三个 profile Manager 现都在 `Workflow/Services/`，管理的是 per-context 的 profile 数据。除上述反向引用外它们不碰 Issue 领域；是否挪回各上下文，视后续重构成本而定，非本边界必需。

## 范围外

`Workflow/Services/Sessions/AgentSessionQuerier.cs` → `Issue.Services` 的依赖是 **Session/Agent 上下文**的问题（session 查询要 issue 上下文），不在本边界，待分析 Agent/Session 时处理。
