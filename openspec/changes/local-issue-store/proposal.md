## Why

当前 Crawlph 依赖 GitHub Issues 作为数据源，但 MVP 阶段我们希望快速迭代，不受 GitHub API 限制、网络问题和外部依赖的影响。

本地 Issue 管理让我们能够：
1. 完全离线工作
2. 无 API 限流
3. 快速验证 workflow 逻辑
4. 后续再考虑 GitHub 同步

## What Changes

- 扩展现有 SQLite 数据库，添加 Labels 和 Comments 支持
- 新增 CLI 命令通过 Server API 操作本地数据
- Issue 使用 `project#number` 格式显示（如 `my-app#1`）
- Labels 用 JSON 数组存储，支持 `+label` / `-label` 语法
- Projects 管理为手动切换，不自动检测

## Capabilities

### New Capabilities

- `local-issue-store`: 本地 Issue 和 Project 的扩展（Labels、Comments）、新增 CLI CRUD 命令

### Modified Capabilities

- `cli-interface`: 新增 issue create/update/close/comment 和 label list 命令
- `project-management`: 移除 GitHub repo 关联，改为纯本地项目管理

## Impact

- 保留 CLI-Server 架构，Server 继续管理 workflow 和 agent 执行
- 扩展现有 `~/.crawlph/crawlph.db` SQLite 数据库（添加 labels 列和 comments 表）
- 保留现有 stage/status 枚举模型，labels 作为补充
- CLI 命令仍通过 HTTP API 与 Server 通信
- 后续可添加 GitHub 同步层作为独立 capability
