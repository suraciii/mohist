## Why

Mohist 当前的工作流基于 workflow.yaml 的 stage 驱动，但缺乏 OpenSpec Ralph 的**结构化任务分解**和**持久化上下文传递**能力。Agent 在 plan 阶段生成的设计是临时的，无法作为后续 build 阶段的持久化参考；build 阶段是粗粒度的一次性执行，无法像 Ralph 那样基于 specs 逐个完成任务并持续学习调整。

我们需要在保持 workflow.yaml 简洁性的同时，引入 OpenSpec 的核心概念：Change 作为 Plan 阶段产物、Agent 生成 Specs、Ralph-style 任务循环执行。

## What Changes

- **新增 Change 产物模型**：Plan 阶段生成 `.mohist-specs/changes/{issue-number}-{slug}/` 目录，包含 proposal.md、design.md、specs/ 和 prd.json
- **Plan 阶段扩展**：Agent 生成 proposal + design + specs，并在 plan 阶段内完成自我审查（最多 3 次迭代），通过后生成 prd.json
- **新增 Review 阶段**：作为 approval gate，人工审查 Change 产物，可以编辑修改，满意后进入 build
- **重构 Build 阶段**：Main-agent 驱动 Ralph-style 任务循环，逐个执行 prd.json 中的 task。失败时由 Mohist Agent 记忆失败原因，作为附加 prompt 传递给重试。支持从失败 task 恢复继续
- **扩展 Check 阶段**：包含自动测试（Agent 执行 npm test/lint）、人工验收（approval gate）和 Change 归档
- **新增 Specs 存储位置**：支持将 specs 放在项目目录（`.mohist-specs/`）下，随代码版本化
- **新增工具集**：`read_prd`, `read_spec`, `store_learning`, `load_learnings`, `update_task_status`, `get_task_status`

## Capabilities

### New Capabilities
- `change-artifacts`: Change 目录结构和产物生成，命名规则 `{issue-number}-{slug}`，冲突自动加 `-v2`, `-v3`
- `agent-spec-generation`: Agent 自动探索并生成 proposal/design/specs
- `agent-self-review`: Plan 阶段内的自我审查，最多 3 次迭代，Agent 自主判断通过
- `ralph-task-execution`: 基于 prd.json 的 Ralph-style 任务循环执行，Main-agent 驱动
- `session-memory`: 任务执行学习记录和传递，存储为文件，永久保留
- `specs-project-storage`: Specs 存储于项目目录并版本化
- `failure-recovery`: Build 阶段失败恢复，从失败 task 继续

### Modified Capabilities
- `check-stage`: 扩展为自动测试 + 人工验收 + 归档

## Impact

- **CLI**：新增 `mo propose <issue>` 命令启动 Plan 阶段，支持 `--force` 覆盖现有 Change
- **Agent**：main-agent 扩展支持 Ralph-style 任务循环、自我审查、失败原因传递
- **工具**：新增 6 个 workflow 工具用于管理 change 生命周期和任务执行
- **存储**：项目根目录新增 `.mohist-specs/` 目录存放 changes，需更新 .gitignore
- **Workflow**：默认 workflow 更新为 4 stages：plan → review → build → check
