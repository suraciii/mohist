## Why

三个问题。

1. **Plan 阶段工件质量差。** PlannerAgent 在一次 `streamText()` 中让 LLM 返回包含所有工件的 JSON，tasks 看不到 specs 和 design 的实际内容。`generateTasksFromSpecs()` 用正则提取 spec 标题生成 tasks，丢失依赖、类型、AFK/HITL 标记。

2. **MainAgent 在做不需要判断力的事。** Stage 转换顺序是固定的（plan → build → review → done），LLM 来决定"该不该进下一步"是浪费 token 且不可靠。这是一个 pipeline，应该由程序驱动。

3. **架构过度嵌套。** Pipeline 编排 → MainAgent (LLM orchestrator) → PlannerAgent (LLM orchestrator) → streamText。三层嵌套，MainAgent 和 PlannerAgent 都是在用 LLM 做程序该做的事。

## What Changes

- **BREAKING**: 删除 `MainAgent`（`runMainAgent`、`runAgentLoop`、整个 agent session 层）。Workflow pipeline 由程序直接驱动 stage 转换。
- **BREAKING**: 删除 `PlannerAgent` 类。Plan 阶段由 pipeline 通过**复用的 ACP 连接**按 artifact 分轮次生成：proposal → specs → design → tasks。
- **BREAKING**: 删除 `ReviewerAgent` 类。Review 阶段同样由 pipeline 通过复用 ACP 连接执行 reviewer prompt。
- **新增 ACP 多轮连接支持**：`AcpConnection` 类封装 `initialize` → `newSession` → 多次 `prompt` → `cleanup` 的生命周期。
- Workflow pipeline（重写后的 `WorkflowController`）按固定顺序执行 stage：plan → gate → build → gate → review → done。不需要 LLM 决策流转。
- `Explore` 保持为独立的非 pipeline 能力：通过 `ExploreService` 直接调用 `runAcpSession()` 生成 proposal.md，不再经过 MainAgent。
- 删除 `execute_stage`、`advance_stage`、`submit_approval`、`spawn_coder`、`generate_tasks` 等 MainAgent tools。
- 删除 `generateTasksFromSpecs()` 程序化路径。
- 删除 `planner-default.yaml`。
- 更新 `context-assembler.ts`：识别 tasks.json 新增的 `mode`/`type`/`output`/`dependsOn` 字段，拼入 task prompt。

## Capabilities

### New Capabilities
- `pipeline-controller`: 程序驱动的 workflow pipeline，按固定顺序执行 stage，在 gate 处暂停等人类审批
- `acp-connection`: 支持多轮 `prompt()` 的 ACP 连接封装，供 plan/review stage 复用会话上下文
- `planner-prompt`: 各 artifact 类型的 prompt 配置，每轮 prompt 只生成一个 artifact
- `reviewer-prompt`: Review 阶段的 prompt 配置
- `gate-controller`: Pipeline 的 gate 暂停/恢复机制，基于 `approvalState` 状态机
- `explore-service`: 独立的 Explore 入口，直接调用单轮 `runAcpSession()`

### Modified Capabilities
- `workflow-controller`: 从"被 MainAgent tool call 触发"变为"程序驱动的 pipeline 主循环"
- `ralph-executor`: 保持不变，已被 pipeline 直接调用
- `context-assembler`: 支持 task 的 mode/type/output/dependsOn 字段拼入 prompt

## Impact

- **packages/cli/src/agent-runtime/acp-session.ts**: 新增 `createAcpConnection()` / `AcpConnection` 类，支持多轮 prompt
- **packages/cli/src/agents/main-agent.ts**: 整个文件删除
- **packages/cli/src/agents/planner-agent.ts**: 整个文件删除
- **packages/cli/src/agents/reviewer-agent.ts**: 整个文件删除
- **packages/cli/src/agent-runtime/agent-loop.ts**: 整个文件删除
- **packages/cli/src/tools/execute-stage.ts**: 删除
- **packages/cli/src/tools/advance-stage.ts**: 删除
- **packages/cli/src/tools/submit-approval.ts**: 删除
- **packages/cli/src/tools/spawn-coder.ts**: 删除
- **packages/cli/src/tools/self-review.ts**: 删除程序化 task 生成函数
- **packages/cli/src/tools/add-comment.ts**: 删除
- **packages/cli/src/tools/get-issue.ts**: 删除
- **packages/cli/src/tools/read-workflow.ts**: 删除
- **packages/cli/src/tools/archive-change.ts**: 保留，改为 pipeline 直接调用
- **packages/cli/src/tools/ask-user.ts**: 保留，仅用于 Explore service
- **packages/cli/src/agents/prompts/**: 删除 `planner-default.yaml`，新增 artifact-level prompt 文件
- **packages/cli/src/workflow/workflow-controller.ts**: 重写为 pipeline controller
- **packages/cli/src/services/agent-runner-service.ts**: 从 session-based runner 改为 pipeline runner
- **packages/cli/src/openspec/context-assembler.ts**: 增加新字段的 prompt 拼装
- **packages/cli/src/cli/commands/issue.ts**: `mo issue approve` 改为直接更新 `approvalState`
