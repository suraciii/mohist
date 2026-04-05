## Why

当前 `mo issue approve` 创建全新 session，丢失之前 agent 的所有上下文（plan 输出、工具调用历史等）。没有 session continuity，gate 暂停/恢复机制不成立——用户审批后 build 阶段在无 plan 上下文的情况下执行，导致质量差或失败。这是 M2 所有交互能力的架构级前置依赖。

## What Changes

- **SessionManager 增加按 issueId 查找 session 的能力**，支持从暂停点恢复
- **runMainAgent 支持 resume 模式**：接受已有 session，在已有消息历史基础上继续 LLM loop
- **AgentRunnerService 增加 resume 方法**：查找已有 session，注入用户消息（如 "User approved. Continue."），恢复 agent loop
- **approve API 改为调用 resume 而非 start**，正确传递跨阶段上下文
- **新增 mo approve CLI 命令**，让用户能从命令行审批 gate
- **修复默认 workflow**：check 阶段加上 `approval: true`，与 PRD 定义的 gate_after: human 对齐

## Capabilities

### New Capabilities

- `session-resume`: Session 恢复机制——按 issueId 查找已有 session，在已有消息历史上注入用户消息并恢复 agent loop

### Modified Capabilities

- `main-agent`: Main Agent 支持 resume 模式，gate 暂停时不关闭 session，approve 时恢复而非重建
- `agent-runtime`: SessionManager 增加 findByIssueId()，session 支持 paused 状态
- `pipeline-model`: 默认 workflow 的 check 阶段增加 approval: true，与 PRD gate_after: human 对齐

## Impact

- **agent-runtime/session.ts**: 新增 findByIssueId()，session 增加 paused 状态（不 close，保留 messages）
- **agents/main-agent.ts**: runMainAgent 接受可选的已有 session，resume 时注入用户消息
- **services/agent-runner-service.ts**: 新增 resume() 方法，持有 session→issueId 映射
- **api/issues.ts**: approve endpoint 改为调用 agentRunner.resume() 而非 start()
- **workflow/workflow-loader.ts**: 默认 workflow check 阶段加 approval: true
- **cli/commands/issue.ts**: 新增 mo issue approve 命令
