## Why

E2E walkthrough 暴露了 3 个阻断 Build stage 的 bug。最严重的是 Ralph executor 因 `tasks.json` 缺少 `attempts` 字段而跳过所有任务（NaN 比较），导致 Build stage 从未实际执行任何任务。次要问题是 workflow_log 外键约束失败（issueId 格式不匹配）和 CLI 不显示审批状态，降低了可观测性。

深入诊断发现：FK 失败有两个根源 —— workflow-controller 使用 `issue.number`（数字字符串），**以及** ralph-executor 使用 `logIssueId`（同样基于 number）。两者都需要修复。

## What Changes

- 修复 `readTasks()` 为缺失的 `attempts` 和 `passes` 字段补默认值，防止 NaN 算术导致 for 循环不执行
- 修复 Ralph loop skip 逻辑：未执行的任务应标记为 `passes: false` 而非 `passes: true`
- 修复 workflow_log issueId 格式：
  - `workflow-controller` 使用 `issue.id`（UUID）而非 `String(issue.number)`
  - `ralph-executor` 分离 `sseIssueId`（number，用于 eventBus）和 `logIssueId`（UUID，用于 workflow_log）
- 修复 `mo issue show` 命令显示 approvalState 信息（所有状态，不只是 awaiting）

## Capabilities

### New Capabilities
- `task-defaults`: tasks.json 读取时的 schema 默认值填充和验证，确保 Ralph executor 的数值运算不会因缺失字段产生 NaN
- `issue-show-approval`: CLI `mo issue show` 命令渲染审批状态信息

### Modified Capabilities
- `ralph-executor`: 修复 skip 逻辑中 passes 被错误设为 true 的问题；修复 workflowLogRepo 和 eventBus 使用不同 issueId 格式的问题

## Impact

- `packages/cli/src/openspec/ralph-executor.ts` — readTasks(), runRalphLoop() skip 逻辑, logIssueId/sseIssueId 分离
- `packages/cli/src/workflow/workflow-controller.ts` — runPipelineBuildStage() issueId 格式
- `packages/cli/src/cli/commands/issue.ts` — show 命令输出
