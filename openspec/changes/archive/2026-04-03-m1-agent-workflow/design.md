## Context

mohist M1 的核心路径：`mo issue start 1` → Main Agent 读取 workflow.yaml → 按阶段 spawn opencode acp → 完成 plan→build→check→done。

当前状态：
- `spawn-agent.ts` 使用不存在的 `opencode agent --local --message` 命令
- Main Agent 的 system prompt 是硬编码的，不支持 workflow.yaml 配置
- 没有 opencode acp 集成
- 没有 skill 注入机制

参考架构：
- openclaw：LLM agent + tools 模式，`sessions_spawn` tool 启动 oneshot 子 agent，push-based 结果回调
- nanoclaw：极简哲学，polling over events，文件 IPC，容器隔离即授权
- opencode acp：stdio JSON-RPC，`initialize → session/new → session/prompt → 等响应 → kill`

## Goal / Non-Goals

**Goals:**
- 端到端跑通 `mo issue start 1` → plan → build → check → done
- mohist 作为 LLM agent 编排 opencode acp 子进程
- workflow.yaml 定义工作流阶段和 prompt 模板
- 支持可配置的 model provider（已有 `llm.ts`）

**Non-Goals:**
- 多用户 / 并发 issue（M2+）
- GitHub provider（M2+）
- workflow approval 自动暂停 agent（M1 手动 `mo issue approve` 推进）
- Web UI / 进度面板（M2+）
- persistent acp session（M1 用 oneshot）
- nanoclaw 式容器隔离（M1 不需要，单用户本地工具）

## Decisions

### D1: mohist 是 LLM agent，不是纯代码编排

**选择**: mohist 自身是一个 AI SDK v5 agent loop（`streamText` + `tools` + `stopWhen`），LLM 自主决定如何编排工作流。

**替代方案**: 纯 TypeScript 代码按 workflow.yaml 顺序执行 stage。
- 否决原因：失去灵活性。LLM 能处理异常、调整策略、决定重试。代码编排无法处理"check 失败后回去修 build"这种动态决策。

**参考**: openclaw 的 agent 也是 LLM 自主决策，system prompt 指导但不强制。

### D2: 每次 spawn 新的 opencode acp session（oneshot）

**选择**: 每个 stage 独立 spawn `opencode acp --cwd <worktree>`，完成一个 task 后 kill。Main Agent 作为上下文搬运工，在 task message 中传递必要信息。

**替代方案**: 一个 acp session 跨 plan→build→check 多次 prompt。
- 否决原因：生命周期管理复杂；oneshot 模式更简单可靠，跟 openclaw 的 oneshot subagent 一致。

**权衡**: 每次 session 的 coding agent 没有前序上下文。但：
- 代码变更在 worktree 文件系统里，新 session 能看到
- Main Agent 在 task message 里传递高层指引
- 简单性 > 上下文连续性

### D3: workflow.yaml 是给 LLM 看的配置，不是给代码解析的

**选择**: `read_workflow` tool 读取 workflow.yaml 原文，LLM 自己理解并决定如何执行。

**替代方案**: TypeScript 代码解析 workflow.yaml 生成执行计划。
- 否决原因：增加代码复杂度，LLM 完全能理解 YAML 配置。参考 openclaw 的 skill 系统也是 LLM 驱动。

### D4: 使用 `@agentclientprotocol/sdk` 连接 opencode acp

**选择**: spawn `opencode acp --cwd <worktree>` 子进程，通过 `@agentclientprotocol/sdk` 的 `Client` 类连接 stdio JSON-RPC。

**替代方案 A**: `opencode run --format json`（同步子进程）。
- 否决原因：run 有 `OPENCODE_SERVER_PASSWORD` bug；不支持实时进度；无法取消。

**替代方案 B**: 内嵌 opencode SDK（`@mariozechner/pi-coding-agent`）。
- 否决原因：耦合 opencode 版本；pi-mono 包可能不公开；mohist 的目标是版本无关。

### D5: M1 不做 skill 注入

