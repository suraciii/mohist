# mohist

AI 驱动的开发工作流自动化工具，使用本地 SQLite 存储，通过 opencode agents 自动完成 Issue 的设计、实现和审查。

## 特性

- **结构化工作流**: Draft → Plan → Build → Check → Integrate → Done（Explore 为独立模式）
- **Web UI**: 看板、Issue 详情、活动监控、探索对话、设置（AI/Agent/System）、日志、归档
- **CLI**: 11 个命令组，60+ 子命令
- **实时事件流**: 基于 SSE 的 49 种事件类型，agent 进度、任务状态、merge 状态实时推送
- **OpenSpec 工作流**: proposal → specs → design → tasks → self-review → build → check → integrate
- **健康门控**: Plan/Build/Integrate 阶段自动运行健康检查（typecheck、build、test），失败自动修复
- **AI 驱动修复**: agent 产物缺失、健康门控失败、审查发现问题、合并冲突 → 自动修复
- **Provider 管理**: 内置 anthropic/openai/glm/kimi/deepseek/minimax/qwen，支持自定义 OpenAI 兼容 provider
- **Agent 运行时配置**: 分层 timeout（session/stage/task）、并发控制、重试策略
- **断点续传**: 支持中断恢复，从检查点继续执行
- **Merge Queue + Worktree 管理**: 每个 issue 独立 worktree，快进合并

## 安装

```bash
npm install -g mohist
```

依赖：
- Node.js >= 18.0.0
- opencode CLI

## 快速开始

```bash
# 1. 配置 AI provider
mo providers login anthropic

# 2. 启动服务
mo server start

# 3. 初始化项目
mo init

# 4. 创建并启动 Issue
mo issue create "Add search feature" --body "用户需要搜索功能" --label enhancement --priority p1
mo issue start 1

# 5. 在 Plan gate 审批通过后，自动进入 Build → Check → Integrate → Done
mo issue approve 1
```

服务运行在 `localhost:3456`，打开浏览器即可访问 Web UI。

## CLI 命令参考

### 服务管理 (`mo server`)

| 命令 | 说明 |
|------|------|
| `mo server start` | 启动服务（daemon 模式） |
| `mo server stop` | 停止服务 |
| `mo server status` | 查看服务状态（PID、端口、运行时间、版本） |
| `mo server restart` | 重启服务 |
| `mo server logs [-n <行数>]` | 查看服务日志 |
| `mo server update` | 重建并重启（源码模式） |
| `mo server install` | 安装为 systemd 用户服务 |
| `mo server uninstall` | 卸载 systemd 用户服务 |

### 项目管理 (`mo project`)

| 命令 | 说明 |
|------|------|
| `mo project create <name> --path <path> --base-branch <branch>` | 创建项目 |
| `mo project list` | 列出所有项目 |
| `mo project use <name>` | 切换当前项目 |
| `mo project show <name>` | 查看项目详情 |
| `mo project remove <name>` | 删除项目 |
| `mo init [name]` | 初始化当前目录为项目 |

### Issue 管理 (`mo issue`)

| 命令 | 说明 |
|------|------|
| `mo issue create <title> -b <body> -l <label> -p <level>` | 创建 Issue |
| `mo issue list [-s <stage>] [-l <label>] [-p <level>] [--archived] [--all]` | 列出 Issue |
| `mo issue show <number>` | 查看 Issue 详情（含合并状态、检查结果） |
| `mo issue update <number> --title <text> --body <text> -l <+label\|-label>` | 更新 Issue |
| `mo issue start <number>` | 启动 Pipeline |
| `mo issue approve <number>` | 审批通过 |
| `mo issue reject <number> -m <reason>` | 审批驳回 |
| `mo issue close <number>` | 关闭 Issue |
| `mo issue reopen <number>` | 重新打开 |
| `mo issue resume <number> [--skip-to-review]` | 恢复执行 |
| `mo issue comment <number> <text>` | 添加评论 |
| `mo issue delete-comment <number> <comment-id>` | 删除评论 |
| `mo issue diff <number>` | 查看代码差异 |
| `mo issue logs <number> [-f]` | 查看工作流日志 |
| `mo issue archive [<number>\|--all-completed]` | 归档 Issue |
| `mo issue unarchive <number>` | 取消归档 |

### 其他命令

| 命令 | 说明 |
|------|------|
| `mo propose <number> [--force]` | 为 Issue 创建 OpenSpec Change 并启动 Plan |
| `mo status [--all]` | 查看项目状态 |
| `mo config [--list\|<key> [<value>]]` | 查看/设置配置 |
| `mo attach [-p <project>] [-f]` | 实时监听 agent 事件（交互式 REPL） |
| `mo providers list` | 列出已配置的 AI provider |
| `mo providers login <provider>` | 配置 AI provider |
| `mo providers logout <provider>` | 移除 AI provider |
| `mo skills install [--force]` | 安装共享 agent skills |
| `mo skills update` | 更新共享 agent skills |
| `mo skills list` | 列出已安装 skills |
| `mo label list` | 列出标签 |

## Web UI

服务启动后访问 `http://localhost:3456`：

