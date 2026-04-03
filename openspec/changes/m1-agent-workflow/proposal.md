## Why

mohist 的 M1 目标是端到端跑通：`mo issue create "..."` → `mo issue start 1` → agent 自动完成 plan → build → check → done。当前 `spawn_agent` 使用不存在的 `opencode agent --local --message` 命令，Main Agent 缺少工作流配置，无法驱动 opencode 完成实际工作。经过深入探索，我们已明确：mohist 自身是一个 LLM agent（类似 openclaw），通过 opencode acp oneshot 子进程编排各阶段执行，每个阶段的"怎么做"由 opencode skill 定义，"做什么"由 workflow.yaml 声明。

## What Changes

- **替换 spawn_agent 为 spawn_coder tool**：通过 `opencode acp --cwd <worktree>` 启动 oneshot 子进程，使用 `@agentclientprotocol/sdk` 连接 stdio JSON-RPC，每次执行一个 task 后 kill
- **新增 workflow.yaml 配置**：用户声明式定义工作流阶段（plan/build/check），每个阶段定义 prompt 模板（发给 opencode 的 task message），支持变量替换（`{issue.title}`、`{plan.output}` 等）
- **改造 Main Agent 为编排者角色**：system prompt 指导 LLM 读取 workflow.yaml，按阶段调用 spawn_coder，传递上下文，推进阶段。tools 通过 context 获取 issue 信息，无需 issue_id 参数。
- **新增 read_workflow tool**：让 agent 读取 workflow.yaml 配置，支持变量替换（`{issue.title}`、`{plan.output}` 等）
- **改造 advance_stage tool**：适配新的阶段模型，通过 context 操作当前 issue
- **移除旧的 agent prompt 系统**（`src/agent/prompts.ts`）：prompt 模板由 workflow.yaml 替代

## Capabilities

### New Capabilities
- `spawn-coder`: 通过 opencode acp 启动 oneshot coding agent 子进程，发送 task message，等待结果返回
- `workflow-config`: 解析 workflow.yaml，提供工作流阶段定义给 Main Agent

### Modified Capabilities
- `main-agent`: 从直接 spawn opencode 子进程的简单执行者，变为读取 workflow.yaml 并按阶段编排 spawn_coder 调用的智能工作流管理者

## Impact

- **核心文件改动**：`src/tools/spawn-agent.ts`（重写为 spawn-coder.ts，支持变量替换）、`src/agents/main-agent.ts`（system prompt 改造，context 传递 issue）、新增 `src/tools/spawn-coder.ts`、`src/tools/read-workflow.ts`、`src/workflow/workflow-loader.ts`
- **新增依赖**：`@agentclientprotocol/sdk`（ACP 客户端）、`yaml`（workflow.yaml 解析）
- **配置层**：新增 `workflow.yaml` schema 和解析逻辑（项目级配置，内置默认 fallback）
- **移除**：`src/tools/spawn-agent.ts`（旧的 spawn 命令）
- **阶段模型**：`plan → build → check → done`，`approval: true` 仅作标记不暂停 agent，用户手动 `mo issue approve` 推进
