# Agent Instructions

## Project Overview

mohist 是一个 AI 驱动的开发工作流自动化工具，使用本地 SQLite 存储，通过 opencode agents 自动完成 Issue 的设计、实现和审查。

## 目录职责

| 目录 | 职责 | 内容 |
|------|------|------|
| `packages/server/` | 新后端核心实现 | ASP.NET Core + Orleans + Issue/Workflow API |
| `packages/runner/` | Runner 实现 | shared runner host、action catalog、agent/process/check actions |
| `packages/web/` | Web UI | React + Vite + Tailwind + TanStack Query |
| `design/` | 技术设计 | 架构设计、技术规格、流程设计 |
| `docs/` | 项目文档 | CONTRIBUTING、使用指南等补充文档 |

## 核心实现结构

```
packages/server/
├── src/Mohist.Server/
│   ├── Api/                    # REST API 路由 + Endpoint Filter
│   ├── Issue/                  # Issue 域 / Grain / Querier / 工作流 profile
│   ├── Workflow/               # WorkflowGrain + workflow domain
│   ├── Runner/                 # Runner Grain + embedded runner bridge
│   ├── Sessions/               # Agent session telemetry
│   ├── Project/, Epic/         # Project / Epic 域 (Bounded Contexts)
│   ├── Events/                 # SignalR Hub + 事件桥接
│   ├── SystemInfo/             # runtime build/update/service info
│   ├── Infrastructure/
│   │   ├── Data/               # EF Core DbContext + Store 实现
│   │   ├── Hosting/            # DI / API / Silo 注册
│   │   ├── Events/             # IEventBus + InMemoryEventBus
│   │   ├── Config/             # ConfigService + 文件系统抽象
│   │   ├── Workspace/, Orleans/, Serialization/, Files/
│   │   └── ...
│   └── Program.cs
└── tests/Mohist.Server.Tests/  # 后端 spec/集成测试
    ├── Specs/{Workflow,Issue,Project,Epic,Runner,Sessions,Skills,SystemSpecs,Api,Foundation}/
    ├── Architecture/           # ArchUnitNET 规则
    └── Support/                # 共享 Fixture / TestData / Traits

packages/runner/
├── src/                        # TypeScript runner runtime
└── package.json                # standalone runner package

packages/web/
├── src/                        # React Web UI
└── tests/                      # Web UI tests
```

## 工作流

工作流是**用户可配置的**：每个项目可选择或自定义自己的 workflow 模板（YAML），由 runner 解释执行。**AGENTS.md 不记录具体 stage 名称或职责**——这些会在模板和实现之间持续漂移。默认模板和当前 stage 设计见 `packages/server/src/Mohist.Server/Issue/Services/WorkflowProfiles/` 下的 yaml 文件，运行时调度契约见 `design/workflow-scheduling.md`。

Issue 自身有独立于 workflow stage 的生命周期状态机（`IssueStatus` 枚举），定义在 `Issue/Domain/IssueStatus.cs`：

| Issue 状态 | 含义 |
|------|------|
| `Backlog` | 已创建但未启动 workflow |
| `InProgress` | workflow 运行中 |
| `Done` | workflow 成功完成 |
| `Cancelled` | 显式关闭或归档 |

Explore 不再是 Mohist runtime 内置功能。探索/需求澄清通过 `mo skills install` 安装的外部 agent skill（如 `mohist-explore`）完成；Mohist runtime 只管理 issue、workflow、审批、产物和执行调度。

## 常用命令

```bash
# 开发
npm run build
npm test
npm run build:web

# 运行 Server
npm run dev:server

# Web UI 开发模式
npm run dev:web
```

## 数据存储

```
~/.mohist/
├── mohist.db       # SQLite 数据库
├── config.jsonc    # 配置文件
└── logs/           # 日志文件
```

## Web UI

