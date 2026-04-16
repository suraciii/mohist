## Context

mohist 的 `util/log.ts` 将日志写入 `~/.mohist/logs/` 目录，格式为纯文本。当前文件名按进程启动时间戳命名（`YYYY-MM-DDTHHMMSS.log`），保留最多 10 个文件。需要改为仿照 openclaw 的按日期 rolling 文件名 `mohist-YYYY-MM-DD.log`，并删除 24 小时前的旧日志。

当前无法通过 Web UI 查看日志，诊断 agent 失败、HTTP 异常、工作流卡住等问题只能 `tail -f` 文件。

openclaw 的日志系统（`logging/log-tail.ts` + gateway `logs.tail` RPC + UI `logs.ts`）提供了成熟的参考实现：JSONL 格式、cursor-based 分页、级别筛选、自动跟随。

## Goals / Non-Goals

**Goals:**
- Web UI 提供 `/logs` 独立页面，可搜索、按级别筛选、自动跟随系统日志
- 日志格式改为 JSONL 以支持结构化查询和展示
- 后端提供 `GET /api/logs/tail` API，cursor-based 分页读取日志文件
- 保持 `--print-logs` 模式的人类可读输出

**Non-Goals:**
- 不做多日志文件切换（只读当前日志文件）
- 不做实时 SSE/WebSocket 推送日志（使用轮询）
- 不做日志归档管理和搜索
- 不改 `--print-logs` 模式的行为

## Decisions

### D1: 日志格式改为 JSONL（仅文件输出）

**选择**: 文件写入改为 JSONL，每行一条 JSON。

```json
{"level":"INFO","time":"2026-04-15T10:30:00","diffMs":150,"service":"server","message":"HTTP request","duration":12,"path":"/api/health","method":"GET"}
```

**理由**: 前端需要按 level 精确筛选（诊断场景核心需求：只看 ERROR/WARN）。纯文本正则解析脆弱。JSONL 是日志领域的标准格式，前端 `JSON.parse()` 即可。

**替代方案**: 保持纯文本 + 后端正则解析 — 解析脆弱，格式一改就坏，且无法可靠提取结构化字段。

**影响范围**: `util/log.ts` 的文件写入逻辑。所有读取 `~/.mohist/logs/` 的地方需要适配。`--print-logs` 模式走 stderr 纯文本，不受影响。

**实现细节**: `build()` 在文件模式下输出 JSON 对象，其中 `diffMs` 字段记录 `next.getTime() - last`，保留现有纯文本模式中的时间差信息。

### D2: API 使用 REST + 轮询，不做 SSE 实时推送

**选择**: `GET /api/logs/tail` REST 端点，前端 3-5 秒轮询。

**理由**: 日志不是高频实时数据（不像 agent text chunk），轮询足够。实现简单，复用现有 Hono 路由模式，不需要新的 SSE channel。

**替代方案**: 复用 `/api/events` SSE channel 加 `log_entry` 事件 — 需要修改 EventBus 类型系统，且日志量大时会干扰其他事件的延迟。

### D3: Cursor-based 分页（参照 openclaw）

**选择**: API 返回 `cursor`（文件字节偏移），客户端下次请求携带 cursor 获取增量。

```
首次: GET /api/logs/tail → { cursor: 15234, lines: [...], truncated: true }
增量: GET /api/logs/tail?cursor=15234 → { cursor: 18000, lines: [...], truncated: false }
```

**理由**: openclaw 已验证此方案可行且高效。cursor 是文件字节偏移，不依赖行号，不受文件截断影响。前端缓冲区 2000 条上限，超出丢弃最旧。

### D4: 前端使用 React + TanStack Query

**选择**: `useLogs` hook 封装轮询逻辑，TanStack Query 管理缓存和刷新。

**理由**: 与现有 Web UI 技术栈一致（React、TanStack Query、Tailwind）。不引入新依赖。

### D5: 日志文件名仿照 openclaw 按日期 rolling

**选择**: 日志文件名改为 `mohist-YYYY-MM-DD.log`，放在 `~/.mohist/logs/` 下。清理策略改为启动时删除 24 小时前的旧日志。`dev:true` 时仍使用 `dev.log`。

**理由**: 与 openclaw 对齐，同一自然日的所有日志写入同一文件，跨天自动 rolling。`readLogTail` 在遇到文件不存在时（如跨天后首次请求），可以通过文件名模式找到目录下最新的日志文件。

**实现细节**: 
- `LOG_PREFIX = "mohist"`, `LOG_SUFFIX = ".log"`
- `formatLocalDate()` 生成 `YYYY-MM-DD`
- `pruneOldRollingLogs()` 删除 mtime < 24h 的旧文件
- `readLogTail` 通过正则 `/^mohist-\d{4}-\d{2}-\d{2}\.log$/` 匹配并选择 mtime 最新的文件作为回退

### D6: 只读当前日志文件（含 rolling 回退）

**选择**: API 以 `Log.file()` 为首选路径读取；如果该路径不存在且符合 rolling 文件名模式，则自动回退到 `logs/` 目录下 mtime 最新的 `mohist-YYYY-MM-DD.log` 文件。

**理由**: 诊断场景通常只需要当前日志。rolling 回退确保跨天后首次请求不会返回空结果（如果新的一天还没有日志写入）。历史日志查看留给 CLI 或文件系统操作。

### D7: 日志 API 排除在 rate limit 之外

**选择**: `GET /api/logs/tail` 请求跳过 rate limiter 检查。

**理由**: 前端 3 秒轮询会产生 20 req/min 的请求量，而现有 rate limit 窗口为 60 秒/30 请求，日志轮询可能占满配额并影响正常操作。日志读取是轻量文件 IO，无安全风险。

**实现细节**: 在 `http-server.ts` 的 middleware 中，对 `/api/logs/*` 路径提前返回，不调用 `rateLimiter.check()`。

### D8: 使用 Page Visibility API 控制后台轮询

**选择**: `useLogs` hook 监听 `document.visibilitychange`，tab 隐藏时暂停轮询，回到前台时立即拉取一次增量。

**理由**: 避免后台 tab 无意义轮询，减少 CPU/电池消耗，并防止缓冲区在后台期间被大量日志撑满导致丢失上下文。

### D9: Header Logs 按钮使用文字+图标

**选择**: 在 Header 右侧 Explore 与 Settings 之间添加 Logs 按钮，样式与现有按钮一致。窄屏时隐藏文字只保留图标（通过 CSS 响应式或 tooltip 实现）。

**理由**: 与现有导航风格统一，避免四文字按钮在 1280px 以下屏幕换行。

## Risks / Trade-offs

- **[BREAKING] 日志格式变更** → 所有依赖日志文件格式的外部工具（如 logrotate 配置、监控脚本）会失败。缓解：mohist 还年轻，外部工具依赖少，影响可控。
- **[轮询开销]** 前端每 3 秒请求一次 → 服务端读文件开销小（cursor-based 只读增量），且 tab 后台时自动暂停，可接受。
- **[大日志文件]** 日志文件超过 maxBytes 时 API 截断返回 → 前端显示 "truncated" 提示，用户知道不是完整日志。
- **[JSONL 解析失败]** 如果日志文件被外部破坏（如非完整 JSON 行），容错 `parseLogLine` 会回退为原始文本显示，不会崩溃。
