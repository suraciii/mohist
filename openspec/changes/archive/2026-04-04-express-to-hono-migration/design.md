## Context

mohist HTTP 层使用 Express 4，包含 1 个 HttpServer 类 + 5 个 Router 文件 + 1 个 error handler，约 930 行代码。所有路由遵循统一的 handler 模式（try/catch + ApiResponse 包装）。当前无 SSE 端点，无 middleware 链（除 json 解析和 logger）。

业务层（services/db/agent-runtime）完全不依赖 Express，仅通过 StateManager 暴露数据访问。

## Goals / Non-Goals

**Goals:**
- 将 HTTP 框架从 Express 替换为 Hono，保持所有 API 端点行为不变
- 保持 1:1 翻译策略，最小化行为变更风险
- 移除 express 依赖，添加 hono + @hono/node-server
- 适配测试到 Hono 的测试模式

**Non-Goals:**
- 不添加 SSE 能力（后续独立变更）
- 不重构错误处理模式（保持手动 try/catch）
- 不修改 API 响应格式或接口契约
- 不引入 Hono 特有高级特性（RPC、validator middleware 等）

## Decisions

### D1: 使用 Hono + @hono/node-server

Hono 提供类型安全的路由、内置 body parsing、更小的包体积（~14KB vs ~500KB）。@hono/node-server 提供 Node.js 运行时适配，保持现有部署方式不变。

**替代方案**: Fastify — 性能好但生态更重，SSE 需要插件。不选因为 Hono 对 SSE 的内置支持更好（为后续铺路）。

### D2: 路由结构保持 factory function 模式

保持现有的 `createXxxRoutes(deps): Hono` 模式，与 Express 的 `createXxxRoutes(deps): Router` 一致。`HttpServer` 类通过 `app.route(path, subApp)` 挂载子路由。

### D3: 测试继续使用 supertest

Hono 兼容 Web标准 Request/Response，supertest 可直接与 `app.request` 或 `@hono/node-server` 的 fetch handler 配合使用。保持 supertest 避免同时改框架和测试工具。

### D4: server 启动使用 serve() 替代 app.listen()

`@hono/node-server` 的 `serve({ fetch: app.fetch, port })` 替代 `app.listen(port)`。HttpServer 类的 start/stop 接口保持不变。

## Risks / Trade-offs

- **[Async handler 变化]** Hono handler 必须返回 Response → 所有 handler 需要改为 async。Mitigation: 机械性变更，模式固定。
- **[supertest 适配]** supertest 需要 Hono 的 fetch adapter。Mitigation: @hono/node-server 提供兼容方案，或使用 `app.request()` 辅助函数。
- **[参数解析]** Express 的 `req.params` 和 Hono 的 `c.req.param()` 行为一致（均为 string），无风险。
- **[body parsing]** Hono 内置 body parsing，无需显式 middleware。`c.req.json()` 是 async 方法，需要 await。Mitigation: 机械性变更。
