# Prompt Management

**Prompt 属于 Project Space 上下文——project 名下的一套命名 prompt 库。** 不属于 workflow profile。

- **project 级 prompt** → Project Space（唯一可配置层；project 即 tenancy 边界）
- **内置 prompt**（源码自带的 .prompt）→ loader 的 fallback：project 没配某个 key 时兜底

workflow 只是消费者之一（按 key 引用）；脱离 workflow 的独立 Agent 也消费同一份 prompt。

## 与 Workflow 的关系

```text
WorkflowDefinition:  action 按 key 声明要用的 prompt（字符串引用）
        │
        ▼  dispatch payload 只带 key + 变量（不带文本）
   ┌──────────────────────────────────────────────────┐
   │  Runner / action ——执行那一刻——                    │
   │  按 key + project 取 prompt（project 没配则兜底内置）│
   │  用 dispatch 变量渲染 → 发给 Agent                  │
   └───────────────────────┬──────────────────────────┘
                            │ 按需取（只取用到的那一条）
                            ▼
                   Project Space（prompt 库）── 文本
```

- Workflow 零依赖 prompt 管理——它只碰 key（字符串）。
- 文本由执行方（runner/action）在执行那一刻按需解析。
- 契合"执行事实与状态裁判分离"——解析 prompt 是执行侧的事。

## 解析规则

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

## 差距脚注

正文是 spec，以下是现状差距，收敛后删：

- prompt 被当作 "workflow profile 的第三块"而非独立 Project Space 概念。
- 有 issue 级 prompt 覆盖（`IssueWorkflowProfileRow.Prompts`），pre-issue 差异应走变量不该换 prompt；需移除并删对应 API。
- prompt 机制（`FilePromptLoader` / `PromptTemplateEngine`）寄生在 `Workflow/Services/Prompts/`；目标迁入 Project Space。
- dispatch 时 server 侧 `LoadPrompt` 解析、文本塞进 payload；目标改为只带 key，由 runner 按需解析。
- prompts 与 template/variables 同存一个 profile row，API 路径含 `/workflow-profile/prompts`。