服务启动后访问 `http://localhost:3456`。实时更新通过 **SignalR hub**（`/hubs/events`）推送，共 **45 种事件类型**（`EventBusEventTypes.All`）。SSE 路由已删除（旧 commit `6fb8b239a5`）。

## 非显而易见的发现

### Agent Runner
- Runner 是独立 TypeScript 进程（`packages/runner/`），通过 `opencode acp --pure` spawn + `@agentclientprotocol/sdk` 与外部 `opencode` runtime 通信
- 超时分三级：session (30min) / stage (60min) / task (10min)
- Prompt 在 `packages/server/src/Mohist.Server/Workflow/Services/Prompts/builtins/` 中定义（`.prompt` 文件，YAML frontmatter + XML/正文 格式）

### 工作流状态机
- 只能顺序推进，不能跳过阶段
- 状态机由 `WorkflowGrain`（Orleans Grain）驱动
- Stage 状态在 `StageRun` / `TaskRun` / `StageCheck` 领域模型中表达
- 状态持久化通过 EF Core `WorkflowRunRow.State` JSON 列 + ETag 乐观锁（不依赖 Orleans `IPersistentState`）
- Grain 单线程激活模型替代旧 `WorkflowEngine` + `Mutex` + `CheckpointManager`

### 审批流
- 用户审批的 stage 由 workflow 模板在 yaml 中声明（典型默认模板包含两个审批 gate：设计与 AI 审查）
- 审批暂停时 UI 通过 `stage_changed` 事件 + `availableActions` 字段推断待办（**`approval_requested` SSE 事件当前未 emit**，见 `EventBusEventTypes.cs:13` 是死注册）
- 通过/驳回后自动入队 `resume-pipeline`
- 失败回退：当前实现用 `rerun` action 目标 `run.CurrentStageId`，**不跨阶段回退**——驳回和失败都重跑当前 stage，跨阶段回退待修

### Provider 接口
- 当前实现统一通过 **opencode** runtime 委托 agent 工作
- `OpencodeRoutes.cs:/api/opencode/runtime` 返回 `{ mode: "local-opencode", command, model }`
- 模型在 `~/.mohist/config.jsonc` 中以字符串形式配置
- 多 provider 架构（anthropic/openai/glm/kimi/deepseek/minimax/qwen）是历史设计意图，**当前未实现**

### OpenSpec 集成
- 基于 OpenSpec 的 proposal → specs + design → tasks 模型
- 产物存放在 `openspec/changes/{slug}/`
- 模板中可声明自我审查与 AI 审查 stage 产出 self-review.md / review.md 制品
- 模板中可声明整合 stage 自动同步增量规格到主规格 + 归档 change
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
| 存储 | 旧 Node SQLite 访问层 | SQLite / PostgreSQL (via Entity Framework Core) |

### 重构策略

- **渐进式迁移**：不是一次性替换，而是按领域逐步迁移，TypeScript 和 .NET 并存
- **API 兼容**：ASP.NET Core 后端提供与现有 Hono 后端相同的 REST API，前端无需改动
- **第一步：Issue Workflow → Orleans**：将 issue 的生命周期状态机建模为 Orleans Grain

### 重构决策记录

#### Decision 1: Issue Workflow → Orleans Grain

**日期**: 2026-05-22
**决策**: 将每个 Issue 的 workflow 建模为 Orleans Grain，但 issue 自身状态与 workflow 状态分离在两个不同 Grain 中。

**核心 Grain 设计思路**:
- `IIssueGrain` — 持 issue 自身状态（标题、描述、状态机 Backlog/InProgress/Done/Cancelled、关联 workflow run id）
- `IWorkflowGrain` — 持 workflow 运行状态（`WorkflowRun` 领域模型），由 issue start 时通过 `GrainFactory.GetGrain<IWorkflowGrain>(wrId)` 调起
- 状态通过自定义 `IStateStore<T>` + EF Core DbContext 持久化到 `WorkflowRunRow.State` JSON 列 + ETag 乐观锁
- 通过 Orleans Reminder / Timer 实现 timeout、retry、auto-fix
- Approval gate 通过 Grain Method 调用（`ApproveAsync` / `RejectAsync`）
- 事件通过自建进程内 `IEventBus` 推送到 SignalR Hub（**不用 Orleans Streams**）

