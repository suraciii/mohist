## ADDED Requirements

### Requirement: 日志文件格式为 JSONL

`util/log.ts` 写入文件的日志 SHALL 为 JSONL 格式，每行一条 JSON 记录，包含以下字段：
- `level`: 日志级别（`"DEBUG"` | `"INFO"` | `"WARN"` | `"ERROR"`）
- `time`: ISO 8601 时间戳字符串
- `diffMs`: 与上一条日志的时间差（毫秒），整数
- `service`: 日志器名称（来自 `Log.create({ service })` 中的 service tag）
- `message`: 日志消息文本
- 其他 extra 字段 SHALL 作为顶层键值对写入

日志文件 SHALL 使用按日期 rolling 的文件名 `mohist-YYYY-MM-DD.log`，存储于 `~/.mohist/logs/`。`dev:true` 模式 SHALL 使用固定文件名 `dev.log`。Server 启动时 SHALL 删除 `~/.mohist/logs/` 下超过 24 小时的旧 rolling 日志文件。

`--print-logs` 模式 SHALL 保持人类可读的纯文本格式输出到 stderr，不输出 JSONL。

#### Scenario: 正常模式下日志文件写入 JSONL
- **WHEN** `Log.init({ print: false })` 被调用
- **AND** logger 调用 `log.info("HTTP request", { method: "GET", path: "/api/health" })`
- **THEN** 文件中追加一行 JSON，可被 `JSON.parse()` 解析
- **AND** 包含 `"level":"INFO"`, `"time":"<ISO8601>"`, `"diffMs":<number>`, `"service":"<service>"`, `"message":"HTTP request"`, `"method":"GET"`, `"path":"/api/health"`
- **AND** 文件路径匹配 `~/.mohist/logs/mohist-YYYY-MM-DD.log`

#### Scenario: --print-logs 模式保持人类可读
- **WHEN** `Log.init({ print: true })` 被调用
- **AND** logger 调用 `log.info("HTTP request")`
- **THEN** stderr 输出纯文本格式，不包含 JSON

#### Scenario: dev 模式使用固定文件名
- **WHEN** `Log.init({ print: false, dev: true })` 被调用
- **THEN** 日志文件路径为 `~/.mohist/logs/dev.log`

#### Scenario: 清理超过 24 小时的旧日志
- **WHEN** server 启动并初始化日志
- **AND** `~/.mohist/logs/` 下存在 `mohist-2026-04-01.log`（修改时间超过 24 小时前）
- **THEN** 该文件被删除

### Requirement: GET /api/logs/tail 端点提供 cursor-based 日志读取

Server SHALL 提供 `GET /api/logs/tail` 端点，从当前日志文件读取日志行，返回结构化 JSON。

请求参数（query string）：
- `cursor` (可选): 上次返回的文件字节偏移，用于增量读取
- `limit` (可选): 最多返回行数，默认 500，上限 5000
- `maxBytes` (可选): 从文件末尾最多读取的字节数，默认 250KB，上限 1MB

响应 JSON（统一 ApiResponse 格式）：
```json
{
  "success": true,
  "data": {
    "file": "/home/user/.mohist/logs/mohist-2026-04-15.log",
    "cursor": 15234,
    "lines": ["<JSONL line>", ...],
    "truncated": true
  }
}
```

#### Scenario: 首次请求（无 cursor）
- **WHEN** 客户端请求 `GET /api/logs/tail`（无 cursor 参数）
- **THEN** 从文件末尾 maxBytes 字节开始读取
- **AND** 返回 `cursor` 为当前文件大小
- **AND** 如果文件大于 maxBytes，`truncated` 为 `true`

#### Scenario: 增量请求（带 cursor）
- **WHEN** 客户端请求 `GET /api/logs/tail?cursor=15234`
- **AND** 文件当前大小为 18000
- **THEN** 从字节偏移 15234 开始读取到文件末尾
- **AND** 返回 `cursor` 为 18000
- **AND** `truncated` 为 `false`（增量读取不截断）

#### Scenario: cursor 超出文件大小（日志轮转）
- **WHEN** 客户端请求 `GET /api/logs/tail?cursor=99999`
- **AND** 文件当前大小为 5000
- **THEN** 从文件末尾 maxBytes 字节开始读取（重置行为）
- **AND** `truncated` 为 `true`

#### Scenario: 日志文件不存在
- **WHEN** 日志文件路径不存在
- **THEN** 返回 `{ success: true, data: { file: "<path>", cursor: 0, lines: [], truncated: false } }`

#### Scenario: 跨天后自动回退到最新 rolling 日志文件
- **WHEN** `Log.file()` 返回 `/home/user/.mohist/logs/mohist-2026-04-16.log`
- **AND** 该文件尚不存在（新的一天还未写入日志）
- **AND** `~/.mohist/logs/` 下存在 `mohist-2026-04-15.log`
- **THEN** API 自动读取 `mohist-2026-04-15.log`
- **AND** 响应中的 `file` 字段为实际读取到的文件路径

### Requirement: 日志 API 返回统一响应格式

`GET /api/logs/tail` SHALL 使用项目统一的 `ApiResponse<T>` 格式返回，即 `{ success: true, data: { file, cursor, lines, truncated } }`。

#### Scenario: API 返回包装后的数据
- **WHEN** 客户端请求 `GET /api/logs/tail`
- **THEN** 响应 JSON 的顶层包含 `"success": true`
- **AND** 实际数据位于 `data` 字段内

### Requirement: 日志 API 不受 rate limit 限制

`GET /api/logs/tail` SHALL NOT 被全局 rate limiter 计入配额，以确保前端轮询不会因高频请求被限流。

#### Scenario: 日志 API 请求不被限流
- **WHEN** 客户端在 1 分钟内请求 30 次以上 `GET /api/logs/tail`
- **THEN** 所有请求均返回 200
- **AND** 不返回 429 Too Many Requests

### Requirement: 日志 API 返回原始 JSONL 行

`GET /api/logs/tail` 返回的 `lines` 数组 SHALL 包含原始 JSONL 文本行（未解析的字符串）。前端负责解析每行的 JSON 结构。

#### Scenario: lines 数组包含原始字符串
- **WHEN** API 返回日志
- **THEN** `lines` 数组的每个元素是 string 类型
- **AND** 每个元素可被 `JSON.parse()` 成功解析为包含 `level`、`time`、`message` 的对象
