## Why

日志中的 `duration` 字段被 HTTP 请求中间件（`http-server.ts:36-42`）和内部事件（ACP session 完成、pipeline 完成等）共用同一个字段名。当内部事件写入 `duration: 242458` 时，日志分析工具误判为 HTTP 请求耗时，且因缺少 `method`/`path` 字段显示为 `undefined`，造成混淆和误报。

## What Changes

- **`util/log.ts` Logger.time()**: `time()` 方法的 stop 日志中的 `duration` 字段改名为 `elapsedMs`，与 HTTP 中间件的 `duration` 语义区分
- **`agent-runner-service.ts`**: pipeline 完成日志的 `duration` 字段改名为 `elapsedMs`
- **`workflow-controller.ts`**: Ralph loop 完成日志的 `duration` 字段改名为 `elapsedMs`
- **`acp-session.ts`**: ACP session 完成日志的 `duration` 字段改名为 `elapsedMs`
- **`http-server.ts`**: 保持 `duration` 字段不变（HTTP 请求专用）
- **`log-tail-api` spec**: 更新 JSONL schema 文档，明确 `duration` 仅用于 HTTP 请求，内部事件使用 `elapsedMs`

## Capabilities

### New Capabilities

（无）

### Modified Capabilities

- `log-tail-api`: JSONL schema 中增加字段语义约束——`duration` 为 HTTP 请求专用，内部事件计时使用 `elapsedMs`；`Logger.time()` 的 stop 日志输出字段从 `duration` 改为 `elapsedMs`

## Impact

- `packages/cli/src/util/log.ts`: `time()` 方法 stop 回调的字段名变更
- `packages/cli/src/server/http-server.ts`: 无变更，保持 `duration`
- `packages/cli/src/services/agent-runner-service.ts`: pipeline 完成日志字段名变更
- `packages/cli/src/workflow/workflow-controller.ts`: Ralph loop 完成日志字段名变更
- `packages/cli/src/agent-runtime/acp-session.ts`: ACP session 日志字段名变更
- `packages/cli/tests/log.test.ts`: 更新 `time()` 相关测试断言
- **BREAKING**: 依赖 `duration` 字段匹配内部事件日志的外部工具/脚本需要改为匹配 `elapsedMs`
