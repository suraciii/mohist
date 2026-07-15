# Workflow Profile

Profile = **template**（选择哪个 `WorkflowDefinition`）+ **variables**（`VariableBundle`）。

Prompt 不属于 Profile，见 [`../prompt-management.md`](../prompt-management.md)。Action
输入输出见 [`actions.md`](actions.md)，内置 Workflow 见 [`builtin-workflows.md`](builtin-workflows.md)。

## 架构

```text
Issue -> Workflow（单向依赖）
WorkflowGrain -> IWorkflowProfileProvider（port，只使用 Workflow 类型）
                    ^
            WorkflowProfileProvider（adapter，实时读取配置）
                    |
    global / project / issue / run profiles / project templates
```

- Workflow 不依赖 Issue。解析发生在 adapter，并实时读取，不保存 snapshot。
- `WorkflowRun` 保存执行状态与 Profile 身份，不保存 Profile body，也没有
  `RuntimeVariables`。
- `TaskRun.Output` = `JsonElement?`，与 `WithInput` 对齐。

## 与 Issue 的依赖方向

`Issue → Workflow` 单向。Workflow 不认识 issue，只操作抽象的 run、
`WorkflowDefinition` 与 variables。

| 概念 | 归属 |
|---|---|
| `WorkflowDefinition`（type + engine） | Workflow（`Workflow/Domain/Definition/`） |
| workflow profile（template + variables） | Issue / Project（它们的配置） |
| prompts | Project Space（见 [`../prompt-management.md`](../prompt-management.md)） |
| 默认 `WorkflowDefinition` 内容（yaml） | application config（composition root） |
| projection（attention 等） | Issue（只读消费） |

## VariableBundle

形状为 `{ vars, stages: { "plan": { vars } } }`。Set 表示替换，Patch 表示 deep merge。

## Profile 层次

```text
project_workflow_profile    ProjectId, DefaultTemplateId, Variables
issue_workflow_profile      IssueId, SourceTemplateId, Template, Variables
workflow_run_profile        WorkflowRunId, Variables
```

Run Profile 优先级最高，保存 `vars.change.id` 等运行事实。它不属于 `WorkflowRun`
aggregate，只通过 `WorkflowRunId` 关联，由 Profile service 读写。

## 合并

合并分三步：Template lane 只做 fallback 选择，不 deep merge；Variable lane 分层 deep
merge；最后叠加 Stage variables。

```text
Template lane（fallback，不 deep merge）：
  issue custom template? -> issue source template? -> project default? -> system default
  -> CurrentTemplateVariables

Variable lane（分层 deep merge）：
  global -> project -> issue -> run
  -> ProfileVariables

Effective:
  deepMerge(CurrentTemplateVariables, ProfileVariables) -> WorkflowEffectiveVariables

Stage:
  deepMerge(WorkflowEffectiveVariables.vars, WorkflowEffectiveVariables.stages[stage].vars)
  -> WorkflowStageEffectiveVariables
```

Deep merge 递归合并 object，后者覆盖冲突字段。存储层的 `null` 被忽略，不提供
null-overwrite 语义。

## ExpandTaskWith

```text
for (k,v) in taskWith:
  "${{...}}" 占据整个字符串 -> 替换为 vars 值，否则保留给 Runner 展开
  object && k in vars -> deepMerge（vars 覆盖）
  其他 -> 原样保留
```

整值展开保留解析后的 JSON 类型。现有 `vars.agent` 因此可以选择 OpenCode 模型选项，
而不需要第二条配置通路。该变量名在此 Action 契约中不携带 Agent 身份：

```yaml
variables:
  agent:
    model: anthropic/claude-sonnet-4
    variant: high

tasks:
  - uses: mohist/opencode
    with:
      prompt: ${{ prompts.proposal }}
      options: ${{ vars.agent }}
```

展开后，Action Input 中的 `options` 仍是 object。Action 不能再次读取 effective
variables，也不能重新 merge `vars.agent`。

Task-level `expect` 使用相同的 template lookup 规则，但单独展开；它不会 deep merge
进 `with`，也不会成为 Action Input。

## Runtime 写入：setVars

Task 成功后，通过 `setVars` 把 Action output 投影到 Run Profile。投影规则（path
语义、只能修改 `vars.*`、失败即 task 失败）以 [`actions.md`](actions.md) 为准。
Profile 侧事实：

- 只 patch `workflow_run_profile.Variables`，不修改 Project / Issue Profile，也不修改
  `WorkflowRun` 执行状态。
- Runner 先从 output 提取值，再调用
  `PATCH /api/workflow-runs/{id}/workflow-profile/variables`，最后报告 task complete。

## 读取 API

```text
GET /workflow-runs/:id/variables/effective           -> WorkflowEffectiveVariables.vars
GET /workflow-runs/:id/variables/effective?stage=X   -> WorkflowStageEffectiveVariables
GET /workflow-runs/:id/variables/effective/:keyPath  -> keyPath 对应的值
```

## 写入 API

```text
system templates    GET /workflow-templates/system
project templates   /projects/:p/workflow-templates
project profile     /projects/:p/workflow-profile
issue profile       /projects/:p/issues/:n/workflow-profile
run profile         /workflow-runs/:id/workflow-profile
effective           /workflow-runs/:id (/yaml, /variables/effective)
```

## 实装差距

默认 `WorkflowDefinition` 内容应归属 application config 层：
`mohist-local.workflow.yaml` 已移至 `Workflow/Services/Profiles/`，但 Issue 侧仍留有
`MohistWorkflow` 薄封装（`Issue/Services/WorkflowProfiles/MohistWorkflow.cs`），待收编
以消除跨 context 反向依赖。
