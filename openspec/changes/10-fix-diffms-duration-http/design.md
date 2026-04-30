## Context

日志系统的 `duration` 字段被两类调用者共用：

1. **HTTP 中间件**（`http-server.ts:36-42`）— 写入 `{ method, path, status, duration }` 表示 HTTP 请求耗时（通常 <1s）
2. **内部事件**（`Logger.time()`、ACP session、pipeline 等）— 写入 `{ duration }` 表示长时间运行的内部操作耗时（可达数分钟至数小时）

因为共用字段名，日志分析时无法区分两类记录。内部事件缺少 `method`/`path`，日志工具显示 `undefined`，且大数值 `duration` 污染 HTTP 请求耗时统计。

## Goals / Non-Goals

**Goals:**
- 将内部事件日志的耗时字段从 `duration` 改为 `elapsedMs`，使 HTTP 和内部事件在 JSONL 中可区分
- 保持 HTTP 中间件的 `duration` 字段不变（向后兼容 HTTP 日志消费方）

**Non-Goals:**
- 不引入日志类型标签（`http_request | internal_event`）— 字段名区分已足够
- 不修改 `writeSessionLog()` / `writeLog()` / EventBus 中持久化的 `duration` 字段（它们不是 logger 输出，是结构化存储）
- 不修改前端代码 — 前端消费的是 workflow_log API 返回的结构化数据，不是原始 JSONL

## Decisions

### D1: 内部事件统一使用 `elapsedMs` 字段名

选择 `elapsedMs` 而非 `totalMs` 或其他名称。理由：
- 语义清晰："elapsed milliseconds"，表示经过的时间
- 与现有 `diffMs`（相邻日志行间隔）命名风格一致
- 不影响 HTTP 中间件的 `duration` 字段

**Alternatives considered:**
- 添加日志类型标签（`logType: "http_request" | "internal_event"`）— 增加每行开销，过度设计
- 使用 `totalMs` — 语义不如 `elapsedMs` 精确

### D2: 仅修改 `log.*()` 调用中的 `duration` 字段

`writeSessionLog()` 和 `writeLog()` 写入 `workflow_log` 表的 `duration` 字段不修改。这些是持久化的结构化数据，由专门的 API 返回给前端，不存在与 HTTP 日志混淆的问题。

**Alternatives considered:**
- 全局统一改名 — 不必要，增加改动范围和回归风险

## Risks / Trade-offs

- **[BREAKING] 外部脚本依赖 `duration` 字段匹配内部事件日志** → 改为 `elapsedMs` 是预期行为，且内部日志无外部消费者承诺
- **遗漏某些 `log.*()` 调用点** → `duration` 变量名在代码中广泛存在（包括非日志用途），需要精确识别所有 `log.info/error/warn` 中传入 `duration` 的位置

## Migration Plan

1. 修改 `log.ts` 的 `time()` stop 回调：`duration` → `elapsedMs`
2. 修改 `agent-runner-service.ts:781`：`duration` → `elapsedMs`
3. 修改 `workflow-controller.ts:940`：`duration` → `elapsedMs`（log.info 中的局部变量）
4. 修改 `acp-session.ts` 所有 `log.info/error` 中的 `duration` → `elapsedMs`（7 处）
5. 更新 `tests/log.test.ts` 中 `time()` 测试的断言
6. 运行 `npm test` 验证

回滚：直接 revert commit 即可，无数据库迁移。

## Open Questions

无。
