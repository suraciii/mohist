## Context

`coder_session` 表只存了 `task_description`（原始 prompt 截断 200 字符）作为标识信息，导致 UI 上所有 session 显示为 `<mohist-task>\n\n<role>\nYou are implementi...`，完全不可读。当前 `getSessionLabel` 的 fallback 逻辑仅靠 `stage` 和截断的 `taskDescription`，无法区分同一 issue 下的多个 build session。

Session 通过两条路径创建：
- `runAcpSession`（`acp-session.ts:395`）— 单次 session，用于 build tasks、auto-fix、skill、explore
- `createAcpConnection`（`acp-session.ts:818`）— 多轮复用 session，用于 Plan/Check 阶段和 conflict-resolution

当前 schema 版本是 20。`CoderSession` 接口和 `CoderSessionItem` 前端类型都没有 `title` 字段。

## Goals / Non-Goals

**Goals:**
- 每个 coder session 有一个人类可读的 `title`，由创建时调用者传入
- 前端展示优先使用 `title`，保持 fallback chain 兼容无 title 的旧数据
- SSE `coder_session_started` 事件实时携带 title
- API 端点返回 title 字段

**Non-Goals:**
- Session 分组/折叠（同 task 多次尝试）— 后续 feature
- 旧数据 backfill — 通过前端 fallback chain（从 executionId 解析 taskId）兼容
- `conflict-resolution.ts` 的 title — 该调用者使用 `createAcpConnection`，可选传入 `"Conflict resolution"` 作为 title，但不阻塞此变更

## Decisions

### D1: 新增 `ALTER TABLE` migration（version 21）

在 `migrations.ts` 中新增 `migrateToVersion21`，执行 `ALTER TABLE coder_session ADD COLUMN title TEXT`。列可为 NULL，对现有数据无影响。

注册方式与其他版本一致：在 `initializeDatabase` 函数中添加 `if (currentVersion < 21)` 分支。

### D2: title 贯穿 repo → 接口 → 调用者，逐层透传

数据流向：调用者 → `AcpSessionOptions.title` / `AcpConnectionOptions.title` → `coderSessionRepo.insert({ title })` → DB 存储 + SSE 事件发出。

不引入中间转换层，title 字符串由各调用者硬编码或从上下文变量拼接，是最简单的方案。

**Alternatives considered:**
- AI 生成 title — 增加延迟和成本，且每个调用者已有足够的上下文信息直接构造可读标题
- 从 taskDescription 后处理提取 — 不可靠（prompt 格式不固定，且包含 XML 标签）

### D3: 前端 getSessionLabel 改为 4 级 fallback chain

更新 `SessionHeader.tsx` 的 `getSessionLabel`：
1. `session.title`（非 null 非空则直接用）
2. 从 `executionId` 正则解析 `T-\d+` → `"T-004"`
3. 从 `executionId` 前缀推导 stage → `"Plan"` / `"Check"`
4. `taskDescription` 前 24 字符（现有行为）

**Alternatives considered:**
- 仅用 title，无 fallback — 旧数据全部显示空白，不可接受
- 仅前端解析，不改 DB — 无法为 auto-fix、skill、explore session 提供有意义的标签

### D4: SSE 事件 payload 扩展

在 `event-bus.ts` 和前端 `types.ts` 的 `coder_session_started` 事件类型中添加 `title?: string | null`。事件发出时携带 `title`（`acp-session.ts:405` 和 `acp-session.ts:831`）。前端 `useCoderSessions.ts` 收到事件时将 title 写入 live session state。

## Risks / Trade-offs

- [旧数据 title 为 NULL] → 前端 fallback chain 从 executionId 解析 taskId 或显示 stage name，不影响可读性
- [调用者遗漏传 title] → session 的 title 列为 NULL，fallback chain 兜底，不会报错
- [ALTER TABLE 在大表上耗时] → coder_session 表数据量小（<1000 rows），ALTER TABLE 瞬间完成
- [conflict-resolution.ts 未在初始 7 处调用者中列出] → 该调用者使用 `createAcpConnection`，title 列为 NULL，前端通过 stage 或 executionId fallback 兜底。可选补传 `title: "Conflict resolution"`

## Migration Plan

1. 新增 `migrateToVersion21` — `ALTER TABLE coder_session ADD COLUMN title TEXT`
2. 部署后新 session 自动写入 title，旧 session 保持 NULL
3. 无需回滚 — 可空列对新旧代码都兼容。如需回滚，前端 fallback chain 保证展示正常

## Open Questions

- `conflict-resolution.ts` 是否应传入 `title: "Conflict resolution"`？该调用者使用 `createAcpConnection`，当前提案未包含，但不阻塞主体实现。可在此 PR 中一并补上。
