## Context

mohist 的 `AgentRunnerService.recoverIssues()` 和 pipeline 执行失败路径中，当 agent 异常退出或部分 task 完成时，系统直接将 issue 标记为 `blocked`，并将详细原因写入日志（如 `status=blocked, 2/8 tasks completed, T-003...T-008 pending`）。用户在前端只看到一个红色 "Blocked" badge 和一个 "Reopen" 按钮，无法得知：
1. 为什么 blocked
2. 做了什么时出了问题
3. 下一步该做什么（reopen？重新开始？从断点恢复？）

当前数据库 schema version 为 15，issues 表已有 `conflict_retry_count` 和 `model` 等扩展列，但没有 `blocked_reason` 或 `retry_count` 字段。EventBus 已有 `agent_error` 事件但无 `agent_blocked` 事件。前端 Issue 类型（`web/src/lib/types.ts`）不含 `blockedReason` 或 `retryCount`。

## Goals / Non-Goals

**Goals:**
- 瞬态故障（agent 崩溃、部分 task 完成）时自动重试，用户无感
- 自动重试耗尽或不可重试故障时，将人话原因持久化并展示给用户
- 提供明确的恢复路径：retry（从断点继续）和 restart（丢弃重来）
- 前端实时响应 blocked 状态变化

**Non-Goals:**
- 不实现时间线/审计日志 UI（"查看详情"展开技术细节不在本次范围）
- 不实现 retry 间隔退避的定时器（server restart 时的 recover 是同步的，用 retryCount 判断是否继续）
- 不修改 agent 内部的 LLM hang detection/recovery 机制（已有 `coder_recovery_status` 事件处理）
- 不修改 merge queue 的 blocked 逻辑（已有 `merge_blocked` 事件和 `conflictRetryCount`）

## Decisions

### D1: 自动重试仅发生在 server restart recover 路径

pipeline 执行中的失败（`runPipeline` 的 catch 块）不自动重试。原因：pipeline 运行时 agent 已在后台，重试需要重新 spawn 子进程，与现有的 pipeline 执行循环（WorkflowController 已有 loop 机制）冲突。

自动重试只在 `recoverIssues()` → `recoverBuildStageIssue()` 中生效：server 重启时发现有部分 task 完成的 active issue，尝试恢复 pipeline 执行。

**Alternatives considered:**
- 在 pipeline catch 块中自动重试 — 风险高，pipeline 内部已有自己的循环逻辑（ralph executor），加一层重试会导致嵌套重试难以控制
- 使用定时器延迟重试 — 增加复杂度，server restart recover 是同步的，直接判断 retryCount 即可

### D2: blocked_reason 写入时机统一为 "标记 blocked 时"

所有调用 `issueRepo.updateStatus(id, Blocked)` 的地方都应同时调用 `issueRepo.updateBlockedReason(id, reason)`。将这两个操作封装为 `IssueRepo.blockIssue(id, reason)` 方法，避免遗漏。

**Alternatives considered:**
- 在 updateStatus 层面拦截 — 太底层，不是所有 Blocked 状态转换都需要 reason
- 使用 issueRepo.update(id, { status: Blocked, blockedReason: reason }) — 已有通用 update 方法，但每次调用需手动传两个参数，容易遗漏

### D3: retry 端点复用现有的 resumePipeline 逻辑

`POST /api/issues/:number/retry` 的实现与现有 reopen 端点的 pipeline resume 路径一致：设置 status 为 active → 调用 `agentRunner.resumePipeline()`。区别在于 retry 不重置 stage（保持 build），而 reopen 重置 stage 到 Draft。

**Alternatives considered:**
- 新增 AgentRunnerService.retryFromCheckpoint() 方法 — 与 resumePipeline 逻辑高度重复，不值得新建方法

### D4: 前端使用 SSE `agent_blocked` 事件驱动实时更新

新增 `agent_blocked` 事件到 EventBus EventMap，在 SSE ALL_EVENT_TYPES 数组中注册。前端 IssueDetailPage 已监听 SSE 事件刷新数据，无需新增 SSE 监听逻辑，只需在收到 `agent_blocked` 后触发数据 refetch。

**Alternatives considered:**
- 复用 `agent_error` 事件 — payload 结构不同，且 error 事件不一定导致 blocked
- 轮询 — 延迟高，已有 SSE 基础设施

### D5: 前端 blocked 面板为 IssueDetailPage 内联组件

不新建独立页面或弹窗，直接在 IssueDetailPage 的 actions 区域（当前 Reopen 按钮位置）替换为 BlockedPanel 组件。该组件包含 reason 文本、进度提示、Retry 和 Restart 按钮。

**Alternatives considered:**
- 弹窗/Dialog — 阻断用户查看 issue 详情，体验差
- 独立页面 — 过重，用户只是需要看原因和点按钮

## Risks / Trade-offs

- **[自动重试导致长时间运行]** → recover 时最多重试 3 次，每次都是完整 pipeline 执行。Server restart 后可能长时间运行 agent。Mitigation：retryCount 持久化，重试耗尽后不再自动重试。
- **[blockIssue 封装遗漏]** → 如果有代码路径直接调用 `updateStatus(Blocked)` 而不通过 `blockIssue()`，blockedReason 会缺失。Mitigation：全局搜索所有 `updateStatus.*Blocked` 调用点并统一改为 `blockIssue()`。
- **[DB migration 兼容性]** → 新增两个 nullable 列，对已有数据无影响。Migration 为 ALTER TABLE ADD COLUMN，向前兼容。
- **[前端 blocked reason 为空]** → 旧数据没有 blockedReason。Mitigation：前端显示默认提示文案。

## Migration Plan

1. **DB Migration（v16）**：`ALTER TABLE issues ADD COLUMN blocked_reason TEXT DEFAULT NULL` + `ALTER TABLE issues ADD COLUMN retry_count INTEGER DEFAULT 0`。幂等，可重复执行。
2. **后端部署**：新增字段和 API 端点向后兼容，旧版前端不受影响（忽略未知字段）。
3. **前端部署**：前端同时依赖新 API 字段和端点，需在后端部署后部署。
4. **回滚**：新增列和 API 端点为 additive，回滚只需还原前端。后端回滚安全（旧代码忽略新列）。

## Open Questions

- 是否需要在 `recoverIssues()` 中对非 build stage 的 issue 也增加自动重试（当前只对 build stage 有 recover 逻辑）？建议先只做 build stage，后续扩展。
