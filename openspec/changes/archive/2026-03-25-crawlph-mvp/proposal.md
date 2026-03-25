## Why

crawlph 目前是一个 opencode skill，依赖平台运行，无法独立部署。我们需要一个**独立的后台服务**，能够：

1. 持续运行，无需用户手动触发
2. 并发处理多个任务
3. 提供更好的用户体验（CLI 界面、实时状态）
4. 支持多项目管理

**核心理念**: crawlph 是"让 AI 自动完成软件开发任务的编排引擎"，GitHub 只是其中一种任务来源。

## What Changes

- **新增** crawlph-server：后台持续运行的服务
  - HTTP API server (localhost:3456)
  - Agent Runner：spawn opencode agents 执行任务
  - 状态管理：SQLite 本地存储（MVP）
  - 项目管理：多项目支持

- **新增** crawlph-cli：纯界面层（thin client）
  - 与 server 通过 HTTP 通信
  - 美化的终端输出
  - **不自动启动 server** - 用户必须先启动 server

- **新增** 命令集（分组式）

  **Server 管理**（无需 server 运行）:
  - `crawlph server start` - 启动 server
  - `crawlph server stop` - 停止 server
  - `crawlph server status` - 查看 server 状态
  - `crawlph server logs` - 查看日志

  **项目管理**（需要 server 运行）:
  - `crawlph init` - 在当前目录初始化项目
  - `crawlph project list` - 列出项目
  - `crawlph project use <name>` - 切换当前项目
  - `crawlph project remove <name>` - 删除项目

  **Issue 管理**:
  - `crawlph issue create <title>` - 创建 Issue
  - `crawlph issue list` - 列出 Issues
  - `crawlph issue show <number>` - 查看 Issue 详情
  - `crawlph issue start <number>` - 启动处理
  - `crawlph issue approve <number>` - 审批通过（设计/实现）
  - `crawlph issue pause <number>` - 暂停
  - `crawlph issue resume <number>` - 恢复

  **快捷命令**:
  - `crawlph status` - 当前项目状态
  - `crawlph status --all` - 所有项目状态

- **保留** 现有 skill (skills/crawlph/SKILL.md) - 作为参考和后续迁移基础

## Capabilities

### New Capabilities

- `server-daemon`: 后台持续运行的服务进程，监听 HTTP 请求，管理 agent 任务
- `project-management`: 项目创建、切换、删除，支持多项目
- `cli-interface`: 命令行用户界面（thin client），与 server 通信
- `http-api`: Server 提供的 RESTful API 接口
- `issue-workflow`: Issue 处理的完整工作流（draft → designing → implementing → done）
- `agent-runner`: 执行 opencode agents 的能力
- `sqlite-storage`: SQLite 本地存储（better-sqlite3）

### Future Capabilities (Phase 2)

- `github-provider`: GitHub Issue/PR 同步插件
- `gitlab-provider`: GitLab Issue/MR 同步插件

## Impact

**新建目录**:
- `crawlph-cli/` - CLI + Server 项目根目录

**数据存储**:
```
~/.crawlph/
├── crawlph.db          # SQLite 数据库（所有数据）
└── logs/               # 全局日志
```

**SQLite Schema**:
- `projects` - 项目索引
- `issues` - 所有 issues（含 stage, status）
- `tasks` - 运行中的任务
- `config` - KV 配置存储

**不受影响**:
- 现有 `skills/crawlph/SKILL.md` - 保留作为参考

**外部依赖**:
- Node.js 18+
- TypeScript
- better-sqlite3 (SQLite)
- Commander.js (CLI framework)
- Express (HTTP server)
- opencode CLI (agent 执行)

**删除的依赖**:
- ~~@octokit/rest~~ - MVP 不使用 GitHub API
