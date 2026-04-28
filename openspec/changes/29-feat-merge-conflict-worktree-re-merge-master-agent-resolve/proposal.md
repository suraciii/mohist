## Why

MergeQueue 遇到 mergeBack 冲突时，issue 卡在 `mergeState=conflict` 无自动恢复路径，必须人工介入。这阻塞了后续 issue 的合并队列，也违背了 mohist 自主完成工作流的目标。

## What Changes

- `MergeState` 枚举新增 `resolving` 状态
- MergeQueue 冲突时 abort master merge，在 worktree 中反向 merge master，将冲突留在 worktree
- issue 回退到 `build` stage（`mergeState=resolving`），触发 agent pipeline
- WorkflowController 在 build stage 识别 `mergeState=resolving`，使用 conflict resolution prompt 让 agent 解决冲突
- agent 解决冲突后 commit，走 review → done → 重新 enqueue MergeQueue（此时应无冲突）
- 冲突解决最多重试 3 次，超过后标记 `blocked` 等待人工

## Capabilities

### New Capabilities

- `merge-conflict-resolution`: MergeQueue 冲突检测 → worktree 内反向 merge → agent 自动解决 → 重试合并的完整流程

### Modified Capabilities

- `worktree-manager`: 需支持在 worktree 中执行 `git merge master`（将 master 变更加入 worktree 的 issue 分支）
- `pipeline-model`: build stage 需识别 `mergeState=resolving` 并走 conflict resolution 路径而非普通 build
- `event-bus`: 新增 `merge_conflict_requiring_resolution` 事件类型

## Impact

- **代码**: `merge-queue.ts`、`workflow-controller.ts`、`server/index.ts`、`agent prompts`、`types/index.ts`、`db/migrations.ts`
- **数据**: SQLite migration 新增 `resolving` mergeState 值（如需显式存储）
- **依赖**: 无新外部依赖
- **风险**: worktree 中 re-merge master 本身也可能冲突（master 又合了新内容），需递归处理但限制最大重试次数