**选择**: M1 不实现 opencode skill 注入机制。task message 由 Main Agent 的 system prompt 直接指导 opencode 怎么做每个阶段。skill 注入留作 M2+。

**原因**: 减少变更范围。M1 的核心目标是跑通 agent + acp + workflow 编排，skill 是锦上添花。

### D6: 阶段模型简化

**选择**: `plan → build → check → done`，审批点由 workflow.yaml 的 `approval: true` 配置。

**当前代码**: `draft → designing → waiting-design-review → implementing → waiting-review → done`
- 已经有 Stage enum 包含 plan/build/check/done
- `advance-stage.ts` 的 `M1_ALLOWED_TRANSITIONS` 已经定义了 plan→build→check→done
- 只需清理旧的 stage（draft 可保留为初始状态）

## Architecture

```
┌──────────────────────────────────────────────────────────┐
│  mohist Main Agent (AI SDK v5 agent loop)                │
│                                                          │
│  System Prompt:                                          │
│    "你是开发工作流编排者。读取 workflow.yaml，              │
│     按阶段调用 spawn_coder，传递上下文，推进阶段。"         │
│                                                          │
│  Tools:                                                  │
│  ├── spawn_coder(task, cwd)  → opencode acp oneshot      │
│  ├── read_workflow()          → 读取 workflow.yaml        │
│  ├── advance_stage(issue_id, stage)                      │
│  ├── add_comment(issue_id, body)                         │
│  └── get_issue(issue_id)                                 │
└──────────────────────┬───────────────────────────────────┘
                       │
         ┌─────────────┼─────────────┐
         ▼             ▼             ▼
   ┌──────────┐ ┌──────────┐ ┌──────────┐
   │ acp #1   │ │ acp #2   │ │ acp #3   │
   │ plan     │ │ build    │ │ check    │
   │ oneshot  │ │ oneshot  │ │ oneshot  │
   │ kill     │ │ kill     │ │ kill     │
   └──────────┘ └──────────┘ └──────────┘
```

### Issue 上下文传递机制

Main Agent 通过 `MainAgentContext` 获取完整的 issue 信息，不需要 tools 接收 issue_id 参数：

```typescript
export interface MainAgentContext {
  issueRepo: IssueRepo;
  commentRepo: CommentRepo;
  worktreePath: string;
  llmConfig?: LlmConfig;
  issue: Issue;              // 当前处理的完整 issue 对象
}
```

**tools 设计**：`advance_stage`、`add_comment`、`get_issue` 等 tools 不需要 `issue_id` 参数，它们直接操作 context 中的当前 issue。这简化了 tool 调用，避免 LLM 需要记住和传递 ID。

### Approval 机制（M1 简化版）

workflow.yaml 中的 `approval: true` 在 M1 仅作为标记，**不暂停 Main Agent**：

```
用户执行 `mo issue start 1`
  ↓
Main Agent 执行 plan 阶段
  ↓
plan 完成，workflow.yaml 显示 approval: true
  ↓
Main Agent 添加 comment: "Plan 完成，等待审批"
  ↓
Main Agent 结束（不自动进入 build）
  ↓
用户执行 `mo issue approve` 或 `mo issue start 1` 继续
  ↓
进入 build 阶段
```

M1 的审批是"软性"的——agent 执行完标记为 approval 的阶段后主动结束，用户手动推进下一阶段。这避免了在 AI SDK agent loop 中实现等待用户输入的复杂性。

```
spawn_coder(task, cwd)
  │
  ├─ 1. spawn("opencode", ["acp", "--cwd", cwd])
  │     stdio: ['pipe', 'pipe', 'pipe']
  │
  ├─ 2. 创建 ACP Client 连接 stdin/stdout
  │     const client = new Client(...)
  │
  ├─ 3. client.initialize()
  │     等待 initialized 响应
  │
  ├─ 4. client.createSession()
  │     获取 session ID
  │
  ├─ 5. client.prompt(sessionId, task)
  │     发送 task message
  │
  ├─ 6. 等待响应（监听 session/update 通知）
  │     - agent_message_chunk → 累积文本
  │     - tool_call_update → 进度日志
  │     - 收到完整响应 → 提取文本
  │
  ├─ 7. proc.kill()（清理子进程）
  │
  └─ 8. 返回结果文本给 Main Agent
```

