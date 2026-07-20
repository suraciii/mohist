# Agent 执行模型

本文定义 Workflow、Agent、Session、Runner 与 Runtime adapter 共享的抽象边界。
Runtime 特有行为放在 [`runtimes/`](runtimes/README.md)，例如
[`runtimes/opencode.md`](runtimes/opencode.md)。

## 层次

| 层次 | 概念 | 所有者 | 权威状态 |
|---|---|---|---|
| 定义 | Mohist Agent | Agent context | 身份、instructions、config、skills、状态 |
| 工作 | TaskRun | Workflow context | Workflow task 生命周期、结果、输出、恢复 |
| 工作 | AgentJob | Agent context | 一次 Mohist Agent 执行的生命周期与结果 |
| 执行契约 | Action | Workflow context | 一次工作 dispatch 的 `uses` / `with` 输入输出契约 |
| 对话 | AgentSession | Session context | transcript、context、usage、Runtime binding、lineage |
| Runtime | Runtime Session | 外部 Runtime | 物理对话与 provider 执行状态 |
| Adapter | OpenCodeRuntime、未来的 Pi adapter | Runner 进程 | protocol、进程、事件、状态核对、错误 |

`Inline Agent` 是产品使用方式，不是另一个实体或 bounded context。它表示 Workflow
TaskRun 直接选择 Runtime 特有的 Action 并提供输入，不解析 Mohist Agent。

## 规范术语

- **Mohist Agent（Named Agent）**：Project 范围内可复用的预定义资源，有稳定 Agent ID。
- **Inline Agent**：Workflow task 直接配置并调用 Action，没有 Agent ID。
- **AgentJob**：Mohist Agent 的一次执行，使用启动时固定的 Agent snapshot。
- **AgentSession**：稳定的逻辑对话与审计记录；不是 Agent 身份，也不拥有工作生命周期。
- **Runtime Session**：OpenCode、Pi 或其他执行后端拥有的物理对话。

## 调用路径

| 路径 | 工作所有者 | Runner 入口 | AgentSession 来源 |
|---|---|---|---|
| Workflow 直接调用 | TaskRun | `mohist/opencode` Action adapter | Workflow |
| 启动 Mohist Agent | AgentJob | AgentJob executor | Agent launch |

```text
Workflow: TaskRun -> mohist/opencode Action adapter --+
                                                       +-> OpenCodeRuntime -> Runtime Session
Agent: Mohist Agent -> AgentJob -> AgentJob executor --+
```

两条路径共享 Runner 执行能力和 Session 基础设施，但不共享工作所有者：TaskRun 对
Workflow 工作负责，AgentJob 对 Mohist Agent 工作负责。每个入口把已经解析好的
AgentSession 目标交给 `OpenCodeRuntime`，Runtime 事实写回该 Session。共享 Runtime
代码不能制造 Workflow -> Agent 的领域依赖。

## Action 语义

`mohist/opencode` 是 Runtime 特有的 Action，回答“用 OpenCode 执行这个回合”。它不接收
Agent ID，不解析 Agent 名称，不读取 Agent 定义，也不创建 AgentJob。因此 Workflow
直接使用它时形成 Inline Agent。

未来的 `mohist/pi` 等 Runtime Action 与它处于同一层。本设计有意不定义
`mohist/agent` 契约；该名称留给后续 Mohist Agent 专项设计，不能在这里充当 Runtime
别名或 `mohist/opencode` 的通用包装。

AgentJob 路径不能通过公开的 `mohist/opencode` Action 契约 dispatch。Agent 定义完成
解析和 snapshot 后，其 executor 接收由 Agent 拥有的 execution request。Workflow Action
adapter 与 AgentJob executor 都可以调用同一个 `OpenCodeRuntime` 深模块。复用点是
Runtime 实现，不是 Action。

## 工作生命周期与对话

TaskRun 与 AgentJob 拥有以下决策：

- pending / running / terminal 状态；
- 成功、失败与结果；
- retry、recovery 或 Workflow 推进。

AgentSession 拥有以下事实：

- 用户 / agent 消息和 tool calls；
- context 与 usage；
- model / Runtime observations；
- 当前 Runtime Session 绑定与会话沿革（lineage）。

Workflow Action adapter 向 TaskRun 报告工作结果，AgentJob executor 向 AgentJob 报告
工作结果；两者都向 AgentSession 报告 Runtime 事实。AgentSession 事件不会推进 Workflow，
也不会让 AgentJob 进入终态。失败的 AgentSession 操作可以成为工作所有者判断的证据，
但 Session 不是裁判。

