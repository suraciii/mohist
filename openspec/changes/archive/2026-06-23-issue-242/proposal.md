## Why

Mohist 的 Session 页面当前是**只读**的——用户只能被动观看 agent 输出，无法在运行中介入。而 opencode 的 `runLoop` 天然支持运行时消息注入（每次迭代从 DB 读最新消息，step > 1 时新消息被包裹为 `<system-reminder>`），但 Mohist 没有暴露任何让用户利用这一能力的路径：Web 没有 chat input，API 没有发送消息的端点，`PromptKind.followup` 虽已定义却从未被使用。解锁这条通路让用户能在 agent 当前 tool call 完成后立即插入指令（例如"加个登出"），无需 cancel、无需等整个 turn 结束，显著提升单次 session 的迭代效率。

## What Changes

- 新增 `POST /api/projects/{projectRef}/issues/{number}/sessions/{name}/followup` 端点，body `{ text: string }`，返回 `{ status: "sent" }`
- Server 验证 session 为 active 后，通过 `RunnerConnectionTracker` 查找 SignalR connectionId，向 runner 推送 `ReceiveFollowup` 消息
- Runner 收到 `ReceiveFollowup` 后，**fire-and-forget** 调用正在运行的 ACP session 的 `connection.prompt()`，仅把消息写入 opencode session DB，不阻塞、不需队列
- opencode 正在运行的 `runLoop` 在下一个迭代边界（当前 tool call 完成后）捡起消息，包裹为 `<system-reminder>` 注入 LLM 上下文
- 产生的 transcript 流仍走现有 `sessionUpdate` → grain → Web SSE 通路，无需新增事件类型
- 新 turn 标记为 `followup` PromptKind（类型已存在，首次启用）
- Web Session 页面底部在 session 运行时显示 chat input（textarea + send），session 终态（completed/failed）自动禁用
- Session 非 active 返回 409；runner 离线返回 503

## Capabilities

### New Capabilities
- `session-followup`: 用户在 session 运行期间向 agent 发送自由文本消息的端到端契约——API 调用语义、runner 侧 fire-and-forget 投递、active session 状态守卫、离线/终态错误处理、多条消息并发写入的预期行为、followup turn 的 PromptKind 标记

### Modified Capabilities
- `agent-session-ui`: 撤销"page stays read-only"场景，运行中的 session 页面 SHALL 在底部显示 chat input，session 终态时禁用输入
- `http-api`: 新增 `POST /api/projects/{projectRef}/issues/{number}/sessions/{name}/followup` 端点，定义请求/响应、409（session 非 active）、503（runner 离线）语义

## Impact

- **Server / API**: `packages/server/src/Mohist.Server/Api/IssueRoutes.Sessions.cs`（新端点）；`RunnerHub.cs` / `RunnerConnectionTracker.cs`（向 runner 推送 `ReceiveFollowup`）
- **Runner**: `packages/runner/src/server/runner-signalr.ts`（新增 `ReceiveFollowup` handler）；`acp-agent.ts` 或 transcript 记录路径（把新 turn 标记为 `followup` PromptKind）
- **Web**: `SessionPage.tsx` 底部新增 chat input 组件；新增 followup mutation hook；terminal 状态下禁用输入
- **依赖**: 依赖 opencode `runLoop` 既有的 DB 轮询 + `<system-reminder>` 包裹行为，无需 runner 侧消息队列或并发控制
