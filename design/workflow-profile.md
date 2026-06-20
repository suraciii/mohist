---
purpose: "Workflow profile：它包含什么（template + variables + prompts）、合并策略、加载流程。"
include:
  - "Module definitions and interfaces."
  - "Variable merge strategy with ASCII diagrams."
  - "Loading and dispatch flow."
exclude:
  - "Runner-side template rendering or ${{ }} expansion."
  - "Database schema details."
style:
  - "Prefer diagrams over prose."
  - "Keep text short and human-readable."
---

# Workflow Profile

> **Workflow profile 是 per project/issue 的工作流配置，含两块：**
> 1. **template** —— 选哪个工作流定义（`WorkflowDefinition`：stage/task/check 结构）
> 2. **variables** —— 变量覆盖（`VariableBundle`）
>
> **prompts 不是 workflow profile 的一部分**——它是更上层的独立抽象（项目/issue 级的 prompt 库），workflow 只是它的消费者之一；将来脱离 workflow 的独立 Agent 也会消费同一份 prompt。见 `prompt-management.md`。

## Modules

```text
WorkflowGrain (Orleans grain, key: workflowRunId)
  状态机 + 调度。
  读: ProfileManager.Load*(workflowRunId)
  读: WorkflowRuntimeContext

WorkflowProfileManager (stateless)
  唯一读入口 + compute。
  LoadTemplate(workflowRunId)       -> ResolvedTemplate
  LoadVariables(workflowRunId)      -> VariableBundle     (独立变量层，已内部 merge)
  ExpandTaskWith(resolved, with) -> mergedWith

ProjectWorkflowProfileManager (stateless)
  项目模板 + 变量 写端。
  ListSystemTemplatesAsync()       -> SystemTemplateInfo[]
  ListTemplatesAsync(projectId)    -> WorkflowTemplateInfo[]
  CreateTemplateAsync(projectId, yaml)
  UpdateTemplateAsync(projectId, id, yaml)
  DeleteTemplateAsync(projectId, id)
  SetDefaultTemplateAsync(projectId, templateId)
  SetVariablesAsync(projectId, bundle)
  PatchVariablesAsync(projectId, bundle)

IssueWorkflowProfileManager (stateless)
  issue 模板 + 变量 写端。
  UpdateTemplateAsync(issueId, { ProjectTemplateId?, Template? })
  SetVariablesAsync(issueId, bundle)
  PatchVariablesAsync(issueId, bundle)
```

## VariableBundle

所有变量层共用一个类型。支持 Set（完整替换）和 Patch（deep merge）。

```text
VariableBundle {
  vars:   { agent: { type: "opencode", model: "sonnet-4" }, timeoutMs: 300000 }
  stages: {
    "plan":  { vars: { agent: { model: "gpt-4o" } } },
    "build": { vars: {} }
  }
}
```

## Variable Merge

变量分两类来源：**模板嵌入变量**（YAML `variables:` 段，来自选定模板）和**独立变量**（用户/管理员配置）。

调用方先加载模板获得嵌入变量作为 base，再加载独立变量覆盖。

```
                   优先级 低 ──────────────────────────────> 高

  ┌──────────────────────┐
  │ TEMPLATE embedded    │  YAML `variables:` 段, 来自选定模板
  │ (base)               │  选择: issue custom > issue template > project default
  └──────────┬───────────┘
             │  deepMerge(embedded, independent)
             ▼
  ┌──────────────────────┐
  │ project vars         │  project_workflow_profile.Variables
  └──────────┬───────────┘
             ▼
  ┌──────────────────────┐
  │ issue vars           │  issue_workflow_profile.Variables
  └──────────┬───────────┘
             ▼
  ┌──────────────────────┐
  │ dispatch injection   │  (仅 dispatch 时) workflow.runId, stage.name, work.id/type/title
  └──────────┬───────────┘
             ▼
  ┌──────────────────────┐
  │ FINAL ResolvedVars   │
  └──────────────────────┘
```

**Deep merge**：对象递归合并，后者覆盖前者同名 key；不提供的字段保留。

```
base:  { vars: { agent: { type: "opencode", model: "sonnet-4", timeout: 300 } } }
cover: { vars: { agent: { model: "gpt-4o" } } }
       
       → { vars: { agent: { type: "opencode", model: "gpt-4o", timeout: 300 } } }
```