Session 命令不是工作 dispatch。执行中提交的 Follow-up 成为当前回合输入；空闲时提交
Follow-up 会启动一个用户发起的对话回合，只记录命令和 Runtime 事实，不创建 TaskRun
或 AgentJob。Compact 与 Reset 遵循相同的 Session-only 所有权规则，且都只在逻辑
Session 空闲时执行；两者都不轮换 AgentSession ID，命令响应返回同一稳定
`sessionId`，只有 Reset 替换 Runtime 绑定。

## AgentSession 来源

每个 AgentSession 有且只有一个不可变来源。

### Workflow 来源

使用 `(projectId, workflowRunId, sessionName)` 寻址。同一 WorkflowRun 内复用相同名称
会继续逻辑对话。省略显式名称时使用 Work ID，避免无关 task 意外共享 context。

### Agent launch 来源

每次启动 Mohist Agent 时创建，并关联已解析的 Agent ID。一个 Mohist Agent 可以创建
多个 AgentJob 和 AgentSession。之后编辑或归档 Agent，不改变 Session 来源或启动时的
执行 snapshot。

相同 prompt、model、Runtime、workspace 或配置不会合并两个来源。Session 不能从
Workflow 来源迁移为 Agent 来源，反之亦然。

来源特有的 route 只是查询和便利入口，最终都解析为以 `sessionId` 标识的规范
AgentSession 资源；`(workflowRunId, sessionName)` 和 `agentId` 都不能替代 Session 身份。

Follow-up、Compact、Reset、transcript 与查询都作用于该规范资源。来源特有的 CLI 或
API 可以先解析它，但不能实现第二套 Session 生命周期。

## 逻辑与物理 Session 身份

AgentSession ID 是逻辑对话的稳定身份。Runtime Session 身份是外部物理维度：

```json
{
  "runtime": "opencode",
  "runtimeSessionId": "ses_..."
}
```

Runtime 变化、工作目录变化和 Reset 可以替换物理绑定并追加 lineage，但不能改变
AgentSession 身份或来源。Compact 和 model / variant 选择变化不会替换物理绑定。

持久化的当前绑定只保留 Runner 重启后继续控制所需的最小数据：`runtime`、
`runtimeSessionId`、`runnerId` 与 `workDir`。Lineage 记录 `runtime`、
`runtimeSessionId` 与 `boundAt`。

## Mohist Agent 启动

Agent context 负责组装启动请求：

1. 按 ID 或名称解析 active Mohist Agent；
2. 把 Agent ID、instructions、config 与 launch prompt 固定到 AgentJob input；
3. 创建并打开 Agent launch 来源的 AgentSession；
4. 把 AgentJob dispatch 给合适的 Runner；
5. Runtime executor 只处理已经组装好的回合输入与 Session 绑定。

Runtime adapter 不再查询 Agent 定义。这样并发修改 Agent 不会改变执行中的输入字节，
Runtime 模块也不依赖 Agent context。

## 模块边界

- Workflow 拥有 TaskRun 与 `uses` / `with` Action 契约。
- Agent 拥有 Mohist Agent、AgentJob、启动组装、AgentJob execution request 与报告校验。
- Session 拥有 AgentSession 身份、metadata、transcript、usage 与 lineage。
- Runner context 只记录执行资源是否在线及其容量，不拥有 Agent 或 Session 语义。
- Runner 进程执行 dispatch 并适配外部 Runtime，不拥有业务实体。

Runtime adapter 接收由 Mohist 定义的回合 / Session 请求并返回规范化事实。它不能
暴露 SDK 类型、解析 Agent 定义、决定 Workflow transition 或拥有 job status。

## 不变量

- Action 不是 Agent。
- AgentSession 不是 Agent，也不是工作所有者。
- Inline Agent 没有 Agent ID 或可复用定义。
- Mohist Agent 有稳定身份，可以拥有多次执行和多个 Session。
- 一次 dispatch 的工作所有者只能是 TaskRun 或 AgentJob 之一。
- 每个 AgentSession 只有一个不可变来源。
- 替换 Runtime Session 不改变 AgentSession 来源或逻辑身份。
- `mohist/opencode` 不暴露 OpenCode 原生 agent 选择。
- AgentJob 执行不依赖 Workflow Action 名称或 Action Input 契约。
- 共享 `OpenCodeRuntime` 不制造 Workflow -> Agent context 依赖。
