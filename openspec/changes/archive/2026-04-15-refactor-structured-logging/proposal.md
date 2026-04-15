## Why

mohist 当前有 244 处 `console.log/error/warn` 散落在代码中，没有日志级别控制、没有结构化格式、没有按模块区分。当 Agent 执行失败（如 Issue #2 在 11 秒后 blocked），错误信息混在 stdout 中，无法按 issue/阶段/模块过滤，诊断极其困难。opencode 项目已有一套成熟的日志实现（`util/log.ts`），零依赖、支持级别过滤、结构化 key=value 格式、文件输出与自动轮转，可以直接借鉴引入。

## What Changes

- 引入 `src/util/log.ts`，参考 opencode 的 `Log` 命名空间实现：4 级别（DEBUG/INFO/WARN/ERROR）、`key=value` 结构化输出、命名 logger 缓存、tag 链式调用
- 默认输出到文件（`~/.mohist/logs/<timestamp>.log`），支持 `print` 模式输出到 stderr
- 支持文件自动轮转，保留最近 10 个日志文件
- 替换全部 244 处 `console.*` 调用为 `Log.create({ service: "xxx" })` 的结构化日志
- Server daemon 模式不再需要 stdout 重定向，直接由 Log 写文件
- `log.time()` 支持自动计时，用于 Agent 执行、数据库操作等关键路径的性能追踪

## Capabilities

### New Capabilities
- `structured-logging`: 统一日志模块，支持级别过滤、结构化输出、文件轮转、命名 logger 缓存、tag 链式上下文、自动计时

### Modified Capabilities
- `server-daemon`: 日志输出从 stdout 重定向改为 Log 模块直接写文件，移除 `fs.openSync` 重定向逻辑
- `workflow-log`: workflow 事件日志与结构化日志的关系界定（workflow_log 存业务事件，Log 存技术日志）

## Impact

- **新增文件**: `packages/cli/src/util/log.ts`（~150 行，零外部依赖）
- **修改文件**: 所有包含 `console.*` 的 30+ 个源文件
- **删除逻辑**: `packages/cli/src/cli/commands/server.ts` 中的 `LOG_FILE` + `fs.openSync` 重定向逻辑
- **配置**: 新增 `log.level` 配置项（支持 DEBUG/INFO/WARN/ERROR），可通过 CLI 参数覆盖
- **兼容性**: 无 breaking change，日志文件路径从 `~/.mohist/logs/server.log` 变为 `~/.mohist/logs/<timestamp>.log`
