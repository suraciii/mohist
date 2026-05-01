## Context

OpenSpec change 归档存在三个问题：

1. **时机过晚**：`server/index.ts:262` 在 `merge_completed` 事件后才标记 `Stage.Done`，但 openspec 归档发生在更晚的 `IssueService.performCleanup()`（用户手动 archive issue 时）。这意味着 issue done → openspec 归档之间有不定长度的时间窗口。
2. **路径不一致**：`ChangeArtifactsManager.archiveChange()` 归档到 `openspec/changes/archive/<name>`（无日期前缀），`archive_change` tool 归档到 `openspec/archive/YYYY-MM-DD-<name>`。
3. **Zombie code**：`src/tools/archive-change.ts` 的 `createArchiveChangeTool()` 从未在任何 agent 中注册，是完全的 dead code。

关键数据流：`CheckStageRunner.run()` → 所有 check 通过 → `requiresApproval: true` → `WorkflowEngine` 设置 approval gate → 用户 approve → `Stage.Done` → `merge_completed` 事件 → 当前无归档。

## Goals / Non-Goals

**Goals:**
- 在 check stage 所有 checks 通过后、设置 approval gate 之前，自动归档 openspec change
- 统一归档路径格式为 `openspec/changes/archive/YYYY-MM-DD-<name>/`，含冲突处理
- 删除 `archive_change` tool 和 issue archive 中的重复归档逻辑

**Non-Goals:**
- 不修改 Stage 枚举或 stage transition 逻辑
- 不实现 spec sync 到 `openspec/specs/`
- 不修改 `WorkflowEngine.run()` 的循环结构
- 不修改 `restoreChange()` 的行为（issue unarchive 仍尝试恢复）

## Decisions

### D1: 归档触发点在 CheckStageRunner 内部

在 `CheckStageRunner.run()` 中，所有 checks 通过后、返回 `requiresApproval: true` 之前，调用 `ctx.artifactManager.archiveChange()`。

**理由**：
- 归档是 check stage 的职责之一，归档发生在 "checks 通过" 和 "等待用户 approval" 之间
- `WorkflowEngine` 无需知道归档逻辑，保持 stage runner 的封装性
- archive 产生的文件变动（从 `changes/` 移到 `changes/archive/`）不会影响 check 结果，因为 checks 已经全部通过

**Alternatives considered:**
- 在 `WorkflowEngine` 中处理：需要 engine 知道 check stage 的内部状态，打破 runner 封装
- 在 `server/index.ts` 的 `merge_completed` handler 中处理：时机太晚，merge 是独立异步流程

### D2: ChangeArtifactsManager.archiveChange() 添加日期前缀和冲突处理

修改 `archiveChange()` 生成 `YYYY-MM-DD-<name>` 格式的目标目录名，并检测已存在的同名目录，自动追加 `-v2`, `-v3`。

同时更新 `restoreChange()` 以适配新格式：
- 搜索归档条目时使用包含匹配而非 `startsWith`（`2026-05-01-42-fix-auth` 不以 `42-` 开头）
- 恢复时剥离日期前缀，目标目录为 `changes/42-fix-auth` 而非 `changes/2026-05-01-42-fix-auth`

**理由**：
- 当前实现直接用 `<name>` 无日期前缀，无法区分同一 change 的多次归档
- `archive_change` tool 已有类似冲突处理逻辑可参考（但归档到错误路径 `openspec/archive/`）
- `restoreChange()` 当前用 `startsWith('${issueNumber}-')` 搜索，不兼容日期前缀格式

**Alternatives considered:**
- 用时间戳而非日期前缀：人类不可读，不符合 OpenSpec 官方格式

### D3: StageContext 接口需要扩展以支持归档

`StageContext` 当前通过 `artifactManager` 提供 `ChangeArtifactsManager` 接口，但接口类型 `ChangeArtifactsManager`（在 `stage-context.ts` 中）只暴露了部分方法，不包含 `archiveChange()`。

需要在 `stage-context.ts` 的 `ChangeArtifactsManager` 接口中添加 `archiveChange(issueNumber: number): Promise<void>` 方法声明。

### D4: IssueService.performCleanup() 移除 openspec 归档

从 `IssueService.performCleanup()` 中删除 `artifactsManager.archiveChange()` 调用。Issue archive 只负责：标记 `archivedAt`、清理 worktree、清理 checkpoints。

**理由**：
- 归档已由 check stage 自动完成
- 避免二次移动（change 已在 `changes/archive/` 中，再次调用会报错或无操作）

### D5: 删除 archive-change.ts 整个文件

`createArchiveChangeTool` 是唯一导出，且无任何文件 import 它。整个文件可以安全删除。

## Risks / Trade-offs

- **[归档后用户拒绝 approval]** → 归档发生在 approval 前，用户拒绝后 change 已在 `archive/` 中。但 `restoreChange()` 方法可以将 change 恢复到 `changes/`，build stage loop-back 时需要配合调用 restore。当前 `WorkflowEngine` 的 loop-back 逻辑（`nextStage: Stage.Build`）不会自动 restore，需要确认：如果 check 失败后 loop back to build，change 仍在原位（因为只有 allPassed 才会归档）。→ **无风险**：只有全部通过才归档，通过后不会再 loop back。
- **[归档失败导致 check stage 中断]** → `archiveChange()` 可能因文件系统权限等原因失败。应在 `CheckStageRunner` 中 catch 归档错误，记录日志但不阻塞 check stage 流程（降级为不归档，由 issue archive 兜底）。
- **[archive/ 下的 change 与 findChangeDir() 冲突]** → `findChangeDir()` 只搜索 `changes/` 直接子目录，不搜索 `changes/archive/`，因此归档后的 change 不会被误找到。→ **无风险**。

## Migration Plan

1. 修改 `ChangeArtifactsManager.archiveChange()` 添加日期前缀和冲突处理，同步更新 `restoreChange()` 搜索和恢复逻辑
2. 扩展 `stage-context.ts` 的 `ChangeArtifactsManager` 接口添加 `archiveChange`
3. 修改 `CheckStageRunner.run()` 在 allPassed 后调用归档
4. 从 `IssueService.performCleanup()` 移除 `archiveChange` 调用
5. 删除 `src/tools/archive-change.ts`
6. 运行所有测试验证
7. 无需数据迁移：`openspec/archive/` 中已有的归档目录保持原样，新归档统一到 `openspec/changes/archive/`

## Open Questions

_None_
