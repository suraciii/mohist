## Why

Settings 页面的 tab 边界按实现结构而非用户意图划分（Providers 和 Model Selection 被拆到不同 tab），缺少 taskTimeout/stageTimeout/maxGracePeriods/log level/mohist model 等 UI，且 per-field Save 产生视觉噪音。随着配置项持续增长（Workflow、Skills 可配置），现有 tab 模式无法扩展。Sidebar 导航是 developer tool settings 的行业标准（GitHub/Vercel/Linear），可容纳 15+ sections 并支持 deep linking。

## What Changes

- Settings 页面从 top tabs 重构为 sidebar 导航 + 内容区布局，sidebar 在 mobile 变为顶部 dropdown
- URL 路由从 `?tab=ai` 改为 `/settings/:section`，支持 deep linking
- 按 3 个用户意图分区：AI（Providers + Models）、Agent（Timeouts + Concurrency + Recovery）、System（Logs + About）
- Provider 列表统一：已连接（●）和未连接（○）在同一列表，已连接优先排序，消除空状态 dead zones
- 新增 Mohist Model 选择器（config.model）和 Coder Model 选择器（config.opencode.model），Stage Overrides 默认折叠
- 新增 Agent Runtime section：Session/Stage/Task 超时输入 + 解释性图表 + Concurrency + Recovery
- Agent Runtime section 使用 section 级 Save + dirty state 追踪，替代 per-field Save
- 新增 System section：Log Level 可编辑 + About 只读信息（version/git hash/server status/paths）
- 后端新增 API：mohist model 读写、system info（version/paths/server status）、log level 读写
- 移除现有 SettingsPage.tsx 的 Tab/TabPanel 组件和 GeneralSettingsSection.tsx 中的旧结构

## Capabilities

### New Capabilities

- `settings-sidebar-nav` — Settings 页 sidebar 导航布局，含 mobile 响应式 dropdown、路由 `/settings/:section`、活跃项高亮
- `settings-ai-section` — AI section 组件：统一 Provider 列表（已连接/未连接合并排序）+ Custom Providers + Mohist Model/Coder Model 双选择器 + Stage Overrides 折叠面板
- `settings-agent-section` — Agent Runtime section 组件：Session/Stage/Task 超时输入 + 解释性层级图表 + Concurrency/Recovery 设置 + section 级 Save/Reset
- `settings-system-section` — System section 组件：Log Level 下拉 + About 只读信息区（version/git hash/server status/DB path/config path/opencode bin path）
- `system-info-api` — 后端 API 端点返回系统运行时信息（版本、git hash、server 状态、各路径），供 Settings System section 展示

### Modified Capabilities

- `web-ui` — Settings 路由从 query param (`?tab=`) 改为 path param (`/settings/:section`)，App.tsx 路由配置更新
- `http-api` — 新增/扩展配置 API：mohist model 读写（`config.model`）、log level 读写（`config.log.level`）、system info 端点
- `provider-config` — 前端 Provider 列表交互变更：统一列表替代 Connected/Available 分组，排序逻辑变更

## Impact

**Frontend（主要）**:
- `packages/cli/web/src/components/SettingsPage.tsx` — 完全重写：sidebar + 路由逻辑
- `packages/cli/web/src/components/GeneralSettingsSection.tsx` — 重构为 Agent + System sections 或拆分
- 新增 `AiSettingsSection.tsx`、`AgentSettingsSection.tsx`、`SystemSettingsSection.tsx`、`SettingsSidebar.tsx`
- `packages/cli/web/src/App.tsx` — 路由更新
- `packages/cli/web/src/hooks/` — 可能新增 useSettings dirty state hook

**Backend（新增 API）**:
- `packages/cli/src/api/` — 新增 system info 端点、model config 端点、log level 端点
- `packages/cli/src/services/` — 可能需要 SystemInfoService 或扩展现有 ConfigService

**依赖**: 无新外部依赖，纯架构重构。
