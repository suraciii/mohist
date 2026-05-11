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
│   │   └── prompts/            # 阶段 prompt (plan/build/check/explore/review 等)
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
│       ├── components/         # 页面组件 (看板/详情/活动/探索/设置/日志/归档)
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

Explore 为 Pipeline 外的独立模式，通过 AI 面试梳理需求，产出 proposal.md。

### 各阶段职责

| 阶段 | 职责 | Gate |
|------|------|------|
| Explore | AI 面试梳理需求，产出 proposal.md | — (Pipeline 外) |
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

使用 openspec-explore 模式进行设计讨论时，讨论内容应自动记录到 `talks/` 目录。文件名格式：`YYYY-MM-DD-<主题>.md`（主题由 agent 根据讨论内容自动拟定）。

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