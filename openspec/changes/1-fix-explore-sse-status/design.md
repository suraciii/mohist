## Context

Explore SSE 消息端点（`POST /api/explore/:id/messages`）当前在 `explore.ts:336` 调用 `runExploreAgent`，然后在 SSE stream 循环（`:344-377`）正常结束后于 `:389-395` 写入两条消息。如果循环抛出异常进入 catch（`:400`），消息不写入 DB，用户消息和 LLM 已消耗的回复全部丢失。

会话列表端点（`GET /api/explore`）调用 `ExploreService.listSessions()` → `ExploreSessionRepo.findByProject()`，不过滤 status。

## Goals / Non-Goals

**Goals:**
- 用户消息在 LLM 调用前写入 DB
- 助手回复在 stream 正常结束和异常中断时都写入 DB
- `findByProject()` 支持可选 status 过滤
- SSE 流式体验不受影响

**Non-Goals:**
- 不做增量保存（不在每个 chunk 后写 DB，避免性能开销）
- 不修改 explore_messages 表结构
- 不处理 LLM 调用本身失败的场景（外层 try-catch 已处理）
- 不改前端

## Decisions

### D1: 用户消息在 `runExploreAgent` 之前写入

在 `explore.ts:309` 之后（验证完成）、`runExploreAgent` 之前（`:336`）插入 `exploreService.addMessage(sessionId, 'user', userContent)`。

同时移除 `:389` 原来的用户消息写入（避免重复）。`historyMessages` 的构建（`:310-317`）不受影响，它用于传给 LLM 而非 DB。

**Alternatives considered:**
- 在 streamSSE 回调内、循环前写入 — 不必要，因为外层已有 session 存在性校验（`:281-288`），提前写入更简单。

### D2: 助手回复用 try-finally 保证写入

将 stream 循环内的 `assistantContent` 和 `toolCallRecords` 变量提升到 try 外层，用 try-finally 模式：finally 块中判断 `assistantContent` 非空则写入 DB。

具体结构：
```
let assistantContent = '';
let toolCallRecords: ToolCallRecord[] = [];
let createdIssueId: string | null = null;

try {
  for await (const part of result.fullStream) { ... }
  const finalText = await result.text;
  exploreService.addMessage(sessionId, 'assistant', finalText, ...);
  await stream.writeSSE({ data: done });
} catch (error) {
  if (assistantContent) {
    try { exploreService.addMessage(sessionId, 'assistant', assistantContent); }
    catch (e) { log.error('Failed to save partial message', ...); }
  }
  await stream.writeSSE({ data: done with error });
}
```

正常路径用 `finalText`（完整文本）写入；catch 路径用 `assistantContent`（已累积的部分）写入。catch 中的 DB 写入用独立 try-catch 保护，不影响错误响应。

**Alternatives considered:**
- 增量保存（每个 chunk 后写 DB）— 增加写放大，SQLite 同步写会阻塞事件循环，影响流式延迟。违背"不阻塞 SSE"约束。
- 用 `finally` 统一写入 — 正常路径需要用 `finalText`（比 `assistantContent` 更完整可靠），catch 路径需要不同处理逻辑（跳过空消息），混在一起不清晰。

### D3: `findByProject` 增加可选 status 参数

`ExploreSessionRepo.findByProject(projectId, status?)` — 当 `status` 有值时追加 `AND status = ?` 条件，否则查询不变。参数透传链：API `c.req.query('status')` → `ExploreService.listSessions(projectId, status?)` → repo。

不验证 status 是否为合法枚举值，无效值自然返回空列表（SQL 匹配不到）。

## Risks / Trade-offs

- [SSE 中断时 `assistantContent` 可能为空] → 不写空消息，仅记录日志。用户消息仍保留，重试时 LLM 历史上下文完整。
- [catch 中 DB 写入也失败] → 用独立 try-catch 保护，不影响错误 SSE 响应发送，记录错误日志。
- [正常路径现在不再写用户消息到 stream 循环内] → 用户消息在 LLM 调用前已写 DB，无风险。

## Migration Plan

纯后端改动，无数据库迁移，无 API 破坏性变更（`?status` 参数可选，默认行为不变）。直接部署即可。
