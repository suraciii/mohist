## Why

当 issue 进入 blocked 状态时，用户只看到一个 "blocked" badge — 不知道为什么停了、不知道 agent 做了什么、不知道下一步怎么办。这个问题在 issues #12, #39, #40, #64, #86 等反复出现。recover 逻辑已有详细 reason，但只写日志不可见；瞬态故障（agent 崩溃、超时）本可自动重试却直接标记 blocked。Blocked 应该是一扇门（告知原因、低成本恢复），而不是一面墙。

## What Changes

- **自动重试瞬态故障**：agent 进程异常退出（exitCode=null）、部分 task 完成等场景，系统自动重试最多 3 次（带间隔退避），保留已完成进度，超过上限才标记 blocked
- **blocked_reason 持久化**：Issue 类型增加 `blockedReason` 字段，所有 recover/block 逻辑写入人话 reason（如 "Build 中断 — 完成了 2/8 个任务后 agent 进程异常退出"）
- **API 暴露 blocked reason**：`GET /api/issues/:number` 返回 `blockedReason`；新增 `POST /api/issues/:number/retry` 端点（从断点恢复）和 `POST /api/issues/:number/restart`（丢弃进度重新开始）
- **前端 blocked 状态交互重构**：blocked 状态下显示原因说明、进度保留提示、操作按钮（重试 / 重新开始 / 查看详情）

## Capabilities

### New Capabilities

- `blocked-auto-retry` — agent recover 逻辑对瞬态故障的自动重试机制（重试次数、退避策略、进度保留）
- `blocked-reason-storage` — blocked reason 的 DB 存储、写入时机和人话格式
- `blocked-recovery-api` — retry（从断点恢复）和 restart（重新开始）端点
- `blocked-state-ui` — 前端 blocked 状态下的原因展示、进度提示和操作按钮

### Modified Capabilities

- `local-issue-store` — Issue schema 增加 `blockedReason` 字段
- `http-api` — `GET /api/issues/:number` 返回 `blockedReason`；blocked issue 不再拒绝所有操作（retry/restart 可用）
- `web-ui` — IssueDetailPage blocked 状态 UI 重构
- `reopen-resume` — reopen 行为与新的 retry 机制协调（retry 从断点恢复，reopen 仍重置到 Draft）

## Impact

- `packages/cli/src/types/index.ts` — Issue interface 增加 `blockedReason`
- `packages/cli/src/db/issues-repo.ts` — 增加 `blocked_reason` 列和 migration
- `packages/cli/src/services/agent-runner-service.ts` — recover 逻辑增加自动重试 + 写入 blocked reason
- `packages/cli/src/api/issues.ts` — API 返回 blockedReason，新增 retry/restart 端点
- `packages/cli/web/src/components/IssueDetailPage.tsx` — blocked 状态 UI 重构