### workflow.yaml schema (M1)

```yaml
stages:
  - stage: plan
    prompt: "分析 issue #{issue.number}: {issue.title}，探索 codebase，产出实现计划"
    approval: false              # 是否等待用户确认才进入下一阶段
    timeout: 600                 # 秒

  - stage: build
    prompt: "按 plan 阶段的计划实现 {issue.title}。计划摘要：{plan.output}"
    approval: true               # plan 完成后暂停等用户确认
    timeout: 1800

  - stage: check
    prompt: "检查 {issue.title} 的实现：运行测试、lint、typecheck，报告问题"
    approval: false
    timeout: 600
```

**prompt 字段**：发给 opencode 的 task message 模板。支持变量替换：
- `{issue.title}` / `{issue.number}` / `{issue.body}` — 当前 issue 信息
- `{plan.output}` / `{build.output}` / `{check.output}` — 前序阶段的 spawn_coder 返回结果
- `{project.path}` — 项目路径

**变量替换机制**：由 `spawn_coder` tool 内部处理。调用时传入 `taskTemplate` 和 `variables` 对象，spawn_coder 完成字符串替换后再发送给 opencode acp。例如：
```typescript
spawn_coder({
  taskTemplate: "分析 issue #{issue.number}: {issue.title}",
  variables: {
    issue: { number: 3, title: "用户登录" },
    plan: { output: "计划内容..." }
  }
})
```

**workflow.yaml 配置层级**：
1. 项目根目录 `workflow.yaml`（用户自定义）
2. `.mohist/workflow.yaml`（项目级覆盖）
3. 内置默认 workflow（plan→build→check→done）

M1 只实现项目级配置，不实现全局配置（`~/.mohist/workflow.yaml` 留作 M2+）。

### 文件变更清单

```
packages/cli/src/
├── tools/
│   ├── spawn-agent.ts        → 删除（替换为 spawn-coder.ts）
│   ├── spawn-coder.ts        → 新建（ACP oneshot 集成）
│   ├── advance-stage.ts      → 小改（已有 M1 transitions，调整 issue_id 传递）
│   ├── add-comment.ts        → 保留
│   ├── get-issue.ts          → 保留
│   └── read-workflow.ts      → 新建（读取 workflow.yaml）
├── agents/
│   └── main-agent.ts         → 重写（新 system prompt，新 tool 集合）
├── workflow/
│   └── workflow-loader.ts    → 新建（workflow.yaml 解析 + 默认值）
└── agent-runtime/
    ├── agent-loop.ts         → 保留
    ├── llm.ts                → 保留
    ├── session.ts            → 保留
    └── tool.ts               → 保留
```

## Risks / Trade-offs

| Risk | Impact | Mitigation |
|------|--------|------------|
| `@agentclientprotocol/sdk` API 不稳定 | spawn_coder 可能需要频繁适配 | 参考 openclaw 的 `acp/client.ts` 实现作为 fallback，必要时直接实现 JSON-RPC |
| ACP session/prompt 响应格式不确定 | 解析结果可能出错 | M1 先做最小实现，只提取最终文本结果；流式进度作为日志输出 |
| Main Agent LLM 质量不够 | 编排决策差，task message 写不好 | M1 使用 claude-sonnet-4 作为默认模型；system prompt 足够详细 |
| workflow.yaml 用户覆盖出问题 | agent 行为异常 | M1 提供合理的默认 workflow，文档说明配置方式 |

## Open Questions

- `@agentclientprotocol/sdk` 的 `Client` 类具体 API 是什么？需要从 openclaw 的 `acp/client.ts` 或 npm 包源码确认
- opencode acp 的 `session/prompt` 响应完成后，如何判断"完成"？是收到某个通知类型，还是 session 状态变为 idle？

