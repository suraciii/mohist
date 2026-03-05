## Why

当前缺乏一个自动化系统来驱动 spec-driven development workflow。手动处理 Issues、生成 specs、实现代码、Review 的过程效率低下且容易遗漏步骤。

crawlph 通过 Ralph Coding 自动化循环，实现从 Issue 到 Production 的全自动 spec-driven development 流程。

## Architecture Change (2025-03-05)

**从纯 Skill 架构迁移到专用 Agent 架构**

原因：
- 用户需要 **后台持续运行** (watch mode)，这会长期占用 main session
- 用户需要 **多种交互方式**：Telegram/Discord channel、GitHub webhook、main session
- 专用 Agent 可以完全隔离，不影响用户的正常使用

## What Changes

### 架构变化

- 新增 **专用 Agent** (`crawlph`)，替代纯 Skill 架构
- Agent 启动后自动进入 **watch mode**，每 60s 检查新 Issues
- 通过 **bindings** 路由 Telegram/Discord 消息到 crawlph agent
- 支持 **GitHub webhook** 事件触发（需要 webhook 配置）

### 功能保持

- 7-stage spec-driven workflow
- Ralph Loop（无限重试直到成功）
- OpenSpec CLI 集成
- Design + Implementation 合并在同一个 PR
- 并发处理最多 8 个 Issues

## Capabilities

### New Capabilities

- `issue-orchestration`: Issue 检测、过滤、claim-based tracking、并发处理（max 8）
- `ralph-loop`: Agent 层无限重试循环，每次 spawn 干净上下文的 sub-agent
- `workflow-stages`: 7-stage workflow（Exploration → Refinement → Design → Implementation → Review → Done + Re-evaluation）
- `openspec-integration`: 在 OpenCode 中调用 openspec CLI 命令生成 specs
- `pr-lifecycle`: PR 创建（Draft）、状态流转（Draft → Open）、Review、合并
- `progress-reporting`: 通过 Channel 发送进度通知，Issue Comments 记录里程碑
- `state-persistence`: 文件存储（`~/.openclaw/agents/crawlph/data/`）用于 claims、cursor、进度追踪
- `agent-bindings`: 通过 bindings 配置实现 Telegram/Discord channel 消息路由

### Modified Capabilities

无（全新 capability）

## Impact

### 新增文件

- `~/.openclaw/workspace-crawlph/AGENTS.md` - Agent 核心逻辑
- `~/.openclaw/workspace-crawlph/SOUL.md` - Agent persona
- `~/.openclaw/workspace-crawlph/TOOLS.md` - Agent 工具说明

### 依赖

- OpenClaw multi-agent 架构
- OpenCode（通过 ACP runtime 调用）
- OpenSpec CLI（Design 阶段生成 specs）
- GitHub API（Issue/PR 操作）
- Telegram/Discord Channel（进度通知）

### 状态存储

- 位置：`~/.openclaw/agents/crawlph/data/`
- 文件：
  - `crawlph-claims.json` - Issue claims
  - `crawlph-cursor.json` - Watch mode cursor
  - `progress/issue-{N}.json` - 每个 Issue 的进度

### 配置变更

- `~/.openclaw/openclaw.json` - 添加 crawlph agent 配置和 bindings
