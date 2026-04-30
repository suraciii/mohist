## Why

Mohist 的 agent prompt 系统存在三种不同的结构风格（XML-structured for plan、bracket-blob for build、markdown prose for review），导致 token 浪费（proposal+design 每个 task 重复内联，11-task issue 消耗 ~38K tokens 只为了传相同内容）和 agent 质量不一致（没有统一的 role framing、behavioral contract、project context 注入）。OpenSpec 项目已经验证了一套 XML-structured prompt 格式（`<task>` `<project_context>` `<rules>` `<dependencies>` `<template>` `<instruction>`），Mohist 的 plan stage 已经部分采用，但 build/review/skill stage 完全没有统一。

## What Changes

- **新增统一的 agent prompt 组装函数** (`formatAgentPrompt`)：所有 stage 的 prompt 都经过同一个函数，输出标准 `<mohist-task>` XML 格式
- **build stage prompt 从 inline blob 重构为 XML-structured**：proposal/design 改为 `<context-files>` 引用（agent 按需读取），spec 保持 inline `<spec>`，task 用 `<task>` 包裹，commit 指令提升为 `<contract>`
- **新增 project-level agent config**：`workflow.yaml` 扩展 `agent.context`（tech stack、build/test commands）和 `agent.rules`（per-stage 约束），注入到每个 prompt 的 `<project_context>` 和 `<rules>`
- **review/conflict/auto-fix prompt 统一格式**：现有 markdown instruction 变成 `<instruction>` 内容，用 `formatAgentPrompt` 包装
- **learnings 从 inline 改为 file reference**：放在 `<context-files>` 里，agent 按需读取

## Capabilities

### New Capabilities
- `agent-prompt-schema`: 统一的 agent prompt XML 格式定义和组装函数
- `project-agent-config`: workflow.yaml 的 agent context/rules 配置加载

### Modified Capabilities
- `ralph-task-execution`: build stage task prompt 组装逻辑（context-assembler.ts）
- `agent-spec-review`: review stage prompt 组装逻辑（artifact-prompt.ts）

## Impact

- `packages/cli/src/agents/artifact-prompt.ts` — 重写所有 buildXxxPrompt 函数
- `packages/cli/src/openspec/context-assembler.ts` — 重写 buildTaskContext
- `packages/cli/src/workflow/workflow-loader.ts` — 新增 agent config 加载
- `packages/cli/src/agents/prompts/*.md` — 审查/冲突修复 prompt 统一格式
- 所有依赖 `buildTaskContext` 或 `buildArtifactPrompt` 的调用点需要适配
- **BREAKING**: prompt 格式变更，已有测试需要更新
