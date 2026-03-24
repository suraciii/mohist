## Context

crawlph 目前是一个 opencode skill (skills/crawlph/SKILL.md)，实现了 7 阶段的工作流。但它有以下限制：

- 必须在 opencode 平台内运行
- 无法持续运行（需要用户触发）
- 无法并发处理多个 Issues
- 状态管理依赖平台
- 不支持多项目管理

我们需要一个**独立的后台服务**，配合 CLI 界面使用。

```
┌─────────────────────────────────────────────────────────────────┐
│                    目标架构                                      │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│   用户                                                          │
│    │                                                            │
│    ▼                                                            │
│   crawlph CLI (thin client)                                     │
│   • 命令解析                                                    │
│   • 输出格式化                                                  │
│   • 调用 HTTP API                                               │
│    │                                                            │
│    │ HTTP (localhost:3456)                                      │
│    ▼                                                            │
│   crawlph Server (业务逻辑)                                     │
│   • 项目管理                                                    │
│   • Issue/PR 管理                                               │
│   • 工作流引擎                                                  │
│   • Agent Runner                                                │
│   • Poller                                                      │
│   • 状态存储                                                    │
│    │                                                            │
│    ├───────► GitHub API (状态存储)                              │
│    │                                                            │
│    └───────► opencode agents (任务执行)                         │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

**关键原则**: CLI 是 thin client，Server 是 fat server。所有业务逻辑在 Server 侧。

## Goals / Non-Goals

**Goals:**

1. 验证核心价值：AI 自动处理 Issue，用户只在关键点介入
2. 实现 Server + CLI 架构的基础设施
3. 完成单 Issue 的完整工作流（draft → done）
4. 支持多项目管理
5. 提供清晰的状态可视化（`crawlph status`）
6. 可以给自己用，也可以给其他开发者用

**Non-Goals:**

1. Ralph Loop（无限重试机制）- 初期不实现，失败就停下
2. 并发 Issues（同时处理多个）- 先做单 Issue 验证流程
3. 冲突检测 - Phase 2
4. 依赖管理 - Phase 2
5. Web UI / 远程访问 - Phase 2
6. 通知推送 - Phase 2
7. CLI 自动启动 Server - 用户必须显式启动

## Decisions

### D1: 技术栈

**选择**: TypeScript + Node.js 18+

**理由**:
- 类型安全
- AI SDK 生态丰富
- 与 opencode 生态兼容
- 快速迭代

**替代方案**:
- Go: 性能更好，但 AI 生态弱
- Python: 简单，但类型系统和并发模型不如 TS

### D2: 命令风格

**选择**: 分组式命令 (`crawlph <group> <action>`)

```
crawlph server start
crawlph project list
crawlph issue start 123
crawlph pr approve 201
```

**理由**:
- 清晰的命令分组
- 易于扩展新命令
- 与 `gh`、`kubectl` 等工具一致

**替代方案**:
- 简洁式 (`crawlph start 123`): 简单但不易扩展

### D3: 项目模型

**选择**: 支持多项目管理

```
┌─────────────────────────────────────────────────────────────────┐
│                    项目模型                                      │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│   crawlph "项目" = 一个 GitHub repo 的管理实例                   │
│                                                                 │
│   一个 Server 可以管理多个项目:                                  │
│   • blog  → suraciii/blog                                      │
│   • shop  → suraciii/shop                                      │
│                                                                 │
│   当前项目确定方式:                                              │
│   1. 目录检测: 当前目录下的 .crawlph/config.json                │
│   2. 全局切换: crawlph project use <name>                       │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

**数据存储**:

```
~/.crawlph/
├── config.json           # 全局配置 (token, server)
├── projects.json         # 项目索引
└── logs/                 # 全局日志

<project-path>/.crawlph/
└── config.json           # 项目配置 (repo, labels)
```

### D4: 状态存储

**选择**: GitHub Labels（主要）+ 本地缓存（辅助）

**理由**:
- Labels 作为状态来源，跨设备同步
- 本地缓存减少 API 调用

**标签设计**:

```
crawlph:stage/draft
crawlph:stage/designing
crawlph:stage/waiting-design-review
crawlph:stage/implementing
crawlph:stage/waiting-review
crawlph:stage/merging
crawlph:stage/done

crawlph:status/paused
crawlph:status/blocked
```

### D5: CLI ↔ Server 通信

