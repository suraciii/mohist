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
│                    MVP 目标架构                                  │
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
│   • Issue 管理                                                  │
│   • 工作流引擎                                                  │
│   • Agent Runner                                                │
│   • 状态存储 (SQLite)                                           │
│    │                                                            │
│    └───────► opencode agents (任务执行)                         │
│                                                                 │
│   ══════════════════════════════════════════════════════════    │
│   无外部依赖：无 GitHub API，无远程服务                          │
│   ══════════════════════════════════════════════════════════    │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

**关键原则**: 
- CLI 是 thin client，Server 是 fat server
- 所有业务逻辑在 Server 侧
- MVP 使用 SQLite 本地存储，GitHub 集成作为 Phase 2 插件

## Goals / Non-Goals

**Goals:**

1. 验证核心价值：AI 自动处理任务，用户只在关键点介入
2. 实现 Server + CLI 架构的基础设施
3. 完成单 Issue 的完整工作流（draft → done）
4. 支持多项目管理
5. 提供清晰的状态可视化（`crawlph status`）
6. 测试友好：SQLite :memory: + mock spawn

**Non-Goals:**

1. GitHub 集成 - Phase 2 插件
2. Ralph Loop（无限重试机制）- 初期不实现，失败就停下
3. 并发 Issues（同时处理多个）- 先做单 Issue 验证流程
4. 冲突检测 - Phase 2
5. 依赖管理 - Phase 2
6. Web UI / 远程访问 - Phase 2
7. 通知推送 - Phase 2
8. CLI 自动启动 Server - 用户必须显式启动

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

### D2: 存储方案

**选择**: SQLite (better-sqlite3)

**理由**:
- 单文件，无需外部服务
- 事务支持，并发安全（WAL 模式）
- 索引优化查询
- :memory: 测试极其简单
- 同步 API，代码简洁

**Schema 设计**:

```sql
-- 项目
CREATE TABLE projects (
  id          TEXT PRIMARY KEY,
  name        TEXT UNIQUE NOT NULL,
  path        TEXT NOT NULL,
  created_at  TEXT NOT NULL,
  updated_at  TEXT NOT NULL
);

-- Issues
CREATE TABLE issues (
  id          TEXT PRIMARY KEY,
  number      INTEGER NOT NULL,
  project_id  TEXT NOT NULL REFERENCES projects(id),
  title       TEXT NOT NULL,
  body        TEXT,
  stage       TEXT NOT NULL DEFAULT 'draft',
  status      TEXT NOT NULL DEFAULT 'active',
  created_at  TEXT NOT NULL,
  updated_at  TEXT NOT NULL,
  UNIQUE(project_id, number)
);

CREATE INDEX idx_issues_project_stage ON issues(project_id, stage);
CREATE INDEX idx_issues_project_status ON issues(project_id, status);

-- 任务
CREATE TABLE tasks (
  id           TEXT PRIMARY KEY,
  issue_id     TEXT NOT NULL REFERENCES issues(id),
  project_id   TEXT NOT NULL REFERENCES projects(id),
  stage        TEXT NOT NULL,
  status       TEXT NOT NULL,
  agent_pid    INTEGER,
  error        TEXT,
  started_at   TEXT,
  completed_at TEXT
);

CREATE INDEX idx_tasks_project_status ON tasks(project_id, status);

-- 配置
CREATE TABLE config (
  key   TEXT PRIMARY KEY,
  value TEXT NOT NULL
);
```

**替代方案**:
- JSON 文件: 简单但并发不安全，无事务
- PostgreSQL: 过于重量级，需要外部服务

### D3: 分层架构

**选择**: Repository 模式

