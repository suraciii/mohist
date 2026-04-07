## Context

Explore-mode 功能已实现 9 个任务（T-001 到 T-009），全部标记 passes。代码审查发现 14 个问题，其中 4 个严重可见 bug 需要修复。本次修复范围涵盖后端工具安全、API 消息持久化时序、前端依赖缺失和错误处理。

## Goals / Non-Goals

**Goals:**
- 修复所有严重和高优先级 bug（共 7 个）
- 修复部分中优先级问题（单例注入、返回类型、死代码、消息校验）
- 不改变现有功能行为，仅修正实现缺陷

**Non-Goals:**
- 不添加新功能（如 session 管理列表 UI、textarea auto-resize）
- 不添加无障碍改进（aria-label 等）
- 不添加测试覆盖（留作后续）
- 不替换自定义 glob 实现
- 不处理潜在 ReDoS（LLM 自身提供 regex，风险极低）

## Decisions

### D1: 消息持久化时序 — 先运行 agent，后保存消息

**决策**: 将 `explore.ts` 中用户消息的保存从 agent 运行前移到 agent 成功后，与 assistant 消息一起保存。

**替代方案**: 保存前检查重复消息 — 引入额外的查询开销，且无法完全避免竞态。

**理由**: 最简方案，保证用户消息和 assistant 消息要么同时存在，要么同时不存在。代价是 agent 失败时用户看不到自己发送的消息（但此时有错误提示，体验可接受）。

### D2: `createdIssueId` 从 tool result 直接捕获

**决策**: 在 `explore.ts` 的 stream 处理中，当 `tool-result` 事件且 tool name 为 `create_issue` 时，从 result 字符串解析出 issue number。

**替代方案**: 从 DB 重新读取 session — 当前实现，间接且脆弱。

**理由**: `create_issue` tool 已返回 `"Issue #${issue.number} created..."` 格式的字符串，用 regex 提取 number 即可。消除对 DB 读取时序的依赖。

### D3: `@tailwindcss/typography` 通过 Tailwind v4 `@plugin` 引入

**决策**: 在 CSS 入口文件中添加 `@plugin "@tailwindcss/typography"` 并安装 npm 依赖。

**理由**: Tailwind v4 使用 `@plugin` 指令替代 v3 的 `plugins` 配置项。

### D4: `eventBus` 通过 context 注入

**决策**: `CreateIssueToolContext` 增加 `eventBus` 字段，从 `createExploreRoutes` 传入，不再直接导入单例。

**替代方案**: 保持单例导入 — 简单但不一致。

**理由**: 与 `add-comment.ts` 模式一致，提升可测试性。

### D5: 前端错误状态通过 hook 返回值暴露

**决策**: `useExploreStream` 增加 `streamError: string | null` 返回值，在 `done` 事件带 error 或 catch 块中设置。`ExplorePage` 根据此状态显示错误 banner。

**理由**: 遵循现有错误展示模式（`IssueDetailPage` 的红色错误框）。

## Risks / Trade-offs

- **[D1 消息延迟持久化]** → 如果 agent 成功但保存消息时 DB 写入失败，agent 已消耗 token 但消息未持久化。可接受，因为 SQLite 写入几乎不会失败。
- **[D2 正则解析]** → 如果 `create_issue` tool 的返回格式变化，regex 解析会失败。通过 fallback 到 DB 读取缓解。