**选择**: HTTP API (localhost:3456)

**理由**:
- 简单通用
- 易于调试（curl 即可）
- 未来可扩展（远程访问、Web UI）

**API 设计**:

```
# Server
GET  /api/health
GET  /api/status

# 项目
GET    /api/projects
POST   /api/projects
GET    /api/projects/:name
DELETE /api/projects/:name
POST   /api/projects/:name/use

# Issues
GET    /api/issues
GET    /api/issues/:number
POST   /api/issues/:number/start
POST   /api/issues/:number/pause
POST   /api/issues/:number/resume

# PRs
GET    /api/prs
GET    /api/prs/:number
POST   /api/prs/:number/approve
POST   /api/prs/:number/request-changes

# 配置
GET    /api/config
PUT    /api/config/:key
```

### D6: Server 生命周期

**选择**: 用户显式管理 Server

```
$ crawlph server start     # 启动
$ crawlph server stop      # 停止
$ crawlph server status    # 状态
```

**理由**:
- 用户明确知道 server 是否在运行
- 便于调试和问题排查
- 避免"自动启动"带来的意外行为

**Server 未运行时的 CLI 行为**:
- `crawlph server *` 命令正常工作
- 其他命令返回错误: "Server is not running. Start with: crawlph server start"

### D7: Agent 执行方式

**选择**: `child_process.spawn("opencode", ...)`

**理由**:
- 保持与现有 opencode 生态兼容
- 无需学习新 SDK
- 快速启动

**风险**: Server 挂了，所有 agent 都挂

**替代方案**:
- opencode SDK: 需要调研，可能更优雅
- 直接调用 OpenAI API: 完全独立，但失去 opencode 生态

**后续调研**: 评估 SDK 可行性 (https://opencode.ai/docs/zh-cn/sdk/)

### D8: 错误处理

**选择**: 失败即停止，标记为 blocked

**理由**:
- MVP 阶段保持简单
- Ralph Loop 增加复杂度
- 用户可以通过日志了解失败原因

**流程**:
1. Agent 执行失败
2. Server 捕获错误
3. 更新 Label 为 `crawlph:status/blocked`
4. 记录日志
5. 用户查看 `crawlph server logs` 或 `crawlph status`
6. 用户修复问题后，`crawlph issue resume <number>`

## Risks / Trade-offs

### R1: Agent 进程与 Server 耦合

**风险**: Server 崩溃会导致所有运行中的 agent 失败

**缓解**:
- MVP 阶段可接受（用户量小）
- Phase 2: 考虑独立的 agent 进程管理
- Phase 2: 引入进程监控和自动恢复

### R2: GitHub API 限流

**风险**: Poller 频繁调用 GitHub API 可能触发限流

**缓解**:
- 轮询间隔 60s
- 使用 ETag / If-Modified-Since 减少数据传输
- 缓存 Issue 数据，减少重复请求

### R3: 单 PR 模式的复杂性

**风险**: 设计和实现在同一个 PR，可能导致审查混乱

**缓解**:
- 使用 commits 分离（先 design commit，后 impl commits）
- PR description 清晰标注当前阶段
- 用户可以通过 commits 历史回顾

### R4: 多项目状态管理

**风险**: 多个项目的当前状态可能混淆

**缓解**:
- 目录检测优先（当前目录 -> 项目）
- `crawlph status` 始终显示当前项目名称
- `--all` 标志显示所有项目

### R5: 跨平台兼容性

**风险**: 进程管理在 Windows 上行为不同

**缓解**:
- 使用 HTTP API，跨平台兼容
- child_process 在 Node.js 中跨平台
- 测试覆盖 macOS, Linux, Windows

## Open Questions

1. **Agent 超时时间**: 默认 30 分钟是否合适？是否需要可配置？
2. **PR 命名规范**: `[crawlph] Design: <issue-title>` 还是其他格式？
3. **Server 停止策略**: 优雅停止 vs 立即停止？

## Migration Plan

1. **创建新目录**: `crawlph-cli/` 作为独立项目
2. **实现 Server**: HTTP API + Poller + Agent Runner + 项目管理
3. **实现 CLI**: 命令解析 + 与 Server 通信
4. **测试**: 单 Issue 完整流程
5. **文档**: README + 使用指南
6. **保留现有 skill**: 作为参考和回退方案

**Rollback**: 直接使用现有 skill，删除 `crawlph-cli/` 目录即可。
