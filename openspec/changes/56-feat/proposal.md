## Why

Agent task timeout 超时后，已完成的代码修改作为未提交的文件残留在 worktree 中，重试时被忽略，导致 agent 必须从零重新实现。Issue #41 的 T-001 在超时前已修改 types/index.ts、merge-queue.ts、worktree-manager.ts，但这些工作全部丢失。现有的 pipeline checkpoint 只记录 task 级完成状态，不保存文件级进度。

## What Changes

- 超时触发前，在 agent 进程被 kill 之前自动执行 `git add -A && git commit -m "WIP: T-XXX timeout"` 保存当前工作进度
- 超时 task 的 retry 逻辑从「不可重试」改为「有 WIP commit 则可重试」，重试时恢复到 WIP commit 状态而非从头开始
- 重试 prompt 注入 WIP commit 信息（已修改的文件列表、diff 摘要），让 agent 从断点继续
- 用户验收时，WIP commit 与正常 commit 统一对待，保留为最终实现

## Capabilities

### New Capabilities

- `wip-commit` — WIP commit 生命周期：超时前自动创建、重试时恢复、验收时保留

### Modified Capabilities

- `ralph-task-execution` — timeout 失败类型改为有条件重试（存在 WIP commit 时可重试）；重试 prompt 注入已有进度上下文
- `worktree-manager` — 新增 WIP commit 创建、查询、恢复操作

## Impact

- `packages/cli/src/agent-runtime/acp-session.ts` — 超时处理逻辑增加 pre-kill WIP commit 步骤
- `packages/cli/src/openspec/ralph-executor.ts` — timeout 失败分类改为条件重试；重试时检测并恢复 WIP commit
- `packages/cli/src/git/worktree-manager.ts` — 新增 `createWipCommit()`、`findWipCommit()`、`getWipDiffSummary()` 方法
- `openspec/specs/ralph-task-execution/spec.md` — 更新 failure categories 表中 timeout 的重试策略
- `openspec/specs/worktree-manager/spec.md` — 新增 WIP commit 相关 scenario
