## Why

当前缺乏一个自动化系统来驱动 spec-driven development workflow。手动处理 Issues、生成 specs、实现代码、Review 的过程效率低下且容易遗漏步骤。

crawlph skill 通过 Ralph Coding 自动化循环，实现从 Issue 到 Production 的全自动 spec-driven development 流程。

## What Changes

- 新增 `crawlph` skill，实现 7-stage spec-driven workflow
- 支持手动触发、`--watch` 持续轮询、`--cron` 定时触发三种模式
- 集成 OpenSpec CLI 生成设计规范
- 实现 Ralph Loop（无限重试直到成功）
- Design + Implementation 合并在同一个 PR（Draft → Open → Merged）
- 自动化 Agent Review + 用户 Review 流程
- 并发处理最多 8 个 Issues

## Capabilities

### New Capabilities

- `issue-orchestration`: Issue 检测、过滤、claim-based tracking、并发处理（max 8）
- `ralph-loop`: Orchestrator 层无限重试循环，每次 spawn 干净上下文的 sub-agent
- `workflow-stages`: 7-stage workflow（Exploration → Refinement → Design → Implementation → Review → Done + Re-evaluation）
- `openspec-integration`: 在 OpenCode 中调用 openspec CLI 命令生成 specs
- `pr-lifecycle`: PR 创建（Draft）、状态流转（Draft → Open）、Review、合并
- `progress-reporting`: 通过 Telegram/其他 Channel 发送进度通知
- `state-persistence`: 文件存储（/data/.clawdbot/）用于 claims、cursor、进度追踪

### Modified Capabilities

无（全新 skill）

## Impact

- 新增 OpenClaw skill：`skills/crawlph/SKILL.md`
- 依赖：OpenCode（通过 ACP runtime 调用）
- 依赖：OpenSpec CLI（Design 阶段生成 specs）
- 依赖：GitHub API（Issue/PR 操作）
- 依赖：Telegram/Channel API（进度报告）
- 状态存储：`/data/.clawdbot/crawlph-*.json`
