# Agent Instructions

## Project Overview

mohist 是一个 AI 驱动的开发工作流自动化工具，使用本地 SQLite 存储，通过 opencode agents 自动完成 Issue 的设计、实现和审查。

## 目录职责

| 目录 | 职责 | 内容 |
|------|------|------|
| `packages/cli/` | 核心实现 | CLI + Server + Agent Runner + Web UI |
| `prd/` | 产品文档 | 产品定位、功能规划、用户故事 |
| `prd/backlog/` | 产品待办 | 从设计讨论中搁置延后的事项，按类别分组，标注所属 Milestone |
| `design/` | 技术设计 | 架构设计、技术规格、流程设计 |
| `docs/` | 用户文档 | README、CONTRIBUTING、使用指南 |
| `talks/` | 设计讨论 | 日期归档的架构探索与设计决策记录，文件名格式：`YYYY-MM-DD-<主题>.md` |
| `opensrc/openclaw/` | 参考源码 | openclaw 项目源代码，供架构参考 |
| `opensrc/nanoclaw/` | 参考源码 | nanoclaw 项目源代码，极简 AI agent 框架（~8K 行），供架构参考 |

## 核心实现结构

```
packages/cli/
├── bin/                        # CLI 入口 (mo / mo-server)
├── src/
│   ├── agent-runtime/          # Agent 运行时管理
│   ├── agent-skills/           # Skill 调度和执行
│   ├── agents/                 # Agent 提示词和配置
│   │   └── prompts/            # 阶段 prompt (plan/build/check/review 等)
│   │       └── artifacts/      # 产物模板 (proposal/specs/design/tasks 等)
│   ├── api/                    # REST API 路由 (issues/projects/config/providers 等)
│   ├── artifacts/              # 产物读写服务
│   ├── cli/                    # CLI 命令实现
│   ├── config/                 # 配置管理
│   ├── db/                     # SQLite 数据层
│   ├── git/                    # Git 操作 (worktree/diff/merge/commit)
│   ├── openspec/               # OpenSpec 集成 (规格同步/变更归档)
│   ├── project/                # 项目管理
│   ├── server/                 # HTTP Server (Hono)
│   ├── services/               # 业务逻辑层
│   ├── tools/                  # 工具集合
│   ├── types/                  # TypeScript 类型定义
│   ├── util/                   # 通用工具
│   ├── utils/                  # 工具函数
│   └── workflow/               # 工作流引擎 (Plan/Build/Check/Integrate runners)
├── web/                        # Web UI (React + Vite + Tailwind + TanStack Query)
│   └── src/
│       ├── components/         # 页面组件 (看板/详情/活动/设置/日志/归档)
│       ├── hooks/              # React hooks (含 useSSE 实时事件)
│       ├── lib/                # API 客户端、类型定义
│       └── context/            # React context (ProjectContext 等)
├── tests/                      # 后端测试
└── dist/                       # 编译输出
```

## 工作流阶段

```
Draft → Plan → Build → Check → Integrate → Done
  ↑       ↑                    ↑          ↑
Backlog  (用户审批)          (用户审批)  (自动合并)
```

Explore 不再是 Mohist runtime 内置功能。探索/需求澄清通过 `mo skills install` 安装的外部 agent skill（如 `mohist-explore`）完成；Mohist runtime 只管理 issue、workflow、审批、产物和执行调度。

### 各阶段职责

| 阶段 | 职责 | Gate |
|------|------|------|
| Plan | 技术设计，拆解任务 | 用户审批 + 健康门控 (typecheck) |
| Build | 逐个执行任务 (AFK/HITL) | 健康门控 (build) + 全任务完成 |
| Check | AI 代码审查 | 用户审批 + merge-ready 检查 |
| Integrate | spec sync + 归档 + 合并 + 集成后健康检查 | 集成后门控 (build+test) |
| Done | 完成 | — |

## 常用命令

```bash
# 开发
cd packages/cli && npm run build
cd packages/cli && npm test

# 运行 Server
cd packages/cli && npm run server

# CLI 使用
node bin/mo server start
node bin/mo issue list
node bin/mo issue start 1
```

## 数据存储

```
~/.mohist/
├── mohist.db       # SQLite 数据库
├── config.jsonc    # 配置文件
└── logs/           # 日志文件
```

## 探索讨论记录

