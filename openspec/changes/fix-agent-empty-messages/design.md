## Context

`runAgentLoop`（`agent-loop.ts`）使用 Vercel AI SDK 的 `streamText` API。新 session 创建时 `messages` 为空数组（`session.ts:27`），首次调用 `streamText({ system, messages: [] })` 时，SDK 要求 messages 非空，抛出 `InvalidPromptError`。

报错后 `executeAgent` 的 catch 块将 issue 状态设为 `Blocked`，但**不回滚 stage**。由于 `agentRunner.start()` 是 fire-and-forget（异步 Promise），start 端点的 catch 块不会执行，stage 停留在 `Plan`。用户 reopen 后只改 status 为 `Active`，stage 仍在 `Plan`，前端不显示 Start 按钮，issue 卡住。

根因链：start 端点提前改 stage → agent 异步失败 → catch 只改 status 不回滚 stage → reopen 只改 status → 死路。

## Goals / Non-Goals

**Goals:**
- 修复 `messages must be empty` 报错，新 session 首次调用 AI SDK 正常工作
- agent 执行失败时回滚 stage 到 Draft，确保 issue 不会卡在中间状态
- reopen 后能恢复流程：有 pausedSession 时自动 resume，无 pausedSession 时重置 stage 允许重新 start

**Non-Goals:**
- 不持久化 pausedSession（内存 Map 丢失问题是更大范围的重构，不在本次修复范围）
- 不改变 stage 状态机的转换规则

## Decisions

### 1. 注入初始消息而非改用 `prompt` 参数

**选择**：在 `runAgentLoop` 中当 `messages` 为空时，注入一条 `{ role: 'user', content: '...' }` 消息。

**替代方案**：改用 `streamText({ prompt })` 而非 `messages`。

**理由**：使用 `messages` 保持一致性，且后续 resume 场景也依赖 messages 数组。注入消息后 session 历史完整，对 agent 行为无副作用。

### 2. agent 失败时回滚 stage 到 Draft

**选择**：在 `executeAgent` 的 catch 块中，除了将 status 设为 `Blocked`，还将 stage 回滚到 `Draft` 并清除 `approval_state`。

**替代方案**：不改 executeAgent，只在 reopen 端点中做兜底清理。

**理由**：在失败源头就清理干净，避免 issue 进入不一致状态（stage=Plan + status=Blocked）。这让 reopen 逻辑更简单——只需改 status。两层防护：executeAgent catch 做首次清理，reopen 做兜底清理。

### 3. reopen 端点增加恢复逻辑

**选择**：reopen 时：
1. 改 status `Blocked → Active`
2. 检查 `agentRunner.hasPausedSession(number)`，有且 agent 未运行则自动 `agentRunner.resume()`
3. 无 pausedSession 时，将 stage 重置为 `Draft` 并清除 `approval_state`

**替代方案**：无 pausedSession 时直接重新 `start`（跳过 Draft 校验）。

**理由**：重置到 Draft 更安全，用户可以重新走完整流程。作为 executeAgent catch 的兜底，确保即使 catch 清理失败，reopen 也能把 issue 拉回可操作状态。

## Risks / Trade-offs

- [Stage 重置丢失进度] → 可接受：issue 本身因报错卡住，之前的 stage 输出（如 openspec 文件）仍在磁盘上不会丢失，重新 start 会复用已有产物
- [初始消息内容影响 agent 行为] → 消息内容保持通用："Start working on the current issue"，与 system prompt 配合引导 agent 自主调用 `read_workflow`
- [approval_state 残留导致再次卡住] → 必须在重置 stage 为 Draft 时同步调用 `clearApprovalState`，否则 `agentRunner.start()` 会检测到 pending approval 并拒绝启动
