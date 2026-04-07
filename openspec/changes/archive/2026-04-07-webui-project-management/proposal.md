## Why

WebUI 目前无法创建、删除或切换项目，用户必须依赖 CLI 完成项目管理工作。当 Server 上没有项目时，WebUI 卡在 "Loading..." 状态，无法自行恢复。这使得 WebUI 无法独立使用。

## What Changes

- WebUI 新增项目创建对话框（名称 + 工作目录路径）
- **路径选择器**: 搜索式目录浏览器替代手动输入，支持路径补全、模糊搜索、最近项目列表
- Header 项目下拉菜单增加 "New Project" 和 "Delete Project" 操作
- 无项目时显示空状态引导页面，替代 "Loading..." 卡住
- 前端 API client 补齐 `createProject`、`deleteProject`、`useProject` 方法
- React Query hooks 补齐对应的 mutation
- **后端新增文件系统 API**: 目录列表和模糊搜索，供路径选择器使用

## Capabilities

### New Capabilities

- `webui-project-management`: WebUI 项目创建、删除、空状态引导的完整功能
- `webui-directory-picker`: 搜索式目录浏览器组件 + 后端文件系统 API

### Modified Capabilities

_(无 requirement 变更，仅将现有 project-management API 暴露给前端使用)_

## Impact

- **前端代码**: `packages/cli/web/src/` — 新增 `DialogSelectDirectory`、`CreateProjectDialog` 组件，修改 `Header`、`api.ts`、`useQueries.ts`
- **后端代码**: `packages/cli/src/api/` — 新增 `fs.ts`，提供目录列表和搜索 API
- **API**: 新增 `GET /api/fs/list`、`GET /api/fs/search`；复用现有 `POST /api/projects`、`DELETE /api/projects/:name`、`POST /api/projects/:name/use`
- **新依赖**: 前端新增 `fuzzysort`（~2KB，用于模糊匹配目录名）
