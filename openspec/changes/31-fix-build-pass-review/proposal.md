## Why

`recoverBuildStageIssue()` 对 all-pass 分支只调用了 `updateStage(Review)` 但没有启动 review pipeline，也没有设置 `pendingGate`。这导致 issue 进入 `Active + Review` 状态后完全不可恢复——用户无法 reopen、approve 或 start，只能手动改 DB。这是一个通过 E2E walkthrough 可靠复现的死锁 bug。

## What Changes

- 在 `recoverBuildStageIssue()` 的 all-pass 分支中，调用 `startPipeline()` 启动 review stage 的 pipeline，让 review agent 正常运行并最终停在 awaiting approval gate 上。
- 确保 `pendingGate` 和 `approvalState` 在恢复路径中被正确设置。

## Capabilities

### New Capabilities

### Modified Capabilities

- `error-resilience` — 恢复逻辑的正确性是 error resilience 的核心部分，all-pass 分支需要与正常 pipeline 路径行为一致。

## Impact

- `packages/cli/src/services/agent-runner-service.ts` — `recoverBuildStageIssue()` 方法（第 233-240 行）
- 恢复路径需要访问 `resumePipeline()` 所需的依赖（projectId, worktreePath, acpOptions 等），部分已在该方法中解析，可能需要补充
