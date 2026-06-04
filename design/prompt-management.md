---
purpose: "Prompt lifecycle: storage, loading, rendering, and multi-layer fallback inside workflow profile."
include:
  - "System / project / issue 三层模型与合并策略."
  - "Prompt 读写路径与 agent dispatch."
  - "PromptTemplateEngine 变量展开."
  - "API 路由设计."
exclude:
  - "Runner 端 structured prompt 渲染."
  - "Workflow 模板 (WorkflowDefinition) 管理."
style:
  - "Prefer diagrams over prose."
  - "Keep text short and human-readable."
---

# Prompt Management

Prompt 是 workflow profile 的第三块，和 template、variables 同级，由 `WorkflowProfileManager` 统一读取。

## 工作流

```text
写入:

  IssuePrompts                ProjectPromptTemplates 表       .prompt 文件
  (ProfileRow.Prompts)        IProjectTemplateStore           IPromptLoader
  key → body                  key → {body, stage, ...}        文件系统, 内置只读
       │                              │                            │
       └──────────────────────────────┼────────────────────────────┘
                                      │
                         WorkflowProfileManager
                         LoadPrompt(runId, key)
                                      │
                              ┌───────┴───────┐
                              ▼               ▼
                         Prompts[key]     Variables
                         (合并后 body)    (LoadVariables)
                              │               │
                              └───────┬───────┘
                                      ▼
                             PromptTemplateEngine
                             Render(body, vars)
                                      │
                                      ▼
                            最终文本 → Agent
```

## 读路径

```text
WorkflowGrain 需要 "build" 阶段的 prompt:

LoadPrompt(runId, "build")
  │
  ├─ 从 WorkflowRun 反查 projectId + issueId
  │
  ├─ IssueWorkflowProfileRow.Prompts["build"]   → 命中则返回 (source: issue)
  ├─ ProjectPromptTemplates.Key="build"          → 命中则返回 (source: project)
  └─ IPromptLoader.LoadAllTemplates()["build"]   → 命中则返回 (source: system)

LoadVariables(runId) → VariableBundle

Render(body, vars)
  "<instruction>Read ${{ openspecChangeDir }}/proposal.md</instruction>"
  → "<instruction>Read /tmp/changes/xxx/proposal.md</instruction>"

→ Agent
```

## Modules

```text
WorkflowProfileManager (已有, 新增 3 个方法)
  LoadPrompt(runId, key)        → ResolvedPrompt?
  LoadPrompts(runId, stage?)    → ResolvedPrompt[]
  RenderPrompt(body, vars)      → (rendered, missing, depth)
  依赖: IDbContextFactory, IProjectTemplateStore, IPromptLoader, PromptTemplateEngine

ProjectWorkflowProfileManager (已有, 新增 prompt 方法)
  ListPromptsAsync(projectId)              → EffectivePrompt[]
  GetPromptAsync(projectId, key)           → EffectivePrompt?
  SetPromptAsync(projectId, key, body, …)  → ProjectTemplate
  DeletePromptAsync(projectId, key)
  PreviewPromptAsync(projectId, key, vars) → PreviewResult

IssueWorkflowProfileManager (已有, 新增 prompt 方法)
  GetPromptsAsync(issueId)               → Dictionary<string, string>
  SetPromptAsync(issueId, key, body)
  DeletePromptAsync(issueId, key)
```

## 数据模型

```text
ProjectPromptTemplates 表 (已有)
  ProjectId, Key, DisplayName, Description, Tags, Stage, Body, UpdatedAt
  PK: (ProjectId, Key)

IssueWorkflowProfileRow (已有, 新增字段)
  + Prompts  // Dictionary<string, string>  key → body

SystemTemplate (已有)
  Key, DisplayName, Description, Tags, Stage, Body
```

## 变量展开

```text
PromptTemplateEngine.Render(body, variables)
  ├─ 正则匹配 ${{ path.to.var }}
  ├─ 从 JsonElement 树查找, 最多 5 轮递归
  └─ 返回 (rendered, missing, depth)
```

未解析变量保留原文。

## .prompt 文件格式

```yaml
---
name: "Build Task"
stage: build
---
<artifact id="build-task">
  <task>Complete exactly one implementation task.</task>
  <instruction>Read ${{ openspecChangeDir }}/proposal.md</instruction>
</artifact>
```

12 个系统模板按 stage 分布：

| 阶段 | 文件 |
|------|------|
| plan | `proposal.prompt`, `specs.prompt`, `design.prompt`, `tasks.prompt`, `self-review.prompt` |
| build | `build.prompt` |
| check | `review.prompt`, `review-self-check.prompt`, `auto-fix.prompt`, `re-verify.prompt` |
| — | `explore.prompt`, `conflict-resolution.prompt` |

## API

系统：

```text
GET  /api/templates/system
POST /api/templates/extract-variables
```

项目 workflow profile：

```text
GET    /api/projects/{id}/workflow-profile/prompts
GET    /api/projects/{id}/workflow-profile/prompts/{key}
PUT    /api/projects/{id}/workflow-profile/prompts/{key}
DELETE /api/projects/{id}/workflow-profile/prompts/{key}
POST   /api/projects/{id}/workflow-profile/prompts/{key}/preview
```

Issue workflow profile：

```text
GET    /api/projects/{id}/issues/{n}/workflow-profile/prompts
GET    /api/projects/{id}/issues/{n}/workflow-profile/prompts/{key}
PUT    /api/projects/{id}/issues/{n}/workflow-profile/prompts/{key}
DELETE /api/projects/{id}/issues/{n}/workflow-profile/prompts/{key}
POST   /api/projects/{id}/issues/{n}/workflow-profile/prompts/{key}/preview
```

`GET /{key}` 返回合并后的有效值（含 Source），`PUT` 设置当前层，`DELETE` 清除当前层。

旧路由废弃：

```text
/api/projects/{id}/templates  →  迁至 /workflow-profile/prompts
```

## 前端

- 项目设置 / Issue 详情 → Workflow Profile → Prompts tab
- key 只读，编辑 body；删除即恢复下级；预览填入变量 JSON
