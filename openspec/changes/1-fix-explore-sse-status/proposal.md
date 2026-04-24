## Why

Explore 会话的 SSE 流中断时（网络抖动、刷新页面、导航离开），用户消息和助手回复全部丢失——`explore.ts:389-395` 的 `addMessage()` 在 stream 循环结束后才执行，进入 catch 则不写 DB，但 LLM token 已消耗。同时 `explore-session-repo.ts:73` 的 `findByProject()` 不过滤 status，活跃/已结晶/已归档会话混在一起返回给前端。

## What Changes

- **用户消息先写 DB**: `POST /:id/messages` 在调用 LLM 前就将用户消息写入 DB，SSE 中断也不丢失
- **助手回复可靠保存**: stream 循环结束后（包括 catch 路径）都尝试将已累积的 `assistantContent` 写入 DB；stream 中断时保存已接收的部分回复
- **会话列表 status 过滤**: `ExploreSessionRepo.findByProject()` 增加 `status?` 参数，API 层支持 `?status=active` 查询参数

## Capabilities

### New Capabilities

- `explore-message-persistence`: Explore 消息持久化保障——用户消息 LLM 调用前写入，助手回复在 stream 结束后（含中断路径）写入

### Modified Capabilities

- `http-api`: `GET /api/explore` 新增 `status` 查询参数支持

## Impact

- `packages/cli/src/api/explore.ts` — 消息写入时序调整，catch 块增加消息保存
- `packages/cli/src/db/explore-session-repo.ts` — `findByProject()` 增加 status 过滤
- `packages/cli/src/services/explore-service.ts` — `listSessions()` 透传 status 参数
- `packages/cli/src/api/explore.ts` GET `/` 路由 — 解析 `?status=` 查询参数
