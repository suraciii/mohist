# Agent Session Reference for Config-Driven Tasks

## 探索背景

#229 暴露了 Plan 阶段的 session 语义回归：产品设计上，Plan 阶段的多个 artifact task 应该可以共享同一个 coder agent session；实际运行中，配置驱动 StageRunner 把 `proposal`、`design`、`tasks`、`self-review` 拆成了多个独立 coder session。

这次探索聚焦一个设计问题：在统一 task runtime 中，应该如何表达“多个 task 使用同一个 agent session”，同时保留 task 作为用户可见进度单元。

## 关键发现

Task 和 Session 不应该一一绑定。

```text
Task
  用户可见的工作单元
  负责状态、attempt、artifact 验证和最终结果

AgentSession
  AI 开发者的一段工作记忆和执行过程
  负责对话、工具调用、transcript、可观察性和取消/关闭
```

当前 config-driven runtime 的问题是把 `agent-session` task 默认解释成“每个 task 创建并关闭一个 session”。这破坏了 Plan 的连续规划体验：

```text
当前行为:
  proposal     -> new session -> close
  design       -> new session -> close
  tasks        -> new session -> close
  self-review  -> new session -> close

期望行为:
  proposal     ┐
  specs        │
  design       ├─ same agent session: plan-artifacts
  tasks        │
  self-review  ┘
```

## 被推翻的命名

### `reusePreviousTaskSession`

这个模型不稳定。它依赖“上一个 task”的相对关系，遇到 skip、restore、retry、rerun、动态插入 task 时语义会变得含糊：

```text
specs restored from disk
design says "reuse previous"

previous 是 specs?
还是最近一个真正启动过 session 的 proposal?
```

### `sessionGroup`

这个名字也不准确。它听起来像“一组 session”，但实际表达的是多个 task 指向同一个 session。

## 决策与结论

更准确的模型是：task 显式引用一个 agent session。

```text
TaskDefinition / AgentSessionTaskInput
  agentSession?: string
```

语义：

```text
agentSession omitted
  每个 task 使用自己的独立 session
  等价于 task:<taskId>

agentSession: "plan-artifacts"
  使用名为 plan-artifacts 的同一个 agent session
  多个 task 引用同一个名字，就复用同一个真实 session
```

这支持三种形态：

```text
1. 每个 task 独立 session

   T1 -> task:T1
   T2 -> task:T2

2. 整个 stage 一个 session

   proposal     -> plan-artifacts
   specs        -> plan-artifacts
   design       -> plan-artifacts
   tasks        -> plan-artifacts
   self-review  -> plan-artifacts

3. 一个 stage 内多个连续 session

   proposal     -> requirements
   specs        -> requirements
   design       -> requirements

   tasks        -> implementation-plan
   self-review  -> implementation-plan
```

## 为什么不放在 TaskExecutionPolicy

`agentSession` 更像 agent-session task 的执行输入，而不是横切 policy。

```text
TaskExecutionPolicy
  适合表达执行策略:
    - maxAttempts
    - timeout
    - mutatesWorktree
    - requiresWorktreeLease
    - failure handling

AgentSessionTaskInput
  适合表达这个 task 如何调用 agent:
    - prompt
    - cwd
    - output artifact
    - agentSession
```

`service-call` task 和 `ralph-task` 不需要 `agentSession`。把它放到 `AgentSessionTaskInput` 可以避免 `TaskExecutionPolicy` 变成杂物袋。

## 产品不变量

- Task 是用户可见进度单元，Session 是 agent 对话容器。
- 一个 Session 可以覆盖多个 Task。
- 一个 Task 应引用一个 AgentSession；默认是 task-local session。
- 多个 Task 引用同一个 `agentSession` 名称时，应复用同一个真实 session。
- Task 仍然拥有 artifact finalization：session timeout 或 session failure 是 task attempt evidence，不应直接变成 workflow final failure。
- Rerun stage 应创建新的 agent session 实例，不复用旧 transcript 继续写。
- Restore/skip 的 task 不应破坏后续 task 的 session 归属，因为归属来自显式 `agentSession` 引用，而不是“上一个 task”。

## UI 含义

Issue detail 仍展示 task list。Session 展示应允许一个 transcript 覆盖多个 task：

```text
Plan transcripts
  plan-artifacts
    proposal
    specs
    design
    tasks
    self-review
```

如果一个 stage 有多个 named agent sessions：

```text
Plan transcripts
  requirements
    proposal
    specs
    design

  implementation-plan
    tasks
    self-review
```

## 建议落地方向

1. 在 `AgentSessionTaskInput` 增加 `agentSession?: string`。
2. `createPlanAgentSessionDispatchTask` 为 Plan artifact tasks 填入 `agentSession: "plan-artifacts"`。
3. `AgentSessionTaskHandler` 通过 session registry/manager 按稳定 key 获取 session。
4. 稳定 key 至少包含 issue、workflow run/stage attempt、stage、agentSession name、cwd、model。
5. Handler 不再无条件在每个 task finally 关闭 session；session 关闭应发生在 session owner 边界：
   - referenced tasks 全部完成；
   - task 最终失败；
   - workflow abort/stop；
   - rerun/rewind/retry stage 清理旧运行态。
6. `coder_session` / transcript API 应能表达一个 session 覆盖多个 task prompt blocks。

## 开放问题

- session owner 边界应由 `AgentSessionTaskHandler` 自己根据 WorkflowRun 判断，还是由 `ConfigDrivenStageRunner` 在 stage/work completion 后统一 close？
- named agent session 的运行态是否需要 first-class 持久化在 `WorkflowRun.StageRun.sessions[]`，还是仅由 `coder_session` 和 task output 中的 `acpSessionId` 投影？
- 对于 stage 中动态追加的 runtime task，是否允许引用既有 named agent session，还是默认必须 task-local？