```
┌─────────────────────────────────────────────────────────────────┐
│                         分层架构                                 │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│   CLI (Commander.js)                                            │
│        │                                                        │
│        ▼                                                        │
│   API (Express)                                                 │
│        │                                                        │
│        ▼                                                        │
│   Services (业务逻辑)                                            │
│   • IssueService                                                │
│   • ProjectService                                              │
│   • WorkflowService                                             │
│        │                                                        │
│        ▼                                                        │
│   Repositories (数据访问)                                        │
│   • IssueRepo                                                   │
│   • ProjectRepo                                                 │
│   • TaskRepo                                                    │
│   • ConfigRepo                                                  │
│        │                                                        │
│        ▼                                                        │
│   Database (SQLite)                                             │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

**理由**:
- 依赖注入，便于测试
- 未来可替换存储后端
- 关注点分离

### D4: 命令风格

**选择**: 分组式命令 (`crawlph <group> <action>`)

```
crawlph server start
crawlph project list
crawlph issue create "添加暗黑模式"
crawlph issue start 1
crawlph issue approve 1
```

**理由**:
- 清晰的命令分组
- 易于扩展新命令
- 与 `gh`、`kubectl` 等工具一致

### D5: 工作流阶段

**选择**: 7 阶段工作流（无 PR 阶段）

```
draft → designing → waiting-design-review → implementing → waiting-review → done
```

**阶段说明**:

| 阶段 | 触发 | 执行者 | 动作 |
|------|------|--------|------|
| draft | `issue create` | 用户 | 创建 Issue |
| designing | `issue start` | Agent | AI 生成设计 |
| waiting-design-review | Agent 完成 | 用户 | 审批设计 |
| implementing | `issue approve` | Agent | AI 实现代码 |
| waiting-review | Agent 完成 | 用户 | 审批实现 |
| done | `issue approve` | 用户 | 标记完成 |

**与原版区别**:
- 移除 `merging` 阶段（无 GitHub PR）
- 审批通过 CLI 命令完成

### D6: CLI ↔ Server 通信

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
POST   /api/issues                    # 新增：创建 Issue
GET    /api/issues/:number
POST   /api/issues/:number/start
POST   /api/issues/:number/approve    # 新增：审批
POST   /api/issues/:number/pause
POST   /api/issues/:number/resume

# 配置
GET    /api/config
PUT    /api/config/:key
```

### D7: Server 生命周期

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

### D8: Agent 执行方式

**选择**: `child_process.spawn("opencode", ...)`

**理由**:
- 保持与现有 opencode 生态兼容
- 无需学习新 SDK
- 快速启动

### D9: 错误处理

**选择**: 失败即停止，标记为 blocked

**流程**:
1. Agent 执行失败
2. Server 捕获错误
3. 更新 Issue status 为 `blocked`
4. 记录错误信息到 Task
5. 用户查看 `crawlph issue show <number>`
6. 用户修复问题后，`crawlph issue resume <number>`

### D10: 未来扩展 - IssueProvider 接口

**选择**: 定义 Provider 接口，GitHub 作为插件

```typescript
interface IssueProvider {
  // 查询
  getIssues(projectId: string): Promise<Issue[]>;
  getIssue(projectId: string, number: number): Promise<Issue>;

  // 变更
  createIssue(projectId: string, data: CreateIssueData): Promise<Issue>;
  updateStage(issue: Issue, stage: Stage): Promise<void>;
  updateStatus(issue: Issue, status: Status): Promise<void>;
}

// MVP: 本地实现
class LocalProvider implements IssueProvider {
  constructor(private repo: IssueRepo) {}
  // 直接操作 SQLite
}

// Phase 2: GitHub 实现
class GitHubProvider implements IssueProvider {
  constructor(private octokit: Octokit) {}
  // 通过 Labels 管理状态
}
```

## Risks / Trade-offs

### R1: Agent 进程与 Server 耦合

**风险**: Server 崩溃会导致所有运行中的 agent 失败

**缓解**:
- MVP 阶段可接受（用户量小）
- Phase 2: 考虑独立的 agent 进程管理

### R2: SQLite 并发限制

**风险**: WAL 模式下写入仍有限制

**缓解**:
- MVP 场景并发量低
- 使用事务保证一致性
- 如需要，Phase 2 可迁移到 PostgreSQL

### R3: 跨平台兼容性

**风险**: better-sqlite3 需要编译

**缓解**:
- npm 提供预编译版本
- 备选：sql.js（纯 JS，无需编译）

## Open Questions

1. **Agent 超时时间**: 默认 30 分钟是否合适？是否需要可配置？
2. **设计文档存储**: 存在 SQLite 还是项目目录的 `.crawlph/designs/`？

## Migration Plan

### Phase 1: MVP (当前)

1. **重构存储层**: 实现 SQLite + Repository 模式
2. **简化数据模型**: 移除 GitHub 特定字段
3. **更新 API**: 使用 Repository 而非 GitHubClient
4. **更新 CLI**: 添加 `issue create`, `issue approve` 命令
5. **测试**: 使用 :memory: 数据库

### Phase 2: GitHub 插件

1. **定义 IssueProvider 接口**
2. **实现 GitHubProvider**
3. **配置选择**: 用户可选择 Local 或 GitHub

**Rollback**: Phase 1 代码独立可用，不依赖 GitHub。
