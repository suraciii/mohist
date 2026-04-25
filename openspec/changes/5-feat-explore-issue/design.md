## Context

当前 Explore 模式的架构：

- **路由**: `/explore` → `ExploreRedirect`（自动创建/跳转 session），`/explore/:id` → `ExplorePage`
- **状态模型**: `ExploreStatus { Active, Crystallized, Archived }`，create_issue tool 调用 `crystallize()` 同时绑定 issueId + 改状态为 Crystallized
- **Agent**: `runExploreAgent` 固定注入 `EXPLORE_SYSTEM_PROMPT`，工具集为 `{read_file, glob, grep, create_issue}`
- **前端**: 无 session 列表组件，ExploreRedirect 自动跳走；ExplorePage header 返回按钮导航到 `/`
- **关键文件**:
  - `src/types/index.ts` — ExploreStatus enum, ExploreSession interface
  - `src/db/explore-session-repo.ts` — create/crystallize/findById/findByProject
  - `src/services/explore-service.ts` — 薄服务层封装 repo
  - `src/agents/explore-agent.ts` — ExploreAgentContext, buildExploreToolRegistry, runExploreAgent
  - `src/tools/create-issue-tool.ts` — 调 crystallize() 绑定 issue
  - `src/api/explore.ts` — POST/GET/DELETE routes
  - `web/src/App.tsx` — ExploreRedirect + Routes
  - `web/src/components/ExplorePage.tsx` — header 返回 `/`
  - `web/src/lib/api.ts` — createExploreSession, listExploreSessions
  - `web/src/lib/types.ts` — ExploreSession interface

## Goals / Non-Goals

**Goals:**
- 替换 ExploreRedirect 为列表页，用户可管理 session
- Session 关联 Draft issue 后保持 active，可继续对话
- Agent 感知关联 issue 状态，提供 update_issue tool
- Draft issue 详情页提供 Explore 入口

**Non-Goals:**
- ACP explore 统一（explore-acp-service.ts 保持原样）
- ExploreStatus.Archived soft delete（用已有硬删）
- Stage.Explore 工作流阶段变更
- 消息数显示（列表页暂不展示，避免额外 query 复杂度）

## Decisions

### D1: crystallized 状态原地兼容，不改历史数据

保留 `ExploreStatus.Crystallized` 枚举值不删除（避免 enum break），但前端和后端逻辑统一将 `crystallized` 视为 `active`。`crystallize()` 方法保留不删（ACP 路由仍在用），但 create_issue tool 改用 `updateIssueId()`。

**Alternatives considered:**
- 数据库迁移把所有 crystallized 改为 active — 多一步 migration，风险大于收益
- 删除 Crystallized enum 值 — 会 break ACP crystallize 路由

### D2: issueNumber 通过 SQL LEFT JOIN 获取

`GET /api/explore` 列表查询改为 `LEFT JOIN issues ON explore_sessions.issue_id = issues.id`，返回 `issueNumber`。不新增 message_count 字段（需 sub-query 或额外 COUNT）。

**Alternatives considered:**
- 单独 API 查 issue 信息 — N+1 问题
- 前端批量查 issues — 多一次请求

### D3: update_issue tool 注册在 buildExploreToolRegistry 中条件注册

在 `buildExploreToolRegistry` 中检查 `session.issueId` 是否存在且 issue stage 为 Draft，条件性注册 update_issue tool。issue 信息通过 ExploreAgentContext 新增的 `issueId` 和 `issueStage` 字段传递。

**Alternatives considered:**
- 始终注册 tool，运行时报错 — 浪费 token，agent 会误调用
- 前端控制 tool 可用性 — 不安全，agent 端应有最终控制权

### D4: ExploreSessionList 为新组件，不复用 KanbanView

列表页是全新的 `ExploreSessionList` 组件，独立于 KanbanView。样式参考 KanbanView 的卡片布局，但独立实现。

**Alternatives considered:**
- 复用 KanbanView — 两者的数据模型和交互差异太大

### D5: 同一 issue 只关联一个 session 的唯一性由 API 层保证

`POST /api/explore` 传入 issueId 时，后端先查 `findByIssueId()` 是否已存在 session。ExploreSessionRepo 新增 `findByIssueId(issueId)` 方法。

**Alternatives considered:**
- 数据库 UNIQUE 约束 — issue_id 允许 NULL，SQLite 的 UNIQUE 对 NULL 值处理不一致
- 前端只检查 — 竞态条件下不安全

### D6: Agent prompt 动态拼接，不修改 EXPLORE_SYSTEM_PROMPT 常量

`runExploreAgent` 在构建 system prompt 时，基于 session 关联的 issue 状态追加额外段落。EXPLORE_SYSTEM_PROMPT 常量保持不变。

**Alternatives considered:**
- 修改常量加入条件文本 — 常量应保持纯净
- 在 tool description 中暗示 — 不够明确

## Risks / Trade-offs

- [历史 crystallized session 显示为 "Active" 可能困惑] → 列表页通过 issueId 有无显示 "Linked to Issue #N" 标记，替代状态标签传达含义
- [ACP crystallize 路由仍调 crystallize()，后续 ACP 统一时需处理] → 本 change 不动 ACP 路径，记录为已知遗留
- [update_issue tool 更新 labels 是全量替换还是增量] → 全量替换（与 create_issue 一致），保持简单

## Migration Plan

无需数据库 migration。变更纯代码层面：

1. 后端：ExploreSessionRepo.create 加 issueId 参数 → ExploreService.createSession 加 issueId → API route 接受 issueId
2. 后端：create_issue tool 改用 updateIssueId()
3. 后端：新增 update_issue tool + findByIssueId + findByProjectWithIssueNumber
4. 后端：ExploreAgentContext 扩展 + runExploreAgent 动态 prompt
5. 前端：ExploreSessionList 新组件 → App.tsx 替换路由
6. 前端：ExplorePage header 改返回 + 显示 issue 关联
7. 前端：IssueDetailPage 加 Explore 按钮
8. 前端：api.ts 加 issueId 参数 + 类型更新

回滚：revert commit 即可，无 schema 变更。

## Open Questions

- 消息数展示暂不含在列表页（需 COUNT sub-query），后续可加
