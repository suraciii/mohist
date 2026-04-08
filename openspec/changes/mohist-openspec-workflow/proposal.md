## Why

Mohist 当前的工作流基于 workflow.yaml 的 stage 驱动，但缺乏 OpenSpec Ralph 的**结构化任务分解**和**持久化上下文传递**能力。Agent 在 plan 阶段生成的设计是临时的，无法作为后续 build 阶段的持久化参考；build 阶段是粗粒度的一次性执行，无法像 Ralph 那样基于 specs 逐个完成任务并持续学习调整。

我们需要在保持 workflow.yaml 简洁性的同时，引入 OpenSpec 的核心概念：Change 作为 Plan 阶段产物、Agent 生成 Specs、Ralph-style 任务循环执行。

## What Changes

- **新增 Change 产物模型**：Plan 阶段生成 `.mohist-specs/changes/{name}/` 目录，包含 proposal.md、design.md、specs/ 和 prd.json
- **新增 Review 阶段**：Agent 自动审查生成的 Specs 一致性，通过后生成 prd.json，然后等待人工审查
- **重构 Build 阶段**：从粗粒度一次性执行改为 Ralph-style 任务循环，逐个执行 prd.json 中的 task，支持 session-memories 传递学习
- **新增 Specs 存储位置配置**：支持将 specs 放在项目目录（`.mohist-specs/`）下，随代码版本化
- **新增工具集**：`explore_and_generate_specs`、`review_specs`、`generate_prd`、`execute_task`、`store_learning`

## Capabilities

### New Capabilities
- `change-artifacts`: Change 目录结构和产物生成
- `agent-spec-generation`: Agent 自动探索并生成 proposal/design/specs
- `agent-spec-review`: Agent 自动审查 Specs 一致性
- `ralph-task-execution`: 基于 prd.json 的 Ralph-style 任务循环执行
- `session-memory`: 任务执行学习记录和传递
- `specs-project-storage`: Specs 存储于项目目录并版本化

### Modified Capabilities
（无，现有 workflow 系统将被扩展而非修改需求）

## Impact

- **CLI**：新增 `mo propose <issue>` 命令启动 Plan 阶段，workflow.yaml 新增 review 阶段
- **Agent**：新增 explore-agent 用于生成 specs，main-agent 扩展支持 Ralph-style 任务循环
- **工具**：新增 5 个 workflow 工具用于管理 change 生命周期
- **数据库**：新增 session_memories 表存储任务学习
- **存储**：项目根目录新增 `.mohist-specs/` 目录存放 changes，需更新 .gitignore
