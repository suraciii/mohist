---
purpose: "Template and variable modules, merge strategy, and loading flow."
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

# Workflow Template & Variables

## Modules

```text
WorkflowGrain (Orleans grain, key: workflowRunId)
  状态机 + 调度。
  读: ProfileManager.Load*(runId)

WorkflowProfileManager (stateless)
  唯一读入口 + compute。
  LoadTemplate(runId)              -> ResolvedTemplate
  LoadVariables(runId)             -> VariableBundle     (独立变量层，已内部 merge)
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
  │ (base)               │  选择: run snapshot > issue custom > issue template > project default
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
  │ workflow run vars    │  workflow_profile.Variables
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
WorkflowProfileManager.LoadTemplate(runId) -> ResolvedTemplate { id, structure, embeddedVariables }

  workflow_profile.Template?            ─→ return it           (快照)
  issue_workflow_profile.Template?      ─→ return parsed       (自定义)
  issue_workflow_profile.SourceTemplateId ─→ load from project_templates
  project_workflow_profile.DefaultTemplateId ─→ load from project_templates
```

### 2. 加载独立变量（内部 merge 3 层）

```
WorkflowProfileManager.LoadVariables(runId) -> VariableBundle

  project_workflow_profile.Variables     ─┐
  issue_workflow_profile.Variables      ─┤  deepMerge ─→ independent
  workflow_profile.Variables            ─┘
```

### 3. 调用方合并并 dispatch

```
WorkflowGrain.MakeDispatchAsync(stage, work):

  template    = ProfileManager.LoadTemplate(GrainKey)
  independent = ProfileManager.LoadVariables(GrainKey)
  resolved    = deepMerge(template.embeddedVariables, independent)
                 + inject { workflow.runId, stage.name, work.* }
  with        = ProfileManager.ExpandTaskWith(resolved, task.With)

  ─→ WorkDispatch { Arguments: with, Variables: resolved }
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

workflow_profile               Template, Variables
                               ProjectId, IssueId
```

## Write API

```text
系统模板:           GET  /system/templates
项目模板:           CRUD /projects/:id/templates[/:tid]
                    PUT  /projects/:id/default-template
项目变量:           PUT/PATCH /projects/:id/variables
Issue:              PUT  /issues/:id/template
                    PUT/PATCH /issues/:id/variables
Run:                PUT/PATCH /workflow/:wrId/variables
```

前端 "Install" → `GET /system/templates` → 选一个 → `POST /projects/:id/templates { yaml }`