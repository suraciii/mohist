## Why

当前 HTTP 层使用 Express，但项目即将需要 SSE 实时推送 agent 进度。Express 没有内置 SSE 支持，且类型安全性弱（req.body/req.query 均为 any）。迁移到 Hono 可以为后续 SSE 能力铺路，同时获得更好的类型推断、更小的包体积。

## What Changes

- 将 HTTP 框架从 Express 4 替换为 Hono + @hono/node-server
- 所有 API 路由文件（projects/issues/config/status/labels）从 Express Router 迁移到 Hono 路由
- HttpServer 类从 Express 适配到 Hono 的 serve() 模式
- 错误处理从手动 try/catch 保持不变（1:1 翻译）
- 测试从 supertest + Express app 适配到 supertest + Hono app
- 移除 express、@types/express 依赖，添加 hono、@hono/node-server

## Capabilities

### New Capabilities

（无新能力，纯框架替换）

### Modified Capabilities

- `http-api`: 底层框架从 Express 切换到 Hono，API 行为和响应格式不变

## Impact

- **代码**: `server/http-server.ts`、`server/index.ts`、全部 `api/*.ts` 文件、`api/error-handler.ts`
- **测试**: `tests/api-routes.test.ts` 需要 supertest 适配
- **依赖**: 移除 express + @types/express，添加 hono + @hono/node-server + @types/*（如有）
- **不涉及**: services、db、agent-runtime、cli 层均不受影响
