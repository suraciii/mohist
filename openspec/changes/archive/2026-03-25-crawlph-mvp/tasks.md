## 1. 项目初始化

- [x] 1.1 创建 `crawlph-cli/` 目录结构
- [x] 1.2 初始化 package.json（name, version, dependencies）
- [x] 1.3 配置 TypeScript（tsconfig.json）
- [x] 1.4 配置构建脚本（package.json scripts）
- [x] 1.5 创建基础类型定义（Issue, Stage, Project, Config 等）

## 2. SQLite 存储层

- [x] 2.1 添加 better-sqlite3 依赖
- [x] 2.2 实现 Database 封装类（run/get/all/transaction）
- [x] 2.3 实现 Schema 迁移（projects, issues, tasks, config 表）
- [x] 2.4 实现 ProjectRepo（CRUD）
- [x] 2.5 实现 IssueRepo（CRUD + 按 stage/status 查询）
- [x] 2.6 实现 TaskRepo（CRUD）
- [x] 2.7 实现 ConfigRepo（KV 存储）

## 3. Server 基础设施

- [x] 3.1 实现 HTTP server（使用 Express）
- [x] 3.2 实现健康检查接口 `GET /api/health`
- [x] 3.3 实现任务队列管理器（入队、出队、并发控制）
- [x] 3.4 实现 Server 启动入口（bin/crawlph-server）
- [x] 3.5 实现 Server 停止逻辑（优雅关闭）
- [x] 3.6 修改状态恢复逻辑（从 SQLite 恢复，非 GitHub）

## 4. 业务服务层

- [x] 4.1 实现 ProjectService（使用 ProjectRepo）
- [x] 4.2 实现 IssueService（使用 IssueRepo）
- [x] 4.3 实现 WorkflowService（状态转换逻辑）
- [x] 4.4 实现 ConfigService（使用 ConfigRepo）

## 5. HTTP API（重构）

- [x] 5.1 实现 `GET /api/status` - 获取当前项目状态
- [x] 5.2 实现 `GET /api/status?all=true` - 获取所有项目状态
- [x] 5.3 重构 `GET /api/issues` - 使用 IssueRepo
- [x] 5.4 重构 `POST /api/issues` - 创建 Issue（新增）
- [x] 5.5 重构 `GET /api/issues/:number` - Issue 详情
- [x] 5.6 重构 `POST /api/issues/:number/start` - 启动处理
- [x] 5.7 重构 `POST /api/issues/:number/approve` - 审批（新增）
- [x] 5.8 重构 `POST /api/issues/:number/pause` - 暂停
- [x] 5.9 重构 `POST /api/issues/:number/resume` - 恢复
- [x] 5.10 移除 PR 相关 API（MVP 不需要）
- [x] 5.11 重构 `GET /api/config` - 使用 ConfigRepo
- [x] 5.12 重构 `PUT /api/config/:key` - 使用 ConfigRepo

## 6. Agent Runner（保留）

- [x] 6.1 实现 agent spawn 逻辑（child_process.spawn）
- [x] 6.2 实现 agent 输出捕获（stdout, stderr）
- [x] 6.3 实现 agent 超时处理（30 分钟默认）
- [x] 6.4 实现 agent 进程管理（启动、监控、终止）
- [x] 6.5 实现 designer agent 的 prompt 模板
- [x] 6.6 实现 implementer agent 的 prompt 模板
- [x] 6.7 实现并发控制（最多 8 个并发）

## 7. CLI 基础设施（保留）

- [x] 7.1 实现 CLI 入口（bin/crawlph）
- [x] 7.2 实现命令路由（使用 Commander.js，分组式）
- [x] 7.3 实现 Server 状态检测
- [x] 7.4 实现美化的输出（使用 chalk）
- [x] 7.5 实现错误信息格式化
- [x] 7.6 实现 --help 和 --version

## 8. CLI Server 命令（保留）

- [x] 8.1 实现 `crawlph server start` - 启动 server
- [x] 8.2 实现 `crawlph server stop` - 停止 server
- [x] 8.3 实现 `crawlph server status` - 查看 server 状态
- [x] 8.4 实现 `crawlph server logs` - 查看日志

## 9. CLI Project 命令（修改）

- [x] 9.1 实现 `crawlph project create <name>` - 移除 --repo 参数
- [x] 9.2 实现 `crawlph project list`
- [x] 9.3 实现 `crawlph project use <name>`
- [x] 9.4 实现 `crawlph project remove <name>`
- [x] 9.5 实现 `crawlph project show <name>`
- [x] 9.6 实现 `crawlph init` - 在当前目录初始化项目

## 10. CLI Issue 命令（修改）

- [x] 10.1 实现 `crawlph issue create <title>` - 创建 Issue（新增）
- [x] 10.2 实现 `crawlph issue list [--status <stage>]`
- [x] 10.3 实现 `crawlph issue show <number>`
- [x] 10.4 实现 `crawlph issue start <number>`
- [x] 10.5 实现 `crawlph issue approve <number>` - 审批（新增）
- [x] 10.6 实现 `crawlph issue pause <number>`
- [x] 10.7 实现 `crawlph issue resume <number>`

## 11. CLI PR 命令（移除）

- [x] 11.1 移除 PR 相关命令（MVP 不需要）

## 12. CLI 快捷命令（保留）

- [x] 12.1 实现 `crawlph status` - 当前项目状态
- [x] 12.2 实现 `crawlph status --all` - 所有项目状态
- [x] 12.3 实现 `crawlph config <key> <value>`
- [x] 12.4 实现 `crawlph config --list`

## 13. Issue 工作流（简化）

- [x] 13.1 实现状态机定义（Stage 枚举和转换规则）
- [x] 13.2 移除 Label 解析逻辑（直接使用 stage 属性）
- [x] 13.3 实现 designing 阶段处理
- [x] 13.4 实现 waiting-design-review 阶段处理
- [x] 13.5 实现 implementing 阶段处理
- [x] 13.6 实现 waiting-review 阶段处理
- [x] 13.7 移除 merging 阶段（MVP 不需要）
- [x] 13.8 实现 done 阶段处理

## 14. 删除 GitHub 相关代码

- [x] 14.1 删除 `src/github/client.ts`
- [x] 14.2 删除 `src/github/rate-limit.ts`
- [x] 14.3 删除 `src/poller/status-poller.ts`
- [x] 14.4 删除 `tests/__mocks__/octokit.ts`
- [x] 14.5 更新 package.json 移除 @octokit/rest

## 15. 测试（重写）

- [x] 15.1 配置测试框架（Vitest）
- [x] 15.2 编写 Database 单元测试（:memory:）
- [x] 15.3 编写 Repository 层测试
- [x] 15.4 编写 Service 层测试
- [x] 15.5 编写 API 集成测试（使用 :memory: 数据库）
- [x] 15.6 更新 Agent Runner 测试（保留 mock spawn）
- [x] 15.7 编写 CLI 命令测试
- [x] 15.8 端到端测试（单 Issue 完整流程）

## 16. 文档和发布

- [x] 16.1 更新 README.md（移除 GitHub 相关内容）
- [x] 16.2 更新 CONTRIBUTING.md
- [x] 16.3 配置 package.json 发布信息
- [x] 16.4 移除 GitHub Labels 文档（MVP 不需要）
- [x] 16.5 测试跨平台兼容性（macOS, Linux, Windows）

## 17. Phase 2 准备（未来）

- [x] 17.1 定义 IssueProvider 接口
- [x] 17.2 设计 GitHub 插件架构
- [x] 17.3 设计 GitLab 插件架构
