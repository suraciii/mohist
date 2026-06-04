---
purpose: "Prompt lifecycle: storage, loading, rendering, and multi-layer fallback inside workflow profile."
include:
  - "System / project / issue 三层模型与合并策略."
  - "WorkflowProfileManager 扩展 prompt 读入口."
  - "ProjectWorkflowProfileManager / IssueWorkflowProfileManager 扩展 prompt 写端."
  - "PromptTemplateEngine 变量展开."
  - "API 路由设计."
exclude:
  - "Runner 端 structured prompt 渲染 (resolvePrompt / renderStructuredPrompt)."
  - "Workflow 模板 (WorkflowDefinition) 管理."
  - "Agent session 发送协议."
style:
  - "Prefer diagrams over prose."
  - "Keep text short and human-readable."
---

# Prompt Management

## 定位

Prompt 是 workflow profile 的一部分，和 template、variables 同级管理。

```text
workflow profile
├── template    (WorkflowDefinition, 控制 stage/task/check 结构)
├── variables   (VariableBundle, 控制 agent/model/timeout)
└── prompts     (提示词模板, 控制每个 stage 发给 agent 的指令)
```

## 整体工作流

```text
                        ┌─────────── 写入 ───────────┐
                        │                            │
                  Issue Prompts            Project Prompts
                  (ProfileRow.Prompts)     (PromptTemplates 表)
                 key → body                key → { body, displayName, stage, ... }
                        │                            │
                        └──────────┬─────────────────┘
                                   │
                          System .prompt 文件
                           (IPromptLoader)
                                   │
                        ═══════════╪═══════════
                                   │
                        ┌────────── 读取 ────────────┐
                        │                            │
              WorkflowProfileManager                 │
              LoadPrompt(runId, key)                 │
                        │                            │
            ┌───────────┤                            │
            ▼           ▼                            │
       Prompts[key]   Variables                      │
       (合并后 body)  (LoadVariables)                 │
            │           │                            │
            └─────┬─────┘                            │
                  ▼                                  │
         PromptTemplateEngine                        │
         Render(body, variables)                     │
                  │                                  │
                  ▼                                  │
            ${{ openspecChangeDir }}                 │
            ${{ agent.model }}                       │
                  │                                  │
                  ▼                                  │
            最终文本 ──────────→ Agent               │
────────────────────────────────────────────────────┘
```

## 写路径

```text
PUT /api/projects/{id}/workflow-profile/prompts/{key}
  └─ ProjectWorkflowProfileManager.SetPromptAsync(projectId, key, body, displayName, ...)
       └─ IProjectTemplateStore.UpsertAsync()
            └─ INSERT/UPDATE ProjectPromptTemplates

PUT /api/projects/{id}/issues/{n}/workflow-profile/prompts/{key}
  └─ IssueWorkflowProfileManager.SetPromptAsync(issueKey, key, body)
       └─ IssueWorkflowProfileRow.Prompts[key] = body
            └─ SaveChanges()
```

## 读路径（运行时 dispatch）

```text
WorkflowGrain 需要 "build" 阶段的 prompt:

  1 ──→ WorkflowProfileManager.LoadPrompt(runId, "build")

          内部先从 WorkflowRun 反查 projectId="p1" + issueKey="p1:7"

          ┌─ issue: IssueWorkflowProfiles WHERE IssueKey="p1:7"
          │    Prompts["build"] → 命中! 返回 body + source="issue"
          │
          ├─ project: ProjectPromptTemplates WHERE ProjectId="p1" AND Key="build"
          │    → 命中! 返回 body + displayName + source="project"
          │
          └─ system: IPromptLoader.LoadAllTemplates()["build"]
               → 命中! 返回 body + source="system"

  2 ──→ WorkflowProfileManager.LoadVariables(runId)
          → VariableBundle { agent: { model: "claude" }, openspecChangeDir: "/tmp/..." }

  3 ──→ PromptTemplateEngine.Render(body, variables)
          "<artifact>Read ${{ openspecChangeDir }}/proposal.md</artifact>"
          → "<artifact>Read /tmp/.../proposal.md</artifact>"

  4 ──→ 最终文本发往 agent session
```

## 三层模型

```text
  Issue Prompts    (最高优先级, IssueWorkflowProfileRow.Prompts)
       ↓ 未命中
  Project Prompts  (项目级, ProjectPromptTemplates 表)
       ↓ 未命中
  System .prompt   (内置只读, Workflow/Prompts/*.prompt 文件)
```

合并规则：key 匹配时 issue > project > system。

## Modules