## Loading

### 1. 选定生效模板

```
WorkflowProfileManager.LoadTemplate(workflowRunId) -> ResolvedTemplate { id, structure, embeddedVariables }

  issue_workflow_profile.Template?      ─→ return parsed       (自定义)
  issue_workflow_profile.SourceTemplateId ─→ load from project_templates
  project_workflow_profile.DefaultTemplateId ─→ load from project_templates
  mohist/default system template        ─→ fallback
```

### 2. 加载独立变量（内部 merge 2 层）

```
WorkflowProfileManager.LoadVariables(workflowRunId) -> VariableBundle

  project_workflow_profile.Variables     ─┐
  issue_workflow_profile.Variables      ─┘  deepMerge ─→ independent
```

### 3. 调用方合并并 dispatch

```
WorkflowGrain.MakeDispatchAsync(stage, work):

  context     = LoadRuntimeContext(workflowRunId)
  template    = ProfileManager.LoadTemplate(workflowRunId)
  independent = ProfileManager.LoadVariables(workflowRunId)
  resolved    = deepMerge(template.embeddedVariables, independent)
                 + inject { workflow.runId, stage.name, work.* }
  with        = ProfileManager.ExpandTaskWith(resolved, task.With)

  ─→ WorkDispatch { Context: context, Arguments: with, Variables: resolved }
```

### ExpandTaskWith

```
ProfileManager.ExpandTaskWith(resolved, taskWith)

  for each (key, value) in taskWith:
    "${{ ... }}" template   → preserve         (runner 展开)
    object && key in vars   → deepMerge (vars 覆盖)
    else                    → preserve
```

## Data Model

```text
project_workflow_profile       key: projectId
  DefaultTemplateId, Variables (VariableBundle)

project_templates              ProjectId, TemplateId, Template

issue_workflow_profile         SourceTemplateId, Template, Variables

workflow_runtime_context       key: workflowRunId
  Context snapshot for dispatch rendering.
```

`WorkflowRuntimeContext` 和 profile variables 分开存。

- Profile variables: 用户/管理员配置，由 `WorkflowProfileManager` 管理。
- Runtime context: run-start 快照，用于 dispatch，不作为用户配置入口。
- `WorkflowRun.Metadata` 保存 `ProjectId` + `IssueId`，不要从 runtime context 反查身份。

## Write API

```text
系统模板:           GET    /workflow-templates/system

项目模板:           GET    /projects/:projectId/workflow-templates
                    POST   /projects/:projectId/workflow-templates
                    GET    /projects/:projectId/workflow-templates/:templateId
                    PUT    /projects/:projectId/workflow-templates/:templateId
                    DELETE /projects/:projectId/workflow-templates/:templateId

项目 Profile:       GET    /projects/:projectId/workflow-profile
                    PUT    /projects/:projectId/workflow-profile/default-template
                    DELETE /projects/:projectId/workflow-profile/default-template
                    GET    /projects/:projectId/workflow-profile/variables
                    PUT    /projects/:projectId/workflow-profile/variables
                    PATCH  /projects/:projectId/workflow-profile/variables

Issue Profile:      GET    /projects/:projectId/issues/:number/workflow-profile
                    PUT    /projects/:projectId/issues/:number/workflow-profile/template
                    DELETE /projects/:projectId/issues/:number/workflow-profile/template
                    GET    /projects/:projectId/issues/:number/workflow-profile/variables
                    PUT    /projects/:projectId/issues/:number/workflow-profile/variables
                    PATCH  /projects/:projectId/issues/:number/workflow-profile/variables

Run:                GET    /workflow-runs/:workflowRunId/yaml
                    GET    /workflow-runs/:workflowRunId/variables/effective
```

前端 "Install" → `GET /workflow-templates/system` → 选一个 → `POST /projects/:projectId/workflow-templates { yaml }`

普通用户调整变量时更新 project/issue workflow profile。workflow run 不保存 template 快照，也不作为常规变量配置入口。

`/variables/effective` 是当前 API 名称；设计语义是 "effective dispatch variables"，不是 runtime context。