探索讨论由外部 agent skill 完成。需要保留结论时，外部 agent 可将提炼后的发现记录到 `.mohist/explores/` 或 `talks/`，也可以通过 `mo issue create` 创建 Mohist issue。Mohist server 不保存 Explore session/chat runtime。

## Web UI

服务启动后访问 `http://localhost:3456`。实时更新通过 SSE（Server-Sent Events）推送，共 49 种事件类型。

## 非显而易见的发现

### Agent Runner
- 使用 `opencode agent --local --message "..."` spawn 子进程
- 超时分三级：session (30min) / stage (60min) / task (10min)
- Prompt 在 `src/agents/prompts/` 中定义（YAML + Markdown 格式）
- 支持 ACP (Agent Client Protocol) 通信

### 工作流状态机
- 只能顺序推进，不能跳过阶段
- `WorkflowEngine` 控制状态流转
- 每个阶段有独立的 `StageRunner`（Plan/Build/Check/Integrate）
- 每个 StageRunner 包含 tasks → checks → auto-fix 流程
- `CheckpointManager` 支持断点续传

### 审批流
- Plan 和 Check 阶段有 `UserApprovalCheck`
- 审批暂停时触发 `approval_requested` SSE 事件
- 通过/驳回后自动入队 `resume-pipeline`
- Check 驳回回到 Build 阶段（非 Plan）
- Integrate 失败也回到 Build 阶段

### Provider 接口
- 内置 anthropic/openai/glm/kimi/deepseek/minimax/qwen
- 支持自定义 OpenAI 兼容 provider
- Provider 配置存储在 `~/.mohist/config.jsonc`
- 支持分阶段模型覆盖（Plan/Build/Check/Integrate 各用不同模型）

### OpenSpec 集成
- 基于 proposal → specs + design → tasks 模型
- 产物存放在 `openspec/changes/{slug}/`
- `self-review.md` 在 Plan 阶段自动生成（agent 自审）
- `review.md` 在 Check 阶段自动生成（AI 审查）
- Integrate 阶段自动同步增量规格到主规格 + 归档 change
- 支持 Ralph 式任务执行（逐个任务、断点续传、失败恢复）

## 后端重构：ASP.NET Core / Orleans

### 测试分层约定

- `Specs/` 下的 spec 测试应尽可能集成、尽可能完整，表达用户可观察的产品形态和端到端行为。
- Workflow 相关 spec 优先通过 `WorkflowGrain`、Runner、API 编排等产品路径验证，不直接针对 `WorkflowRun` / `StageRun` 等领域模型写 spec。
- 领域模型、技术组件、工具函数可以做普通单元测试；这些测试用于验证局部规则，不承担产品 spec 叙事。
- 私有方法不直接测试；通过 public API 和产品路径观察其效果。

### 重构目标

将 mohist 后端从 TypeScript/Node.js 单体逐步迁移到 **ASP.NET Core + Orleans** 方案，提升分布式能力、可靠性和可维护性。

| 维度 | 现状 (TypeScript) | 目标 (ASP.NET Core + Orleans) |
|------|-------------------|-------------------------------|
| 运行时 | Node.js 单进程 | .NET 8 + Orleans Silo |
| HTTP | Hono | ASP.NET Core Minimal API |
| 状态管理 | 内存 Map + SQLite | Orleans Virtual Actor + 持久化 Grain State |
| 并发调度 | 手动内存队列 + Map | Orleans Grain 天然并发安全 |
| 工作流引擎 | 旧 TypeScript workflow runtime（已移除） | Orleans Grain 状态机 (每个 issue = 一个 Grain) |
| Agent 进程管理 | spawn `opencode agent` 子进程 | Orleans Grain 管理 Agent 生命周期 |
| 事件推送 | 内存 EventBus + SSE | Orleans Stream + SSE |
| 存储 | SQLite (better-sqlite3) | SQLite / PostgreSQL (via Entity Framework Core) |

### 重构策略

- **渐进式迁移**：不是一次性替换，而是按领域逐步迁移，TypeScript 和 .NET 并存
- **API 兼容**：ASP.NET Core 后端提供与现有 Hono 后端相同的 REST API，前端无需改动
- **第一步：Issue Workflow → Orleans**：将 issue 的生命周期状态机建模为 Orleans Grain

### 重构决策记录

#### Decision 1: Issue Workflow → Orleans Grain

**日期**: 2026-05-22
**决策**: 将每个 Issue 建模为一个 `IssueWorkflowGrain`（StatelessWorker 或 Reentrant），管理其完整的生命周期状态机。