**映射关系**:
| 旧 TypeScript 设计 | 实际 .NET 实现 |
|---------------------|-------------|
| `IIssueWorkflowGrain`（issue + workflow 合并） | `IIssueGrain` + `IWorkflowGrain`（分离） |
| Grain State 持久化 | EF Core JSON 列 + ETag 乐观锁 |
| Orleans Stream (`IAsyncObservable`) 推 SSE | `IEventBus` + SignalR `/hubs/events` |
| `CheckpointManager` 断点续传 | EF Core 持久化，Silo 重启自动恢复 |
| Orphan scan 定时器 | **未实现**（`WorkflowGrain` 仅 1 个 heartbeat reminder） |

**不做的**:
- 不改变前端 API 契约
- 不改变 workflow 领域模型的核心语义（Stage/Task/Check/Approval）
- 不在此步迁移 Provider、Config、Web UI

#### Decision 2: 单项目结构 + Central Package Management

**日期**: 2026-05-22
**决策**: .NET 后端使用单项目 `Mohist.Server`（非多项目拆分），各 Bounded Context 作为命名空间目录而非独立项目。

**项目结构**:
```
packages/server/
├── Mohist.sln
├── Directory.Build.props          # 统一 net11.0
├── Directory.Packages.props       # CPM 统一版本管理
├── src/Mohist.Server/
│   ├── Workflow/
│   │   ├── Domain/                # 领域模型 (WorkflowRun, StageRun, ...)
│   │   ├── Grains/                # Orleans Grain (IWorkflowGrain, WorkflowGrain)
│   │   ├── Services/              # Querier / ProfileManager / ActivityQuerier
│   │   └── Surrogates/            # Orleans 序列化代理
│   ├── Issue/                     # 同 Workflow/ 布局
│   ├── Project/, Epic/, Runner/, Sessions/, Events/, ...
│   ├── Infrastructure/            # Data/Hosting/Events/Config/Orleans/...
│   └── Mohist.Server.csproj
└── tests/Mohist.Server.Tests/
    ├── Specs/<BoundedContext>/
    ├── Architecture/
    └── Support/
```

**理由**: 当前阶段以迁移为主，无需过早拆分项目。领域模型和 Grain 在同一项目中减少序列化/反序列化开销，后续按需拆分。

#### Decision 3: WorkflowRunner → WorkflowGrain 迁移

**日期**: 2026-05-22
**决策**: 将 TypeScript `WorkflowRunner`（run loop + Mutex + LoopController）迁移为 Orleans `WorkflowGrain`，利用 Grain 单线程保证替代 Mutex，利用 EF Core 持久化替代 WorkflowStore。

**关键差异**: WorkflowGrain 不使用 while 循环 + wake/wait 模式。外部（API 调用、Reminder）通过调用 Grain Method（`StartAsync`、`ApproveAsync`、`ResumeAsync`）驱动状态推进，Grain 内部 `RunLoop` 处理到下一个挂起点（await-approval / failed / complete / paused）即返回。

**已实现 vs 文档设计**:
- ✅ Grain 单线程激活模型替代 Mutex
- ✅ Grain Method 调用直接驱动（无事件循环）
- ✅ EF Core 持久化
- ❌ 旧文档提到的 `IHandlerRegistry` / `ITaskHandler` / `ICheckHandler` / `ITaskLoader` 抽象未单独抽出；任务执行逻辑内联在 `WorkflowGrain.PrepareWorkAsync` / `WorkLease` 中
- ❌ 旧文档提到的 `tryInjectRetryTask` → `TryInjectRetryTask` 内置 helper 未命名/未抽
