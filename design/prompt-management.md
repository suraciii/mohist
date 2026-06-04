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

Prompt 是 workflow profile 的第三块，和 template、variables 同级管理。

```text
workflow profile
├── template    (WorkflowDefinition, 控制 stage/task/check 结构)
├── variables   (VariableBundle, 控制 agent/model/timeout)
└── prompts     (提示词 template, 控制每个 stage 发给 agent 的指令)
```

## 三层存储

```text
  Issue WorkflowProfiles.Prompts     key → body     (最高优先级)
       ↓ 未命中
  Project WorkflowProfiles.Prompts   key → body     (项目覆盖)
       ↓ 未命中
  System .prompt 文件                IPromptLoader  (内置只读)
```

与 variables 完全一致：项目和 Issue 都在同一个 profile row 里。

## 数据模型

```text
ProjectWorkflowProfileRow
  ProjectId, DefaultTemplateId, Variables, Prompts
  存储: variables 和 prompts 均为 key→value 结构, JSON 序列化

IssueWorkflowProfileRow
  IssueKey, SourceTemplateId, Template, Variables, Prompts
  存储: 同上，Template 为 WorkflowDefinition JSON

SystemTemplate (已有, IPromptLoader 加载)
  Key, DisplayName, Description, Tags, Stage, Body
```

字段名不带 `Json` 后缀——序列化是实现细节，不应出现在字段名中。

## 工作流

```text
写入:

  Issue Prompts          Project Prompts            .prompt 文件
  (ProfileRow.Prompts)   (ProfileRow.Prompts)       IPromptLoader
  key → body              key → body                文件系统, 内置只读
       │                        │                        │
       └────────────────────────┼────────────────────────┘
                                │
                   WorkflowProfileManager
                   LoadPrompt(runId, key)
                                │
                        ┌───────┴───────┐
                        ▼               ▼
                   Prompts[key]     Variables
                   (合并后 body)   (LoadVariables)
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
  ├─ IssueWorkflowProfileRow.Prompts["build"]     → 命中 (source: issue)
  ├─ ProjectWorkflowProfileRow.Prompts["build"]   → 命中 (source: project)
  └─ IPromptLoader.LoadAllTemplates()["build"]    → 命中 (source: system)

LoadVariables(runId) → VariableBundle

Render(body, vars)
  "<instruction>Read ${{ openspecChangeDir }}/proposal.md</instruction>"
  → "<instruction>Read /tmp/changes/xxx/proposal.md</instruction>"

→ Agent
```

## Modules

```text
WorkflowProfileManager
  统一读入口: template + variables + prompts。
  LoadPrompt(runId, key)        → ResolvedPrompt?
  LoadPrompts(runId, stage?)    → ResolvedPrompt[]
  RenderPrompt(body, vars)      → (rendered, missing, depth)
  依赖: IDbContextFactory, IPromptLoader, PromptTemplateEngine

ProjectWorkflowProfileManager
  项目级 template + variables + prompts 写端。
  ListSystemPromptsAsync()                 → SystemTemplate[]
  ListPromptsAsync(projectId)              → EffectivePrompt[]
  GetPromptAsync(projectId, key)           → EffectivePrompt?
  SetPromptAsync(projectId, key, body)     → void
  DeletePromptAsync(projectId, key)        → void
  PreviewPromptAsync(projectId, key, vars) → PreviewResult
  依赖: IDbContextFactory, IPromptLoader, PromptTemplateEngine

IssueWorkflowProfileManager
  Issue 级 template + variables + prompts 写端。
  GetPromptsAsync(issueId)             → Dictionary<string, string>
  SetPromptAsync(issueId, key, body)
  DeletePromptAsync(issueId, key)

IPromptLoader (已有, 不变)
  系统默认提示词来源, 从 .prompt 文件加载。
  LoadAllTemplates() → Dictionary<string, SystemTemplate>
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

废弃：`/api/projects/{id}/templates` → 迁至 `/workflow-profile/prompts`

## 前端

- 项目设置 / Issue 详情 → Workflow Profile → Prompts tab
- key 只读，编辑 body；删除即恢复下级；预览填入变量 JSON
