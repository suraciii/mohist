## Why

Server 端 JSON 序列化没有统一入口：HTTP 层从未配置 `JavaScriptEncoder`，`System.Text.Json` 默认会把所有非 ASCII 字符（中文等）转义成 `\u4e2d\u6587`。结果是 API 响应、SignalR 推送、runner 事件、日志与 artifact 中的中文全部变成不可读的转义串，既损害可用性又增大传输/存储体积。同时业务代码里散布着十余处各自 `new JsonSerializerOptions(...)` 与 140+ 处直接 `JsonSerializer.Serialize/Deserialize` 调用，配置彼此漂移，使中文在不同路径上行为不可预期。现在修是因为这是面向用户的可见缺陷，且改动越晚散点越多。

## What Changes

- 将既有的 `Mohist.Server.Infrastructure.JSON` 确立为 server 唯一序列化门面，补齐统一 `JSON.Options`（含 `JSON.Indented` 变体）的 encoder 配置。
- `JSON.Options` 配置 `Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)`：非 ASCII 字符直出，同时保留对 HTML 危险字符的转义（安全）。
- HTTP API 出站响应（`Results.Ok/Json`、`ApiResults.*`、`ExceptionMiddleware`）通过全局 `Microsoft.AspNetCore.Http.Json.JsonOptions` 复用 `JSON.Options`，中文原样直出。
- SignalR 两个 hub（`/hubs/runner`、`/hubs/events`）通过 `JsonHubProtocolOptions` 复用同一份 options，推送 payload 中文原样直出。
- 消除散落的本地 `JsonSerializerOptions` 字段（约 17 处），收敛中间层共享 options（`WorkflowVariableJson.Options`、`CloudEvent.JsonOptions` 等约 5 处）为委托 `JSON.Options`。
- 约 40+ 处无 options 参数的默认 `JsonSerializer.Serialize/Deserialize` 调用统一改走 `JSON.*`。
- 明确自定义 converter（`FailureReason`、`ApprovalFeedbackStatus`、`AgentSessionStore` 等）归属：统一注册到 `JSON.Options` 或以文档化窄变体保留，确保枚举/会话状态序列化行为不回退。
- 持久化兼容：encoder 只影响序列化输出方向，SQLite 中已存的 JSON 可正常反序列化读回，无数据迁移、无 schema 变更。

## Capabilities

### New Capabilities

- `json-serialization`: Server 唯一 JSON 序列化门面与编码契约——统一 `JsonSerializerOptions` 来源、非 ASCII 字符直出 encoder、HTTP API 与 SignalR hub 复用同一配置、自定义 converter 归属，以及禁止在业务代码中新建本地 options 的约束。

### Modified Capabilities

- `http-api`: 新增要求——API 响应 SHALL 原样保留非 ASCII 字符（不得转义为 `\uXXXX`），覆盖 `Results.Ok/Json`、`ApiResults.*`、异常中间件等所有出站响应路径。

## Impact

- **代码**：`packages/server/src` 下 Workflow / Sessions / Issue / Project / Events / Infrastructure / Api 多子系统，按 Bucket A/B/C/D 分批改造；既有 `Infrastructure/JSON` 经增强成为统一门面。
- **API 出站编码**：行为变更（转义 → 直出），对 API 消费方更友好，字段命名/响应结构/`ApiResponse<T>` 信封不变。
- **SignalR hub**：runner 与 events hub 推送 payload 编码变更（转义 → 直出）。
- **持久化**：SQLite 已存 JSON 反序列化兼容，无迁移；新写入的 JSON 文件（config、artifact、session）体积减小、可读性提升。
- **依赖**：不引入第三方序列化库，继续使用 `System.Text.Json`。
- **范围**：仅 server/.NET；不改动 web/runner 包的序列化。
- **风险（medium）**：跨多子系统；自定义 converter 迁移需谨慎以防枚举/会话状态序列化回退。
