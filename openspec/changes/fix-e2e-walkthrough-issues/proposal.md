## Why

E2E walkthrough 暴露了 6 个问题，其中 3 个是严重级别：审批门禁状态仅存内存导致服务器重启后无法审批、build 阶段代码未提交到 git、recoverIssues() 不区分审批等待与 agent 崩溃导致可能丢失已完成的工作。这些问题使得 pipeline 在生产环境（服务器重启、agent 崩溃）下不可靠。

## What Changes

- 将 `pendingGates` 从内存 Map 改为基于 DB `approval_state` 的查询，使审批门禁状态在服务器重启后仍可恢复
- 在 `recoverIssues()` 中增加审批等待状态的检测，对 awaiting 状态的 issue 恢复 pendingGates 而非重置到 Draft
- 在 build 阶段完成后增加 git commit 步骤，将 agent 的代码变更提交到 worktree
- 在 `IssueStatus` 枚举中增加 `Completed` 状态，pipeline 到达 done 阶段时设置 status 为 completed
- 在 ACP session 中改进 stream 关闭时序，减少 EPIPE 错误

## Capabilities

### New Capabilities

- `build-commit`: Build 阶段完成后的 git commit 自动化，确保 agent 代码变更被提交到 worktree

### Modified Capabilities

- `agent-runtime`: 恢复逻辑需要区分审批等待和 agent 崩溃，对 awaiting 状态恢复 pendingGates 而非重置
- `pipeline-model`: IssueStatus 枚举增加 Completed 值，done 阶段设置 status 为 completed
- `error-resilience`: ACP session stream 关闭时序改进，减少 EPIPE 错误

## Impact

- **DB Schema**: `issues` 表 status 列新增 `completed` 值（枚举扩展，无需 migration）
- **API**: `POST /api/issues/:number/approve` 端点改为优先查 DB fallback，不影响现有行为
- **CLI**: `mo issue list` 将显示 `completed` 状态；`mo issue show` 对 done issue 显示 completed
- **Workflow**: build 阶段末尾新增 git commit 步骤，依赖 worktree git 可用
- **Agent Runner**: `recoverIssues()` 行为变更，不再重置 awaiting 状态的 issue
