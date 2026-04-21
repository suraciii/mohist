## Why

Issue 详情页无法看到 agent session 的完整对话内容。根因分析发现三个致命 bug + 一个架构缺失 + 三个前端问题：

**致命 Bug:**
1. `api/issues.ts` 5 处构造 `acpOptions` 时未传 `eventBus` → Pipeline 运行期间零 SSE 事件
2. `issueId` 类型不匹配（后端 UUID vs 前端 issue number）且双重用途（DB 外键 + SSE 标识）
3. Plan 阶段 `acpOptions` 缺少 `executionId`

**架构缺失:**
4. `createAcpConnection` 不暴露 sessionUpdate 拦截机制 → 无法为 Plan/Review 阶段产生带 round 元数据的事件；如果直接修好 bug 1+3，Plan 阶段会产生无 round 标签的 `coder_*` 事件，与需要的 `plan_session_update` 语义重复
5. Build 阶段 `RalphExecutor` 构造时未传 `eventBus` → 零 SSE 事件、零 workflow_log、零 coder_session（但 RalphExecutor 内部已有 eventBus 支持，只需传入）

**前端问题:**
6. `agentStatus.issueId === String(issueNumber)` 比较永远 false → agent 运行检测失败
7. `coder_tool_call` 丢弃已有数据（`rawInput`/`rawOutput`/`title` 在 ACP notification 中已有但未转发）

## What Changes

**后端修复（打通数据管道）:**
- `AcpSessionOptions` / `AcpConnectionOptions` 增加 `issueNumber`、`onSessionUpdate` 字段
- 5 处 `acpOptions` 构造点补上 `eventBus` + `issueNumber`
- `acp-session.ts`: SSE emit 用 `String(issueNumber)`，DB 操作继续用 UUID；`onSessionUpdate` 存在时跳过内部 `coder_*` emit
- `workflow-controller.ts`: Plan/Review 阶段通过 `onSessionUpdate` 桥接 `plan_session_update` / `plan_round_start` 事件，设置阶段级 `executionId`
- `coder_tool_call` 事件 payload 增加 `rawInput`、`rawOutput`、`title`（两个 handler 都要改）
- Build 阶段：`runPipelineBuildStage` 传入 `this.eventBus` + repos + `issueNumber`

**前端修复（消费数据）:**
- `agentStatus` 比较改用 `issueNumber` 字段
- 新增 `plan_round_start` / `plan_session_update` 事件类型和 SSE 注册
- 新建 `useSessionTimeline` hook 从 `workflow_log` API 加载历史 + SSE 实时追加
- 新建 `SessionTimeline` 组件按 round 分组展示完整对话链

## Capabilities

### New Capabilities
- `pipeline-session-events`: Plan/Review 阶段 ACP 事件桥接到 EventBus，携带 round 元数据

### Modified Capabilities
- `coder-session-tracking`: 修复 eventBus 传递、issueId 双轨制、executionId 缺失；`coder_tool_call` payload 丰富化；Build 阶段可观测性
- `agent-session-ui`: 前端 agentStatus 修复；AgentSessionPanel → SessionTimeline
- `session-timeline-ui`: 按 round 分组展示完整对话

## Impact

- **packages/cli/src/api/issues.ts**: 5 处 `acpOptions` 补 `eventBus` + `issueNumber`
- **packages/cli/src/agent-runtime/acp-session.ts**: option 接口扩展 + issueId 双轨 + onSessionUpdate 分流 + coder_tool_call 丰富化（两个 handler）
- **packages/cli/src/workflow/workflow-controller.ts**: Plan/Review 阶段 onSessionUpdate 桥接 + executionId 按阶段设置 + Build 阶段传 eventBus
- **packages/cli/src/openspec/ralph-executor.ts**: context 接口扩展 + 传入 eventBus/repos/issueNumber
- **packages/cli/src/services/event-bus.ts**: 新增 plan_round_start、plan_session_update 事件类型
- **packages/cli/web/src/**: agentStatus 修复 + SessionTimeline 组件 + useSessionTimeline hook + types 扩展
