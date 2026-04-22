## Context

Plan stage 使用 multi-round ACP 连接，依次生成 proposal → specs → design → tasks → self-review。E2E walkthrough 中 tasks.json 未生成。

分析 OpenSpec OPSX propose skill 的可靠性来源：

1. **结构化指令**：每个 artifact 通过 `openspec instructions <artifact> --json` 获取包含 `template`（输出骨架）、`instruction`（指导文本）、`dependencies`（依赖文件路径 + 完成状态）、`outputPath`（输出路径）的 JSON
2. **显式依赖读取**：agent 被告知要读取哪些已完成的依赖文件，带完整路径
3. **模板填充模式**：template 是骨架文件，agent 往里填内容，而非从空白开始自由发挥
4. **强制性步骤**：每轮都是 "获取指令 → 读依赖 → 按模板写文件 → 验证" 的固定流程

Mohist 当前的 `buildArtifactPrompt()` 只拼了一段自由文本 prompt（issue 信息 + changeDir 路径 + 指导文本），缺少结构化约束。特别是 tasks 作为最后一个 artifact，此时 agent 已运行 10+ 分钟，需要更强的锚点。

## Goals / Non-Goals

**Goals:**
- 所有 artifact 可靠生成，不遗漏
- prompt 结构对齐 OPSX propose 的可靠模式
- 保留 multi-round ACP（共享上下文有利于后续 artifact 利用前置 artifact 信息）

**Non-Goals:**
- 不改为每 artifact 独立 ACP session
- 不改变 tasks.json 格式
- 不修改 Build/Check stage

## Decisions

### D1: 改进 buildArtifactPrompt() 输出结构化 prompt

**选择**: 修改 `buildArtifactPrompt()` 使其生成的 prompt 包含以下分区（使用 XML 标签）：

```
<task>
Create the {artifactType} artifact for change "{changeName}".
{artifact description from template}
</task>

<dependencies>
Read these files for context:
- {changeDir}/proposal.md (if exists)
- {changeDir}/specs/ (if exists)
- {changeDir}/design.md (if exists)
</dependencies>

<output>
Write to: {changeDir}/{artifact.outputFile}
</output>

<template>
{template skeleton content}
</template>

<instruction>
{detailed instructions from current .md file}
</instruction>
```

**理由**:
- OPSX propose skill 使用完全相同的分区模式（`<task>`, `<dependencies>`, `<output>`, `<template>`, `<instruction>`）
- XML 标签分区比自由文本更不容易被 agent 忽略
- `<output>` 标签明确告诉 agent 写到哪里
- `<template>` 给 agent 一个填充骨架而非从空白开始
- `<dependencies>` 告诉 agent 去读哪些已完成的 artifact，充分利用 multi-round 共享上下文的优势

**替代方案**: 只增强 tasks.md prompt 文本 → 自由文本在长会话中容易丢失焦点

### D2: 为每个 artifact 添加模板骨架文件

**选择**: 在 `prompts/artifacts/` 目录下为每个 artifact 添加对应的 template 文件：

- `templates/proposal.tpl.md` — proposal 输出骨架
- `templates/specs.tpl.md` — spec 输出骨架
- `templates/design.tpl.md` — design 输出骨架
- `templates/tasks.tpl.md` — tasks.json 输出骨架

`buildArtifactPrompt()` 读取模板文件内容，放入 `<template>` 标签。

**理由**:
- OPSX 的 `schema.yaml` 为每个 artifact 定义了 `template` 字段，指向骨架文件
- 模板填充比自由文本写作对 agent 来说更容易执行
- tasks.json 有固定 JSON 结构，模板骨架特别有效

**替代方案**: 把模板硬编码在 prompt 拼接逻辑里 → 不好维护，不如独立文件

### D3: 每轮次 verify 后添加重试

**选择**: 在 `workflow-controller.ts` 的 round 循环中，verify 失败后发送 retry prompt：

```
The artifact file {path} was not found. You MUST create it now.

Use the write_file tool to write the {artifactType} artifact to:
{changeDir}/{outputFile}

{artifact description summary}
```

**理由**:
- 立即发现，立即补救
- retry prompt 更短、更聚焦，降低 agent 忽略的概率
- 只重试一次，避免无限循环

**替代方案**: verify 失败直接报错 → 浪费了已有的上下文

### D4: 保留 multi-round ACP 会话

**选择**: 所有 artifact 和 self-review 共用同一个 ACP 会话。

**理由**:
- 共享上下文是优势：agent 在生成 tasks 时可以直接引用前面生成的 proposal、specs、design 内容
- OPSX propose 也是在一个 skill invocation 中连续生成所有 artifact
- 前三个 artifact 实际都成功了，问题出在 prompt 结构而非会话长度

## Risks / Trade-offs

- **模板骨架可能约束过死** → 对于简单变更，agent 可能觉得模板有些字段不适用。缓解：模板中用 HTML comment 标注可选字段
- **重试仍可能失败** → 如果 agent 确实无法执行写操作，重试也会失败。缓解：重试 prompt 尽量简短聚焦，给最大成功概率
- **所有 artifact prompt 都改** → 改动面比只改 tasks.md 更大。但这确保了所有 artifact 都有相同的可靠性保障

## Migration Plan

1. 创建 `prompts/artifacts/templates/` 目录和 4 个模板文件
2. 修改 `buildArtifactPrompt()` 生成结构化 prompt
3. 修改 `workflow-controller.ts` 添加 retry 逻辑
4. E2E walkthrough 验证
