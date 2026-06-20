---
purpose: "Prompt 属于 Project Space：project 级 prompt 库（内置兜底）、执行侧按需解析、变量展开、API。"
include:
  - "Prompt 的归属（Project Space）与和 workflow 的关系."
  - "project 配置 + 内置 fallback 的解析规则."
  - "执行侧按需解析时机."
  - "PromptTemplateEngine 变量展开."
  - "API 路由设计."
exclude:
  - "Runner 端 structured prompt 渲染细节."
  - "Workflow 模板 (WorkflowDefinition) 管理."
style:
  - "Prefer diagrams over prose."
  - "Keep text short and human-readable."
---

# Prompt Management

**Prompt 属于 Project Space 上下文——project 名下的一套命名 prompt 库。** 它**不属于 workflow profile**。

- **project 级 prompt** → **Project Space**（唯一可配置层；owner，可见性由 project 提供，云产品里即 tenancy 边界）
- **内置 prompt**（源码自带的 .prompt）→ 应用配置，**只是 loader 的 fallback**：project 没配某个 key 时兜底，不是要单独管理的一层

workflow 只是它的消费者之一（按 key 引用）；将来脱离 workflow 的独立 Agent 也消费同一份 prompt——但那 Agent 本就活在某个 project 里，取 prompt 不算新增耦合。

## 与 Workflow 的关系（目标态）

```text
WorkflowDefinition:  action 按 key 声明要用哪个 prompt（字符串引用）
        │
        ▼  dispatch payload 只带 key + 已解析变量（不带文本，key 很小）
   ┌──────────────────────────────────────────────────┐
   │  Runner / action ——执行那一刻——                    │
   │  按 key + project 取那一条（project 没配则兜底内置） │
   │  用 dispatch 变量渲染 → 发给 Agent                  │
   └───────────────────────┬──────────────────────────┘
                           │ 按需取（只取用到的那一条）
                           ▼
                  Project Space（prompt 库）── 文本
```

- **Workflow 零依赖 prompt 管理**：它只碰 key（字符串）。
- **文本由执行方（runner/action）在执行那一刻按需解析**：lazy、只取用到的那一条，扛得住大 prompt。
- 契合"执行事实与状态裁判分离"——解析 prompt 是**执行侧**的事，不是裁判。

## 解析规则

project 自己配的 prompt 优先；没配的 key，loader 兜底到源码内置的 .prompt。只有一层可配置（project），内置只是 fallback：

```text
  Project prompts       key → body     (project 配置，命中即用)
       ↓ 未命中
  内置 .prompt 文件       源码自带       (fallback，只读)
```

## 变量展开

```text
PromptTemplateEngine.Render(body, variables)
  ├─ 正则匹配 ${{ path.to.var }}
  ├─ 从 JsonElement 树查找, 最多 5 轮递归
  └─ 返回 (rendered, missing, depth)
```

未解析变量保留原文。渲染由执行方在解析时做（带上 dispatch 变量）。

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

## 数据模型（现状）

```text
ProjectWorkflowProfileRow
  ProjectId, DefaultTemplateId, Variables, Prompts

IssueWorkflowProfileRow
  IssueKey, SourceTemplateId, Template, Variables, Prompts

SystemTemplate (IPromptLoader 加载)
  Key, DisplayName, Description, Tags, Stage, Body
```

字段名不带 `Json` 后缀——序列化是实现细节。

## API（现状）

系统：

```text
GET  /api/templates/system
POST /api/templates/extract-variables
```

项目：

```text
GET    /api/projects/{id}/workflow-profile/prompts
GET    /api/projects/{id}/workflow-profile/prompts/{key}
PUT    /api/projects/{id}/workflow-profile/prompts/{key}
DELETE /api/projects/{id}/workflow-profile/prompts/{key}
POST   /api/projects/{id}/workflow-profile/prompts/{key}/preview
```

Issue：

```text
GET    /api/projects/{id}/issues/{n}/workflow-profile/prompts
GET    /api/projects/{id}/issues/{n}/workflow-profile/prompts/{key}
PUT    /api/projects/{id}/issues/{n}/workflow-profile/prompts/{key}
DELETE /api/projects/{id}/issues/{n}/workflow-profile/prompts/{key}
POST   /api/projects/{id}/issues/{n}/workflow-profile/prompts/{key}/preview
```

`GET /{key}` 返回合并后的有效值（含 Source），`PUT` 设置当前层，`DELETE` 清除当前层。

## 前端

- 项目设置 / Issue 详情 → Prompts
- key 只读，编辑 body；删除即恢复下级；预览填入变量 JSON

## 现状偏差（迁移项）

本文是目标态。当前代码与目标的偏差：

- **概念错位**：现文档/代码把 prompt 当作 "workflow profile 的第三块"。目标：prompt 属于 **Project Space 上下文**（project-scoped），不属于 profile（profile 只有 template + variables，见 [`workflow-profile.md`](workflow-profile.md)）。
- **issue 级 prompt 无场景，应移除**：现状有 issue 级 prompt 覆盖（`IssueWorkflowProfileRow.Prompts` + `/issues/{n}/workflow-profile/prompts` API）。目标：砍掉——per-issue 的差异走变量（issue body 等）流进标准 prompt，不需要换模板；issue 级**变量**保留（那是数据，不是模板）。
- **寄生在 Workflow**：prompt 机制（`FilePromptLoader` / `PromptTemplateEngine` / builtins）现躺在 `Workflow/Services/Prompts/`。目标：挪进 **Project Space**（内置 .prompt 作为 loader fallback 留在源码/应用配置），供 runner 和未来独立 Agent 共用，不依赖 Workflow。
- **解析时机与执行方**：现状是 `WorkflowGrain`（server）在 dispatch 时 `LoadPrompt` 解析、文本塞进 payload。目标：dispatch 只带 key，由 runner/action 在执行那一刻按需解析（见上方「与 Workflow 的关系」）。
- **存储/API 耦合 profile**：prompts 现和 template/variables 同存一个 profile row，API 路径含 `/workflow-profile/prompts`。目标：prompts 归 Project Space、按 project scope 独立查询——独立 Agent 不该知道"workflow profile"这个概念。
