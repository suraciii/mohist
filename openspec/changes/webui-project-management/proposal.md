## Why

WebUI 目前无法创建、删除或切换项目，用户必须依赖 CLI 完成项目管理工作。当 Server 上没有项目时，WebUI 卡在 "Loading..." 状态，无法自行恢复。这使得 WebUI 无法独立使用。

## What Changes

- WebUI 新增项目创建对话框（名称 + 工作目录路径）
- Header 项目下拉菜单增加 "New Project" 和 "Delete Project" 操作
- 无项目时显示空状态引导页面，替代 "Loading..." 卡住
- 前端 API client 补齐 `createProject`、`deleteProject`、`useProject` 方法
- React Query hooks 补齐对应的 mutation

## Capabilities

### New Capabilities

- `webui-project-management`: WebUI 项目创建、删除、空状态引导的完整功能

### Modified Capabilities

_(无 requirement 变更，仅将现有 project-management API 暴露给前端使用)_

## Impact

- **前端代码**: `packages/cli/web/src/` — 新增 `CreateProjectDialog` 组件，修改 `Header`、`api.ts`、`useQueries.ts`
- **后端代码**: 无改动，所有需要的 API 端点已存在
- **API**: 无新增端点，复用现有 `POST /api/projects`、`DELETE /api/projects/:name`、`POST /api/projects/:name/use`
