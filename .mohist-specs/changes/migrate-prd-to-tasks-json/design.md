## Context

mohist 的 Plan 阶段产出任务清单，当前文件名为 `prd.json`。该文件被以下消费方使用：

1. **RalphExecutor** — `readPrdTasks(prdPath)` 读取任务列表，按 `order` 排序后逐个执行
2. **ContextAssembler** — 读取 Task 的 `title`、`description`、`acceptance_criteria`、`spec_file` 拼装 prompt
3. **ChangeArtifactsManager** — `readPrd()`/`writePrd()` 方法读写文件
4. **read_prd tool** — 供 agent 查看 task 列表
5. **self-review tool** — `generatePrd()` 从 specs 生成 prd.json

当前运行时状态通过独立的 `task-status.json` 追踪，导致状态分散在两个文件中，增加了同步复杂度。实际生成的 prd.json 已经在 task 上使用 `passes` 字段。

当前 `PrdTask` interface 中 `capability`、`requirement_ref`、`estimated_effort` 三个字段从未被任何消费方使用。

## Goals / Non-Goals

**Goals:**
- 将 `prd.json` 改名为 `tasks.json`，使命名与职责对齐
- 精简 schema，砍掉未使用字段
- 统一字段命名为 camelCase
- 将运行时状态（passes/attempts/error）合并到 tasks.json 的 task 上
- 删除 `task-status.json` 及相关代码

**Non-Goals:**
- 不改变执行逻辑（RalphExecutor 仍然按 order 顺序执行）
- 不增加新字段（mode/type/output 等未来按需添加）
- 不改变 .mohist-specs 目录结构（B-063 目录迁移是独立变更）

## Decisions

### 新 schema：单文件，计划 + 状态合一

```jsonc
// tasks.json
{
  "version": 1,
  "tasks": [
    {
      "id": "T-001",              // 必须
      "title": "...",             // 必须
      "description": "...",       // 必须
      "order": 1,                 // 必须
      "acceptanceCriteria": [],   // 可选
      "spec": "specs/x/spec.md#REQ-001",  // 可选
      "dependsOn": [],            // 可选，文档性
      "passes": false,            // 必须，Plan 产出时默认 false
      "attempts": 0,              // 必须，Plan 产出时默认 0
      "error": null               // 可选，失败时填充
    }
  ]
}
```

### 删除 task-status.json

运行时状态直接写在 tasks.json 的每个 task 上，不再需要独立的 task-status.json。删除：
- `packages/cli/src/tools/task-status.ts` — 整个文件
- `detector.ts` 中的 `taskStatusPath` 字段
- `ralph-executor.ts` 中所有 task-status.json 的读写逻辑

RalphExecutor 直接在 tasks.json 上更新 passes/attempts/error。

### 类型重命名

- `PrdTask` → `Task`（含 passes/attempts/error 字段）
- `PrdJson` → `TasksFile`
- 删除 `TaskStatusFile`/`TaskStatusEntry`/`PrdTaskStatus` 等类型

### 工具重命名

- `read_prd` tool → `read_tasks` tool
- 删除 `update_task_status` tool — 状态更新由 RalphExecutor 内部直接操作 tasks.json
- 删除 `get_task_status` tool — 状态查询由 `read_tasks` tool 覆盖

## Risks / Trade-offs

- [Risk] 已有 change 目录下的 `prd.json` 文件需要迁移或兼容读取 → Mitigation: detector 同时检查 `tasks.json` 和 `prd.json`，优先读 `tasks.json`
- [Risk] 改动面涉及多个文件，需要确保所有引用一致 → Mitigation: TypeScript 类型系统会在编译时捕获遗漏
- [Risk] tasks.json 变为可变文件，git diff 中会包含运行时状态变更 → Acceptable: 每次执行后 git commit 会记录状态变化，增加可追溯性
