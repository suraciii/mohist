## MODIFIED Requirements

### Requirement: 日志文件格式为 JSONL

`util/log.ts` 写入文件的日志 SHALL 为 JSONL 格式，每行一条 JSON 记录，包含以下字段：
- `level`: 日志级别（`"DEBUG"` | `"INFO"` | `"WARN"` | `"ERROR"`）
- `time`: ISO 8601 时间戳字符串
- `diffMs`: 与上一条日志的时间差（毫秒），整数
- `service`: 日志器名称（来自 `Log.create({ service })` 中的 service tag）
- `message`: 日志消息文本
- 其他 extra 字段 SHALL 作为顶层键值对写入

日志中涉及耗时的字段 SHALL 遵循以下语义约定：
- `duration`: 仅用于 HTTP 请求耗时（由 `http-server.ts` 中间件写入，始终伴随 `method`、`path`、`status` 字段）
- `elapsedMs`: 用于内部事件计时（如 `Logger.time()` 的 stop 日志、ACP session 完成耗时、pipeline 完成耗时等）

`Logger.time()` 方法的 stop 回调 SHALL 输出 `elapsedMs` 字段（而非 `duration`），表示从 `time()` 调用到 `stop()` 调用之间经过的毫秒数。

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

#### Scenario: HTTP 请求日志使用 duration 字段
- **WHEN** HTTP 中间件记录请求 `log.info("HTTP request", { method: "GET", path: "/api/health", status: 200, duration: 12 })`
- **THEN** 日志行包含 `"duration":12` 且伴随 `"method":"GET"`, `"path":"/api/health"`, `"status":200`

#### Scenario: Logger.time() stop 日志使用 elapsedMs 字段
- **WHEN** 代码调用 `const timer = log.time("build stage")` 后调用 `timer.stop()`
- **THEN** stop 日志包含 `"elapsedMs":<number>` 字段
- **AND** stop 日志不包含 `duration` 字段

#### Scenario: 内部事件耗时使用 elapsedMs 字段
- **WHEN** agent-runner-service 记录 pipeline 完成日志 `log.info("Pipeline run completed", { elapsedMs: 242458 })`
- **THEN** 日志行包含 `"elapsedMs":242458`
- **AND** 日志行不包含 `method`、`path`、`status` 等 HTTP 专属字段
- **AND** 日志行不包含 `duration` 字段

#### Scenario: ACP session 完成日志使用 elapsedMs 字段
- **WHEN** ACP session 关闭时记录日志 `log.info("ACP connection closed", { sessionId, elapsedMs: 440473 })`
- **THEN** 日志行包含 `"elapsedMs":440473`
- **AND** 日志行不包含 `duration` 字段
