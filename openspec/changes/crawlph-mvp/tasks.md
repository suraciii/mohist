## 1. 项目初始化

- [x] 1.1 创建 `crawlph-cli/` 目录结构
- [x] 1.2 初始化 package.json（name, version, dependencies）
- [x] 1.3 配置 TypeScript（tsconfig.json）
- [x] 1.4 配置构建脚本（package.json scripts）
- [x] 1.5 创建基础类型定义（Issue, Stage, Project, Config 等）

## 2. Server 基础设施

- [x] 2.1 实现 HTTP server（使用 Express 或 Fastify）
- [x] 2.2 实现健康检查接口 `GET /api/health`
- [x] 2.3 实现任务队列管理器（入队、出队、并发控制）
- [x] 2.4 实现 Server 启动入口（bin/crawlph-server）
- [x] 2.5 实现 Server 停止逻辑（优雅关闭）
- [x] 2.6 实现状态恢复逻辑（从 projects.json 和 GitHub Labels 恢复）

## 3. 项目管理

- [x] 3.1 实现项目数据模型（Project interface）
- [x] 3.2 实现项目存储（~/.crawlph/projects.json）
- [x] 3.3 实现 `POST /api/projects` - 创建项目
- [x] 3.4 实现 `GET /api/projects` - 列出项目
- [x] 3.5 实现 `GET /api/projects/:name` - 项目详情
- [x] 3.6 实现 `DELETE /api/projects/:name` - 删除项目
- [x] 3.7 实现 `POST /api/projects/:name/use` - 切换当前项目
- [x] 3.8 实现目录检测逻辑（当前目录 → 项目）

## 4. HTTP API

- [x] 4.1 实现 `GET /api/status` - 获取当前项目状态
- [x] 4.2 实现 `GET /api/status?all=true` - 获取所有项目状态
- [x] 4.3 实现 `GET /api/issues` - 列出 Issues（当前项目）
- [x] 4.4 实现 `GET /api/issues/:number` - Issue 详情
- [x] 4.5 实现 `POST /api/issues/:number/start` - 启动处理
- [x] 4.6 实现 `POST /api/issues/:number/pause` - 暂停
- [x] 4.7 实现 `POST /api/issues/:number/resume` - 恢复
- [x] 4.8 实现 `GET /api/prs` - 列出 PRs
- [x] 4.9 实现 `GET /api/prs/:number` - PR 详情
- [x] 4.10 实现 `POST /api/prs/:number/approve` - 批准
- [x] 4.11 实现 `GET /api/config` - 获取配置
- [x] 4.12 实现 `PUT /api/config/:key` - 设置配置
- [x] 4.13 实现统一的错误处理和响应格式

## 5. GitHub 集成

- [x] 5.1 实现 GitHub Client（使用 @octokit/rest）
- [x] 5.2 实现获取 Issues 列表（按 Label 过滤）
- [x] 5.3 实现获取单个 Issue 详情
- [x] 5.4 实现 Label 操作（添加、删除、检查）
- [x] 5.5 实现获取 PR 状态（approved, merged 等）
- [x] 5.6 实现创建 PR（通过 gh CLI）
- [x] 5.7 实现 GitHub API 限流处理

## 6. Agent Runner

- [x] 6.1 实现 agent spawn 逻辑（child_process.spawn）
- [x] 6.2 实现 agent 输出捕获（stdout, stderr）
- [x] 6.3 实现 agent 超时处理（30 分钟默认）
- [x] 6.4 实现 agent 进程管理（启动、监控、终止）
- [x] 6.5 实现 designer agent 的 prompt 模板
- [x] 6.6 实现 implementer agent 的 prompt 模板
- [x] 6.7 实现并发控制（最多 8 个并发）

## 7. Status Poller

- [x] 7.1 实现定时轮询逻辑（每 60 秒）
- [x] 7.2 实现检测 PR 状态变化（approved, merged）
- [x] 7.3 实现检测新 Issues（带 draft 标签）
- [x] 7.4 实现条件请求（ETag / If-Modified-Since）

## 8. CLI 基础设施

- [x] 8.1 实现 CLI 入口（bin/crawlph）
- [x] 8.2 实现命令路由（使用 Commander.js，分组式）
- [x] 8.3 实现 Server 状态检测
- [x] 8.4 实现美化的输出（使用 chalk）
- [x] 8.5 实现错误信息格式化
- [x] 8.6 实现 --help 和 --version

## 9. CLI Server 命令

- [x] 9.1 实现 `crawlph server start` - 启动 server（daemon 模式）
- [x] 9.2 实现 `crawlph server stop` - 停止 server
- [x] 9.3 实现 `crawlph server status` - 查看 server 状态
- [x] 9.4 实现 `crawlph server logs` - 查看日志

## 10. CLI Project 命令

- [x] 10.1 实现 `crawlph project create <name> --repo <owner/repo>`
- [x] 10.2 实现 `crawlph project list`
- [x] 10.3 实现 `crawlph project use <name>`
- [x] 10.4 实现 `crawlph project remove <name>`
- [x] 10.5 实现 `crawlph project show <name>`

## 11. CLI Issue 命令

- [x] 11.1 实现 `crawlph issue list [--status <stage>]`
- [x] 11.2 实现 `crawlph issue show <number>`
- [x] 11.3 实现 `crawlph issue start <number>`
- [x] 11.4 实现 `crawlph issue pause <number>`
- [x] 11.5 实现 `crawlph issue resume <number>`

## 12. CLI PR 命令

- [x] 12.1 实现 `crawlph pr list`
- [x] 12.2 实现 `crawlph pr show <number>`
- [x] 12.3 实现 `crawlph pr review <number>` - 打开浏览器
- [x] 12.4 实现 `crawlph pr approve <number> [--message <msg>]`
- [x] 12.5 实现 `crawlph pr request-changes <number> <message>`

## 13. CLI 快捷命令

- [x] 13.1 实现 `crawlph status` - 当前项目状态
- [x] 13.2 实现 `crawlph status --all` - 所有项目状态
- [x] 13.3 实现 `crawlph config <key> <value>`
- [x] 13.4 实现 `crawlph config --list`

## 14. Issue 工作流

- [x] 14.1 实现状态机定义（Stage 枚举和转换规则）
- [x] 14.2 实现 designing 阶段处理
- [x] 14.3 实现 waiting-design-review 阶段处理
- [x] 14.4 实现 implementing 阶段处理
- [x] 14.5 实现 waiting-review 阶段处理
- [x] 14.6 实现 merging 阶段处理
- [x] 14.7 实现 done 阶段处理

## 15. 测试

- [x] 15.1 配置测试框架（Vitest）
- [x] 15.2 编写 GitHub Client 单元测试
- [x] 15.3 编写 Agent Runner 单元测试
- [x] 15.4 编写 API 集成测试
- [x] 15.5 编写 CLI 命令测试
- [x] 15.6 执行端到端测试（单 Issue 完整流程）

## 16. 文档和发布

- [x] 16.1 编写 README.md（安装、用法、示例）
- [x] 16.2 编写 CONTRIBUTING.md（开发指南）
- [x] 16.3 配置 package.json 发布信息
- [x] 16.4 创建 GitHub Labels 文档
- [x] 16.5 测试跨平台兼容性（macOS, Linux, Windows）
