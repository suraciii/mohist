## Why

E2E walkthrough 第 3 轮中，Plan stage 前 3 个 artifact（proposal、specs、design）成功生成，但 tasks.json 缺失。对比 OpenSpec OPSX propose skill 的可靠性，根因是 Mohist 的 `buildArtifactPrompt()` 生成的 prompt 缺乏结构化约束：没有模板骨架、没有依赖文件路径的显式引用、没有强制写入指令。agent 在多轮长会话后期缺乏足够的"锚点"来确保执行写文件操作。

## What Changes

- **改进 `buildArtifactPrompt()` 的 prompt 结构**：借鉴 OPSX propose skill，每个 artifact 的 prompt 包含结构化的 template（输出骨架）、dependencies（依赖文件路径）、output（输出路径），使用 XML 标签分区
- **增强所有 artifact 的 prompt 模板**：统一添加显式写文件指令和步骤列表
- **添加 per-round 重试机制**：verify 失败后发送更强制性的 retry prompt

## Capabilities

### New Capabilities

（无）

### Modified Capabilities

- `agent-spec-generation`: `buildArtifactPrompt()` 生成结构化 prompt（template + dependencies + output 分区）
- `agent-spec-generation`: 所有 artifact prompt 模板添加显式写文件步骤
- `pipeline-model`: workflow-controller 增加每轮次验证和重试机制

## Impact

- **代码**: `src/agents/artifact-prompt.ts`, `src/agents/prompts/artifacts/*.md`, `src/workflow/workflow-controller.ts`
- **APIs**: 无变化
- **依赖**: 无变化
- **系统**: 提高 Plan stage 所有 artifact 的生成可靠性
