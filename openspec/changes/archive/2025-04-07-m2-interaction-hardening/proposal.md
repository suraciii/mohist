## Why

M2 交互层有四个残留问题：(1) `ask_user` 阻塞时 agent 状态不透明——WebUI 和 CLI 都不知道 agent 在等用户回答；(2) questions API 的 `question_answered` 事件 `projectId` 硬编码空字符串，多 project 场景下 SSE 过滤失效；(3) Server 重启后 issue 卡死在中间阶段，无法恢复；(4) EventBus SSE 连接异常断开时 listener 残留。这些问题在单用户单 project 场景下部分被掩盖，但会阻碍 M2 "能交互" 的目标达成。

## What Changes

- **Agent 状态暴露**: `AgentRunnerService` 新增 `waitingForQuestion` 状态，`/api/agent/status` 返回当前等待的 questionId 和 question 内容，WebUI 和 mo attach 可据此展示 "等待回答" 状态
- **Question 事件修复**: `questions` API 的 `question_answered` 事件从 DB join issues 表获取 projectId，`ask_user` tool 的 `question_asked` 事件通过 issue 查询确保 projectId 正确
- **Server 重启降级**: 启动时检测卡死的 active issues（status=active 但无对应 agent session），自动将 status 改为 `active` 但 stage 保持不变，并在 agent status API 中标记 `recoverable: true`，前端引导用户 reopen+restart
- **EventBus 心跳清理**: SSE 连接增加心跳检测（30s 间隔），超时无客户端响应则主动清理 listener

## Capabilities

### New Capabilities

- `agent-waiting-state`: Agent 在 ask_user 阻塞时的状态暴露，包括 agent status API 扩展和 EventBus 事件

### Modified Capabilities

- `event-bus`: SSE 连接心跳机制，异常断开时自动清理 listener
- `ask-user-tool`: question_asked/question_answered 事件确保携带正确 projectId
- `http-api`: agent status 返回 waitingForQuestion 信息；questions API 修复 projectId

## Impact

- `services/agent-runner-service.ts` — 新增 waitingQuestions Map，status API 扩展
- `services/event-bus.ts` — 可选心跳清理机制（SSE 层实现）
- `api/events.ts` — 心跳检测逻辑
- `api/agent.ts` — status 返回值扩展
- `api/questions.ts` — question_answered 事件修复 projectId
- `tools/ask-user.ts` — question_asked 事件确保 projectId
- `server/index.ts` — 启动时检测并标记卡死 issues
- `db/question-repo.ts` — 新增 findByIdWithIssueId 方法（用于 join 查 projectId）
