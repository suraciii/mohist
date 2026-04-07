## Context

mohist M2 实现了 gate 暂停/恢复、ask_user 提问、消息注入、SSE 实时事件推送等交互能力。通过代码审查发现四个残留问题：

1. **ask_user 阻塞时状态不透明**: Agent 在 `ask_user` 工具中阻塞等待用户回答时，`AgentRunnerService` 仍将其标记为 `running`，不区分"正在执行工具"和"等待用户回答"。WebUI 和 mo attach 只能看到 "running"，无法展示 "等待回答" 状态。
2. **question 事件 projectId 缺失**: `ask_user` tool 的 `question_asked` 事件在 `context.projectId` 为 undefined 时发送空字符串；`questions` API 的 `question_answered` 事件硬编码空字符串。Question 表无 `project_id` 列，无法直接查询。
3. **Server 重启后 issue 卡死**: `pausedSessions`、`activeAgents`、`pendingResolvers` 全部为内存 Map，重启后丢失。卡在中间阶段的 issue 既无法 approve 也无法 start，完全卡死。
4. **EventBus SSE listener 残留**: SSE 连接异常断开时（客户端崩溃、网络切换），abort handler 可能不触发，listener 残留在 EventBus 中。

## Goals / Non-Goals

**Goals:**

- 让 agent 在 ask_user 阻塞时的状态可观测（agent status API、SSE 事件）
- 修复 question 事件的 projectId 一致性问题
- Server 重启后提供降级恢复路径，消除 issue 卡死
- SSE 异常断开时自动清理 listener

**Non-Goals:**

- ask_user 阻塞时允许用户发自由文本（需要双 loop 管理，留给 M3）
- Session 持久化（完整的 session 恢复属于 M4 B-090）
- 多 project SSE 严格隔离（当前广播模式够用，严格隔离留给未来）
- EventBus 从 `Map<string, Set>` 迁移到更复杂的方案

## Decisions

### D1: ask_user 状态通过 AgentRunnerService 暴露

**决策**: 在 `AgentRunnerService` 新增 `waitingQuestions` Map（`issueId → questionId`），ask_user 工具创建问题时通知 AgentRunnerService，回答或超时后清除。

**替代方案**:
- A) 独立的 QuestionStatusService — 过度设计，增加一层间接
- B) 从 pendingResolvers Map 推断 — pendingResolvers 是模块级变量，AgentRunnerService 无法直接访问

**理由**: AgentRunnerService 已经是 agent 状态的唯一真相源（管理 activeAgents、pausedSessions），waitingQuestions 是自然的扩展。ask_user tool 的 `AskUserContext` 已有 `issueId`，只需加一个回调或引用。

### D2: ask_user tool 通过回调通知 AgentRunnerService

**决策**: `AskUserContext` 新增可选的 `onWaitingChange` 回调（`(issueId: string, questionId: string | null) => void`），ask_user 创建问题时调用 `onWaitingChange(issueId, questionId)`，回答/超时后调用 `onWaitingChange(issueId, null)`。AgentRunnerService 在创建 ask_user tool 时注入回调。

**理由**: 回调是最简单的方式，不引入新的依赖关系。QuestionRepo 和 EventBus 保持不变。

### D3: question_answered 的 projectId 通过 join 查询

**决策**: `questions` API 在 emit `question_answered` 事件前，通过 join issues 表查询该 question 对应 issue 的 projectId。

**替代方案**:
- A) 给 questions 表加 project_id 列 — 需要 migration，且 project_id 是 issue 的冗余数据
- B) 让 questions API 接收 IssueRepo — 增加依赖，但最简单直接

**理由**: 选 B。questions API 已经在 `server/index.ts` 中创建时传入 QuestionRepo，只需同时传入 IssueRepo，在 reply handler 中 join 查询。不需要 schema 变更。

**注意**: `question_asked` 事件在 ask_user tool 中 emit，如果 `context.projectId` 为 undefined，会通过 `context.issueRepo` 查询 issue 获取 projectId。

### D4: Server 重启降级 — 检测 + 标记 + 引导

**决策**: Server 启动时扫描所有 `status = 'active'` 的 issues，这些是上次运行时未完成的。不做任何自动状态变更，但在 `AgentStatus` 返回值中新增 `recoverableIssues` 数组，列出这些 issue 的 number 和 stage。

**替代方案**:
- A) 自动将 active issues 改为 blocked — 侵入性强，可能不符合用户意图
- B) 自动将 active issues 改为 draft — 丢失 stage 信息
- C) 不处理，让用户自己发现 — 体验差

**理由**: 标记但不修改，让用户决定。WebUI 和 CLI 可以根据 `recoverableIssues` 展示引导："Issue #5 卡在 check 阶段，建议 reopen 后重新 start"。

**注意**: `waitingQuestions` 只反映当前 server 进程内存中的 ask_user 等待状态，不是全局所有 pending questions。Server 重启后 `waitingQuestions` 为空是正常的，不影响功能。

### D5: SSE 心跳机制

**决策**: 在 `events.ts` 的 streamSSE handler 中每 30 秒发送 SSE 心跳注释（`: heartbeat\n`），同时监听 `stream.writeSSE` 的失败来检测断开连接。如果 writeSSE 抛出异常（网络已断），立即清理所有 listener 并结束 stream。

**理由**: SSE 规范中 `:` 开头的行为注释会被客户端忽略，但可以保持连接活跃（防止代理超时）。writeSSE 失败是连接断开的最可靠检测信号。心跳间隔 30 秒是常见实践。

## Risks / Trade-offs

- **[D2 回调耦合]** ask_user tool 依赖 AgentRunnerService 的回调 → 如果不传回调，waitingQuestions 不会被更新。**缓解**: 回调是可选的，不传则退化为当前行为（状态不可观测但不影响功能）。
- **[D4 不自动修复]** 重启后 issue 需要用户手动恢复 → 用户可能不知道要操作。**缓解**: agent status API 返回 recoverableIssues，WebUI 和 CLI 可以主动引导。
- **[D5 心跳不能覆盖所有场景]** 如果客户端进程崩溃且 TCP keepalive 未触发，心跳 write 可能延迟几分钟才失败。**缓解**: 30 分钟的 MAX_CONNECTION_DURATION 是最终兜底。
- **[D3 join 查询]** questions API 新增 IssueRepo 依赖 → questions API 的职责略微膨胀。**缓解**: 只用一个 findById join 查询，不改 API 签名结构。