| 页面 | 路由 | 说明 |
|------|------|------|
| 看板 | `/` | 6 列看板（Plan/Build/Check/Integrate/Done），移动端 Tab 切换 |
| Issue 详情 | `/issue/:number` | Pipeline 视图、代码差异、评论、合并管理、agent 会话 |
| 会话详情 | `/issue/:number/session/:sessionId` | Coder agent 完整对话 transcript |
| 活动监控 | `/activity` | 运行中/等待中/最近完成的 agent 任务 |
| 探索 | `/explore` / `/explore/:id` | 与 AI 自由对话，梳理需求 |
| 设置 | `/settings/:section` | AI 模型/Agent 运行时/系统设置 |
| 日志 | `/logs` | 实时服务器日志查看 |
| 归档 | `/archived` | 已归档 Issue 列表 |

实时更新通过 SSE 事件推送，无需刷新页面。

## 工作流

```
                    Explore (Pipeline 外)
                    AI 面试，梳理需求
                    产出 proposal.md
                           │
                           ▼
Draft ──→ Plan ──→ Build ──→ Check ──→ Integrate ──→ Done
           ▲                    │           │
           │                    ▼           ▼
         Backlog             Build       Build
                           (驳回)     (集成失败)
```

### 阶段说明

| 阶段 | 职责 | 产物 | Gate |
|------|------|------|------|
| Explore | 结构化面试，梳理需求 | proposal.md | — (Pipeline 外) |
| Plan | 技术设计、拆分任务 | specs/ + design.md + tasks.json + self-review.md | 用户审批 + 健康门控 |
| Build | 逐个执行任务，写代码，内循环 write→test→fix | 代码变更 | 健康门控 + 全任务完成 |
| Check | AI 代码审查 | review.md | 用户审批 + merge-ready |
| Integrate | 规格同步、归档、合并到主干 | 合并后的代码 | 集成后健康门控 |
| Done | 完成 | — | — |

### 用户审批点

- **Plan gate**: 技术方案和任务拆分确认后，AI 开始写代码
- **Check gate**: 审查代码和合并状态，确认后进入集成

### 自动修复

- Plan 阶段产物缺失 → AI 自动修复（1 次尝试）
- 健康门控失败 → AI 自动修复（可配置重试次数）
- Check 审查发现问题 → AI 自动修复（默认 3 次重试）
- 合并冲突 → AI 自动 rebase（默认 2 次重试）

## OpenSpec 工作流

基于 OpenSpec 的 proposal → specs + design → tasks 模型，产物存放在 `openspec/changes/{slug}/`：

```
openspec/changes/{slug}/
├── proposal.md           # Explore 产出：意图、范围、用户故事
├── specs/                # Plan 产出：增量规格 (GIVEN/WHEN/THEN)
├── design.md             # Plan 产出：技术方案、架构决策
├── tasks.json            # Plan 产出：有序任务列表
├── self-review.md        # Plan 产出：Agent 自我审查
├── review.md             # Check 产出：AI 审查报告
└── (archive → openspec/changes/archive/)  # Integrate 归档
```

### 快速开始

```bash
# 从 Issue 创建 Change 并启动 Plan
mo propose 42

# 审批 Plan，进入 Build
mo issue approve 42

# Build 完成后审批 Check，进入 Integrate
mo issue approve 42
```

## 配置

配置存储在 `~/.mohist/config.jsonc`。

### AI Provider 配置

```bash
# CLI 方式
mo providers login anthropic
mo providers login openai
mo providers login custom-provider  # 自定义 OpenAI 兼容 provider

# Web UI 方式
# Settings → AI → Available Providers → Connect
```

### Agent 运行时配置

Web UI 中 `Settings → Agent` 可配置：

| 配置项 | 默认值 | 说明 |
|--------|--------|------|
| Session Timeout | 30 min | 单次 agent 会话超时 |
| Stage Timeout | 60 min | 单个阶段超时 |
| Task Timeout | 10 min | 单个任务超时 |
| Max Concurrent | 8 | 最大并发 agent 数 |
| Poll Interval | 30s | 状态轮询间隔 |
| Retry Budget | 2 | 最大容错次数 |

## 数据存储

```
~/.mohist/
├── mohist.db       # SQLite 数据库
├── config.jsonc    # 配置文件
└── logs/           # 日志文件
```

## 架构

```
┌──────────────────────────────────────────────────────────────┐
│                        Web UI (React)                        │
│                     localhost:3456                            │
├──────────────────────────────────────────────────────────────┤
│                      HTTP API (Hono)                         │
│                   60+ REST 端点 + SSE 流                      │
├──────────────────────────────────────────────────────────────┤
│                    Services 业务逻辑层                         │
├──────────────┬──────────────────┬────────────────────────────┤
│ Agent Runner │  Workflow Engine │  Merge Queue               │
│ (opencode)   │  (状态机)         │  (快进合并)                 │
├──────────────┴──────────────────┴────────────────────────────┤
│                      SQLite 数据层                            │
└──────────────────────────────────────────────────────────────┘
```

- **Fat Server**: 所有业务逻辑、agent 执行、状态管理在服务端
- **Thin CLI**: 通过 HTTP API 与服务端通信
- **SSE 实时推送**: 49 种事件类型，agent 进度实时可见

## 开发

```bash
# 工作目录
cd packages/cli

# 安装依赖
npm install

# 构建（backend + web）
npm run build

# 运行测试
npm test

# Web UI 测试
npm run test:web

# 代码检查
npm run lint

# 类型检查
npm run typecheck

# Web UI 开发模式
npm run dev:web
```

## 要求

- Node.js >= 18.0.0
- opencode CLI

## License

MIT