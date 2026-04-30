## Why

`POST /api/issues/:number/start`（及 reopen/approve/reject/messages 共 5 个 endpoint）在 `projectService.getById()` 返回 null 时静默跳过 worktree 创建，导致 agent 直接在主仓库 `process.cwd()` 下工作。Issue #10 就因此将 openspec changes 写入主仓库而非隔离 worktree，后续无法 merge-back 或跟踪 diff。

## What Changes

- **start endpoint**：增加 project null 检查，返回 404 而非静默跳过 worktree 创建
- **reopen endpoint**：同上
- **approve endpoint**：同上
- **reject endpoint**：同上
- **messages endpoint**：同上
- 所有 5 个 endpoint 统一增加 warn 日志，当 project 为 null 或 worktreeManager 不可用时记录

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- **http-api**: start/reopen/approve/reject/messages endpoint 需在 project 为 null 时返回 404 错误，而非静默使用 `process.cwd()` 作为 worktreePath

## Impact

- `packages/cli/src/api/issues.ts`：5 个 POST handler 的 project null 检查和日志
- 无 breaking change（当前静默跳过是 bug，修复后行为更严格但更正确）
- 无依赖变更
