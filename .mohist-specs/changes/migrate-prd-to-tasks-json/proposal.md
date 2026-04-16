## Why

mohist 当前用 `prd.json` 存放 Plan 阶段产出的任务清单，但 `prd` 这个名字暗示产品需求文档，实际内容是执行层面的任务定义。同时，字段中存在多个冗余字段（`capability`、`requirement_ref`、`estimated_effort`）从未被消费方使用。将任务清单重命名为 `tasks.json` 并精简 schema，使命名与职责对齐。

## What Changes

- 任务清单文件从 `prd.json` 改名为 `tasks.json`
- 运行时状态（passes/attempts/error）合并到 tasks.json 的每个 task 上，删除 `task-status.json`
- 字段精简：砍掉 `capability`、`requirement_ref`、`estimated_effort`
- 字段重命名：`acceptance_criteria` → `acceptanceCriteria`，`spec_file` → `spec`（使用 anchor link 风格如 `specs/search/spec.md#REQ-001`）
- 所有读写 `prd.json` 的代码改为读写 `tasks.json`
- `PrdTask` / `PrdJson` 类型重命名为 `Task` / `TasksFile`
- 删除 `task-status.ts` 工具（update_task_status / get_task_status）

## Capabilities

### New Capabilities

- `tasks-json-schema`: tasks.json 文件的 schema 定义和读写接口

### Modified Capabilities

- `ralph-executor`: 从读 prd.json 改为读 tasks.json
- `context-assembler`: Task 类型适配新字段名
- `detector`: OpenSpecChange.prdPath → tasksPath

## Impact

- `packages/cli/src/artifacts/change-artifacts-manager.ts` — PrdJson/PrdTask 类型 + readPrd/writePrd 方法
- `packages/cli/src/openspec/detector.ts` — OpenSpecChange.prdPath
- `packages/cli/src/openspec/context-assembler.ts` — Task interface
- `packages/cli/src/openspec/ralph-executor.ts` — readPrdTasks
- `packages/cli/src/tools/read-prd.ts` — 整个工具重命名为 read-tasks
- `packages/cli/src/tools/self-review.ts` — generatePrd → generateTasks
- `packages/cli/src/tools/task-status.ts` — 不直接读 prd.json，不受影响
- `packages/cli/src/agents/planner-agent.ts` — 产出 tasks.json 而非 prd.json
- `packages/cli/src/agents/main-agent.ts` — 引用新工具名
