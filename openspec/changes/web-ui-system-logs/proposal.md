## Why

mohist 输出日志到 `~/.mohist/logs/` 但只能在终端或文件中查看，无法通过 Web UI 诊断运行时问题（agent 失败、HTTP 请求异常、工作流卡住等）。需要一个结构化的日志查看能力，让用户在浏览器中即可搜索、筛选和追踪系统日志。

## What Changes

- **BREAKING**: `util/log.ts` 文件输出格式从纯文本改为 JSONL，每行一条 JSON 记录，包含 `level`、`time`、`diffMs`、`service`、`message`、`extra` 字段
- **BREAKING**: 日志文件名从进程时间戳格式 `~/.mohist/logs/YYYY-MM-DDTHHMMSS.log` 改为按日期 rolling 的 `~/.mohist/logs/mohist-YYYY-MM-DD.log`，清理策略从保留 10 个文件改为删除 24 小时前的旧日志（仿照 openclaw）
- `--print-logs` 模式保持人类可读纯文本输出到 stderr，不受影响
- 新增 `GET /api/logs/tail` API 端点，支持 cursor-based 分页读取日志文件，返回结构化 JSON
- 新增 Web UI `/logs` 页面，提供级别筛选（DEBUG/INFO/WARN/ERROR）、文本搜索、自动跟随、导出功能
- Header 导航新增 Logs 入口

## Capabilities

### New Capabilities
- `log-tail-api`: 后端日志文件读取 API，cursor-based 分页，JSONL 解析，参照 openclaw 的 `logging/log-tail.ts` 实现
- `web-ui-logs-page`: 前端日志查看页面，级别筛选、文本搜索、自动跟随、结构化展示、导出

### Modified Capabilities
- (无现有 spec 需要修改)

## Impact

- `packages/cli/src/util/log.ts` — 输出格式变更（仅文件写入）
- `packages/cli/src/api/logs.ts` — 新增文件
- `packages/cli/src/server/index.ts` — 注册新路由
- `packages/cli/web/src/` — 新增 LogsPage 组件、useLogs hook、路由和导航
- 参考: `opensrc/openclaw/src/logging/log-tail.ts`、`opensrc/openclaw/ui/src/ui/views/logs.ts`、`opensrc/openclaw/ui/src/ui/controllers/logs.ts`
