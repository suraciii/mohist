## Why

启动 Agent 后若响应丢失、客户端断线或 Mohist 重启，调用方目前无法用同一启动意图确认工作是否已受理，只能重试并冒着重复执行的风险。AgentJob、AgentSession、SessionInput 与 AgentTurn 分别记录工作、会话、输入和执行轮次；现在需要让它们以可幂等的启动和可恢复的观察面成为可追溯的产品事实。

## What Changes

- 为 Web 与 CLI 的 Agent 启动请求引入稳定调用身份；同一身份的重试始终解析为原有的 AgentJob、AgentSession、首条 SessionInput 和首个 AgentTurn，而非创建重复工作或输入。
- 将启动的受理、排队、执行、完成、失败和结果未知表达为独立且持久的事实。无法确认投递或执行结果时保留 Unknown，不自动重放输入或把它改写为 Failed。
- 扩展启动响应和读取面，使调用方能稳定引用 Job、Session、Input 与 Turn，并在重连或服务、Runner 重启后继续读取各自状态、回复和 transcript，无需维持原始长连接。
- 让 Web 与 CLI 使用同一状态解释和恢复入口，明确每种启动状态的下一步，而不从 Session 状态推断 AgentJob 的首次执行结果。
- 保持首个 AgentJob 结束后的 AgentSession 可用，供后续独立的会话能力继续使用；本变更不交付 follow-up、取消或停止语义。

## Capabilities

- `agent-launch-idempotency`: Agent 启动以稳定调用身份受理，原子建立或重现唯一的 AgentJob、AgentSession、首条 SessionInput 与首个 AgentTurn，并在超时、断线或重启后的重试中避免重复领域效果。
- `agent-launch-observation`: 启动工作及其会话、输入和轮次的可恢复读取契约，涵盖持久状态、Unknown 表达、回复与 transcript 的续读，以及 Web 和 CLI 一致的状态解释与恢复入口。

## Impact

- **Server** (`packages/server/src/Mohist.Server/Agent/`, `Sessions/`, `Api/`): 启动路由与 `AgentLauncher`、AgentJob/AgentSession 持久状态和读取 DTO/API 需要支持调用身份、Input/Turn 事实与恢复观察；Server 继续作为状态裁判，Runner 仅报告执行事实。
- **Web** (`packages/web/src/entities/agent/`, Agent 启动与会话页面): 启动 mutation 需携带并保留调用身份，断线或响应丢失后可恢复到既有工作，并呈现统一的 Job、Session、Input 与 Turn 状态。
- **CLI** (`packages/cli/Mohist.Cli/`): `mo agent launch` 需发送调用身份、输出所有稳定引用，并提供从已知引用继续读取启动结果和会话记录的路径。
- **Runner** (`packages/runner/`): 需在重启或重新连接后报告可与持久 Input/Turn 对账的执行事实，不取得重试、状态推进或自动重放的决策权。
- **Testing**: 覆盖响应丢失、调用重试、服务或 Runner 重启、队列等待、Unknown 与断线续读；测试使用 fake Runtime、时间和存储边界，不访问真实外部环境。
