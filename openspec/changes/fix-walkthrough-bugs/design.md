## Context

E2E walkthrough（`talks/2026-04-22-e2e-walkthrough.md`）暴露了 3 个 bug，其中 1 个严重阻断 Build stage。当前状态：

- **Plan stage 正常工作**：4/4 产物全部成功生成（之前的修复已生效）
- **Build stage 完全无法运行**：Ralph loop 因 NaN 算术跳过所有任务
- **可观测性不足**：workflow_log 丢失事件、CLI 不显示审批状态

深入诊断发现 workflow_log FK 失败有两个根源：
1. workflow-controller 使用 `String(issue.number)`（数字）作为 issueId
2. ralph-executor 的 `logIssueId` 也是基于 number（因为 `sseIssueId` 和 `logIssueId` 混用了同一个值）

受影响文件：
- `ralph-executor.ts` — readTasks() + skip 逻辑 + issueId 分离
- `workflow-controller.ts:379` — issueId 格式
- `cli/commands/issue.ts` — show 命令

## Goals / Non-Goals

**Goals:**
- 让 Build stage 能正常执行任务（修复 NaN 问题）
- 让 workflow_log 正确记录事件（修复两处 FK 失败）
- 让用户能通过 CLI 看到审批状态

**Non-Goals:**
- 不改变 tasks.json 的 agent prompt（让 agent 生成 `attempts` 字段是另一回事）
- 不重构 Ralph executor 的整体架构
- 不改变审批流程本身
- 不统一整个代码库的 issueId 惯例（Plan stage eventBus 仍使用 number）

## Decisions

### D1: 在 readTasks() 中填充默认值

**选择**: 在 `readTasks()` 返回时 normalize 每个任务对象。

**替代方案**:
- A) 在 for-loop 入口处 `const baseAttempts = nextTask.attempts ?? 0` — 只修一个症状
- B) 在 `Task` 接口层用 class/validator — 过度工程

**理由**: `readTasks()` 是唯一入口，在这里 normalize 确保所有下游代码（不只是 for-loop）都拿到安全的数据。最小改动，最大覆盖。

### D2: skip 逻辑中 passes 设为 false

**选择**: 将 501 行的 `passes: true` 改为 `passes: false`。

**理由**: 未执行的任务标记为 passed 是逻辑错误。虽然 `failed++` 已经计数，但 tasks.json 中 `passes: true` 会误导后续逻辑（如重新运行时 `findNextPendingTask` 会跳过它）。

### D3: workflow-controller 使用 issue.id

**选择**: 将 `const issueId = String(issue.number)` 改为 `const issueId = issue.id`。

**理由**: workflow_log.issue_id 有外键约束引用 issues(id)，issues(id) 是 UUID。直接使用 UUID 即可。

### D4: ralph-executor 分离 sseIssueId 和 logIssueId

**选择**: 将 `sseIssueId` 和 `logIssueId` 从同一个值分离为两个独立变量：
- `sseIssueId = String(context.issueNumber ?? '')` — 用于 eventBus emit（前端/UI 可能期望 number）
- `logIssueId = context.issueId || ''` — 用于 writeTaskLog 和 log.info（workflow_log 需要 UUID）

**理由**: `logIssueId` 当前混用 `sseIssueId`（number），导致 workflow_log FK 失败。分离后：
- eventBus 继续用 number（不改变已有行为）
- workflow_log 用 UUID（修复 FK 失败）

### D5: CLI show 命令显示所有 approvalState 状态

**选择**: 渲染所有 approvalState 状态（awaiting/approved/rejected），而不只是 awaiting。

**理由**: 用户需要知道 issue 是否已通过或已被拒绝审批，不只是等待审批。

## Risks / Trade-offs

- **[风险] readTasks 默认值掩盖了 prompt 问题** → 治标不治本。但防御性编程是合理的，agent 生成的 JSON 不应信任完整性。后续可单独改进 tasks prompt 让 agent 也生成 `attempts` 字段。
- **[风险] workflow-controller issueId 改动影响范围** → 只影响 `runPipelineBuildStage` 内部的日志写入，plan stage 使用不同的代码路径（ACP multi-round session），不受影响。
- **[架构债务] Plan stage 和 Build stage 使用不同的 issueId 惯例** → Plan stage eventBus emit 使用 number（`String(acpOptions.issueNumber ?? '')`），Build stage 现在统一使用 UUID。这不是这个 change 要解决的问题，但值得记录。未来应考虑统一整个代码库的 issueId 格式。