**核心 Grain 设计思路**:
- `IIssueWorkflowGrain` — 一个 issue 一个 Grain，持有 issue 的 workflow state
  - 状态: Draft → Plan → Build → Check → Integrate → Done
  - 内嵌 C# `WorkflowRun` 领域模型
  - 通过 Orleans Reminder / Timer 实现 timeout、retry、auto-fix
  - Approval gate 通过 Grain Method 调用（`SubmitApprovalAsync`）
  - 事件通过 Orleans Stream 推送到 SSE endpoint

**映射关系**:
| 现有 TypeScript 组件 | Orleans 对应 |
|---------------------|-------------|
| `AgentRunnerService` (1118行) | 多个 Grain: `IIssueWorkflowGrain` + `IAgentSchedulerGrain` |
| 内存 `runningSlots` / `pendingQueues` Map | Grain 内部状态 + Orleans 并发保证 |
| `WorkflowRun` / `StageRun` / `TaskRun` 领域模型 | Grain State (可序列化) |
| `EventBus` (内存事件) | Orleans Stream (`IAsyncObservable`) |
| `WorkflowRuntime` / `WorkflowRunner` | Grain 方法编排 |
| `CheckpointManager` 断点续传 | Grain 天然持久化，Silo 重启自动恢复 |
| `UserApprovalCheck` 暂停等待 | Grain Method + `await` 或 Reminder 轮询 |
| Agent 子进程 spawn | `IAgentProcessGrain` 管理进程生命周期 |
| Orphan scan 定时器 | Orleans Reminder / Silo 周期扫描 |

**不做的**:
- 不改变前端 API 契约
- 不改变 workflow 领域模型的核心语义（Stage/Task/Check/Approval）
- 不在此步迁移 Provider、Config、Web UI

#### Decision 2: 单项目结构 + Central Package Management

**日期**: 2026-05-22
**决策**: .NET 后端使用单项目 `Mohist.Server`（非多项目拆分），Workflow 作为命名空间目录而非独立项目。

**项目结构**:
```
packages/server/
├── Mohist.sln
├── Directory.Build.props          # 统一 net10.0
├── Directory.Packages.props       # CPM 统一版本管理
├── src/Mohist.Server/
│   ├── Workflow/
│   │   ├── Domain/                # 领域模型 (WorkflowRun, StageRun, ...)
│   │   ├── Grains/                # Orleans Grain (IWorkflowGrain, WorkflowGrain)
│   │   └── Handlers/              # Handler 接口 + Registry
│   └── Mohist.Server.csproj
└── tests/Mohist.Server.Tests/
```

**理由**: 当前阶段以迁移为主，无需过早拆分项目。领域模型和 Grain 在同一项目中减少序列化/反序列化开销，后续按需拆分。

#### Decision 3: WorkflowRunner → WorkflowGrain 迁移

**日期**: 2026-05-22
**决策**: 将 TypeScript `WorkflowRunner`（run loop + Mutex + LoopController）迁移为 Orleans `WorkflowGrain`，利用 Grain 单线程保证替代 Mutex，利用 Grain 持久化替代 WorkflowStore。

**映射关系**:
| TypeScript WorkflowRunner | C# WorkflowGrain |
|---------------------------|------------------|
| `Mutex` (内存锁) | Orleans Grain 天然单线程访问（Reentrant） |
| `LoopController` (wake/wait) | Grain Method 调用直接驱动（无需事件循环） |
| `WorkflowStore.save()` | Grain State 持久化（后续接入） |
| `HandlerRegistry` | `IHandlerRegistry`（DI 注入） |
| `TaskHandler.run()` | `ITaskHandler.RunAsync()` |
| `CheckHandler.run()` | `ICheckHandler.RunAsync()` |
| `TaskLoader.load()` | `ITaskLoader.LoadAsync()` |
| `AbortSignal` 中断 | `PauseAsync()` / Orleans Cancellation |
| `tryInjectRetryTask` | 内置 `TryInjectRetryTask` |

**关键差异**: WorkflowGrain 不使用 while 循环 + wake/wait 模式。外部（API 调用、Reminder）通过调用 Grain Method（`StartAsync`、`ApproveAsync`、`ResumeAsync`）驱动状态推进，Grain 内部 `RunLoop` 处理到下一个挂起点（await-approval / failed / complete / paused）即返回。
