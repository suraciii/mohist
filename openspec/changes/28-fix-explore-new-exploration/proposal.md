## Why

Explore 会话列表全部标题为 "New Exploration"，用户无法区分不同会话——列表功能实质失效。标题在创建时硬编码为固定值，缺少从用户消息中自动提取摘要的机制，也没有手动编辑标题的入口。

## What Changes

- Session 创建时从用户第一条消息截取前 50 字符作为初始标题（前端在创建 session 时传入）
- 添加 `PATCH /api/explore/:id` 端点支持更新 session 标题
- ExploreSessionRepo 新增 `updateTitle()` 方法
- ExploreSessionList 卡片支持双击编辑标题
- Session crystallize 时用关联 issue 标题更新 session 标题

## Capabilities

### New Capabilities

- `explore-session-title` — session 标题的自动命名、手动编辑和 crystallize 时更新

### Modified Capabilities

None.

## Impact

- **后端**: `explore-session-repo.ts`（新增 updateTitle）、`explore.ts` API（新增 PATCH 端点、crystallize 时更新标题）、`explore-service.ts`（新增 updateTitle 方法）
- **前端**: session 创建流程（传入用户首条消息作为标题）、session 列表卡片（双击编辑）、crystallize 回调（刷新标题）
- **数据库**: 无 schema 变更（`title` 字段已存在）
- **API**: 新增 `PATCH /api/explore/:id`（body: `{title: string}`）