```text
WorkflowProfileManager (已有, 新增 prompt 方法)
  统一读入口: template + variables + prompts。
  + LoadPrompt(workflowRunId, key)        → ResolvedPrompt?
  + LoadPrompts(workflowRunId, stage?)    → ResolvedPrompt[]
  + RenderPrompt(body, variables)         → (rendered, missing, depth)

ProjectWorkflowProfileManager (已有, 新增 prompt 方法)
  项目级 template + variables + prompts 写端。
  + ListSystemPromptsAsync()                    → SystemTemplate[]
  + ListPromptsAsync(projectId)                 → EffectivePrompt[]
  + GetPromptAsync(projectId, key)              → EffectivePrompt?
  + SetPromptAsync(projectId, key, body, ...)   → ProjectTemplate
  + DeletePromptAsync(projectId, key)
  + PreviewPromptAsync(projectId, key, variables) → PreviewResult

IssueWorkflowProfileManager (已有, 新增 prompt 方法)
  Issue 级 template + variables + prompts 写端。
  + GetPromptsAsync(issueKey)                   → Dictionary<string, string>
  + SetPromptAsync(issueKey, key, body)
  + DeletePromptAsync(issueKey, key)

IPromptLoader (已有, 不变)
  系统默认提示词来源, 从 .prompt 文件加载。
  LoadAllTemplates() → Dictionary<string, SystemTemplate>
```

### 注入关系

```text
WorkflowProfileManager
  ├── IDbContextFactory<MohistDbContext>
  └── (内部调用 ProjectWorkflowProfileManager.GetSystemTemplateDefinition)

ProjectWorkflowProfileManager
  ├── IDbContextFactory<MohistDbContext>
  ├── IPromptLoader                          (新增注入, 读取系统提示词)
  ├── IProjectTemplateStore                   (新增注入, ProjectPromptTemplates CRUD)
  └── PromptTemplateEngine                    (新增注入, 渲染预览)

IssueWorkflowProfileManager
  ├── IDbContextFactory<MohistDbContext>
  └── Prompts 字段                           (IssueWorkflowProfileRow 新增)
```

## 数据模型

```text
ProjectPromptTemplates 表 (已有)
  ProjectId, Key, DisplayName, Description, Tags, Stage, Body, UpdatedAt
  PK: (ProjectId, Key)

IssueWorkflowProfileRow (已有, 新增字段)
  IssueKey, SourceTemplateId, TemplateJson, VariablesJson
  + Prompts   ← 新增: Dictionary<string, string>  (key → body)

SystemTemplate (已有)
  Key, DisplayName, Description, Tags, Stage, Body
```

## 变量展开

```text
PromptTemplateEngine.Render(body, variables)
  ├── 正则匹配 ${{ path.to.var }}
  ├── 从 JsonElement 变量树查找 (点号分隔路径)
  ├── 最多 5 轮递归展开
  └── 返回 (rendered, missing, depth)
```

未解析变量保留原文，同时出现在 MissingVariables 列表中。

## .prompt 文件格式

```yaml
---
name: "Build Task"
description: "Implements a single build task"
tags: [build]
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
| explore | `explore.prompt` |
| plan | `proposal.prompt`, `specs.prompt`, `design.prompt`, `tasks.prompt`, `self-review.prompt` |
| build | `build.prompt` |
| check | `review.prompt`, `review-self-check.prompt`, `auto-fix.prompt`, `re-verify.prompt` |
| 通用 | `conflict-resolution.prompt` |

## API

系统提示词（只读目录）：

```text
GET  /api/templates/system                     → SystemTemplate[]
POST /api/templates/extract-variables           → { variables: [...] }
```

项目 workflow profile（已有路由扩展）：

```text
GET    /api/projects/{id}/workflow-profile/prompts                 → EffectivePrompt[]
GET    /api/projects/{id}/workflow-profile/prompts/{key}           → EffectivePrompt
PUT    /api/projects/{id}/workflow-profile/prompts/{key}           → ProjectTemplate
DELETE /api/projects/{id}/workflow-profile/prompts/{key}           → 204
POST   /api/projects/{id}/workflow-profile/prompts/{key}/preview   → PreviewResult
```

Issue workflow profile（已有路由扩展）：

```text
GET    /api/projects/{id}/issues/{n}/workflow-profile/prompts                   → EffectivePrompt[]
GET    /api/projects/{id}/issues/{n}/workflow-profile/prompts/{key}             → EffectivePrompt
PUT    /api/projects/{id}/issues/{n}/workflow-profile/prompts/{key}             → { key, body }
DELETE /api/projects/{id}/issues/{n}/workflow-profile/prompts/{key}             → 204
POST   /api/projects/{id}/issues/{n}/workflow-profile/prompts/{key}/preview     → PreviewResult
```

`GET /{key}` 返回合并后的有效值（含 Source 标记来源），`PUT` 设置当前层，`DELETE` 清除当前层恢复下级。

废弃旧路由：

```text
GET    /api/projects/{id}/templates          → 迁至 /workflow-profile/prompts
GET    /api/projects/{id}/templates/{key}    → 迁至 /workflow-profile/prompts/{key}
PUT    .../templates/{key}/override          → 迁至 .../prompts/{key}
DELETE .../templates/{key}/override          → 迁至 .../prompts/{key}
POST   .../templates/{key}/preview           → 迁至 .../prompts/{key}/preview
```

## 前端

- 项目设置 → Workflow Profile → Prompts tab
- Issue 详情 → Workflow Profile → Prompts tab
- 编辑：key 只读（继承自下层），编辑 body
- "恢复默认" → DELETE（清除当前层）
- "预览" → POST preview，填入变量 JSON 查看渲染结果
