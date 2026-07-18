# OpenCode Runtime

## 决策

`mohist/opencode` 是直接基于 `@opencode-ai/sdk/v2` 实现的 Runtime 特有 Action。它与
Agent / Session 的所有权模型（Inline Agent、工作所有者、共享 Runtime 不制造依赖等
不变量）见 [`agent-execution.md`](../agent-execution.md)。

ACP adapter 直接移除，不保留 fallback。现有 AgentJob 执行也必须离开 ACP，通过
由 Agent 拥有的 executor 使用同一个 `OpenCodeRuntime` 能力，而不是依赖 Workflow Action
契约。本设计不定义 `mohist/agent` Action，也不重新设计 Mohist Agent 产品。

当前只实现 OpenCode Runtime。未来可以增加 Pi Action，但本次不会为它预先引入通用
`AgentRuntime` 接口。稳定边界是 Workflow Action 契约、AgentJob 执行契约
和 Session 命令，不是假想的跨 Runtime SDK wrapper。

## Action 输入输出契约

```ts
type OpenCodeActionInput = {
  prompt: PromptSpec
  session?: string
  options?: {
    model?: string
    variant?: string
  }
}

type OpenCodeActionOutput = null | {
  promise: string
}
```

现有 prompt resolver 在进入 Runtime 前把 `prompt` 解析为非空字符串。`model` 使用
OpenCode 的 `providerID/modelID` 形式。OpenCode model ID 自身可能继续包含 `/`，因此
Runtime 只按第一个 `/` 分割。`variant` 始终是独立字段，不能拼进 model ID。

输入没有 OpenCode `agent`，也没有 `kind` 或 `type`。`uses: mohist/opencode` 已经选择
Runtime。OpenCode 的默认 agent、tools、plugins、permissions 与自动压缩策略
继续由 OpenCode 原生配置负责。

`options` 中除 `model` 与 `variant` 之外的键被忽略并记入诊断，不使回合失败；这让
仍含遗留键（如 `type`、liveness 配置）的已持久化 `vars.agent` 可以继续绑定，直到
写入路径完成收敛。`model` 或 `variant` 存在但不是字符串时，返回 invalid input。

Action 不读取 Workflow variables。`with` 在 dispatch 前完成模板展开（语义与示例见
[`profile.md`](../workflow/profile.md)），展开后的 Action Input 是本次执行唯一的配置
事实。`variant` 可以和 `model` 一起提供，也可以单独提供；省略 `model` 时，OpenCode
把它应用到当前或默认 model。

创建新的物理 Session 时，把显式 model 传给 Session creation 和第一个 Prompt。复用
现有物理 Session 时，每个 Prompt 携带本次指定的 model 与 variant；成熟 Session API
会在创建 user message 时更新 Session 选择，不需要单独调用 switch。省略 options
时保留当前 Session 选择；首次没有选择时使用 OpenCode 默认值。改变 model
或 variant 不会轮换物理 Session。

Runner 的 Workflow task executor 在 `OpenCodeActionInput` 之外单独接收 `expect` 与
artifact 声明，并在 OpenCode Action 返回后应用它们。只有命中 promise 时才把对应值
作为 Action Output 暴露；`{ promise }` 由 task executor 依据 Workflow 拥有的 `expect`
合成，Action 与 Runtime 都不产生该字段。Runtime 身份、transcript、model、usage、
诊断信息与 expectation 明细保存在原有 state / read model 中，不塞进 Action output。

规范化回合事实包含回合的最终 assistant 文本。task executor 用它评估
`path: _output` 的 expect marker；该文本经由 Action result 的回合事实提供，
不经由 Action Output。

## SDK 调用面

OpenCode 1.17.18 同时导出成熟兼容 API `client.session.*` 和新协议
`client.v2.*`。OpenCode 自己的 Web UI 与 TUI 仍使用 `client.session.*` 执行关键 Session
操作。生成的 `client.v2.session.wait()` 与 `client.v2.session.compact()` 方法虽然存在，
当前 Server 实现仍会报告 `operation unavailable`。

因此 Mohist 使用以下调用：

| 能力 | SDK operation |
|---|---|
| 创建 Session | `client.session.create()` |
| 执行并等待 Workflow / AgentJob 回合 | `client.session.prompt()` |
| 提交用户 Follow-up 并立即返回 | `client.session.promptAsync()` |
| 中断执行 | `client.session.abort()` |
| 压缩 context | `client.session.summarize()` |
| 读取 Session 状态 | `client.session.get()`、`client.session.messages()`、`client.session.status()` |
| 接收实时事件 | `client.global.event()` |
| 读取 model catalog | `client.v2.model.list()`、`client.v2.provider.list()` |

依赖仍然是 `@opencode-ai/sdk/v2`。选择成熟 Session namespace 是隐藏在
`OpenCodeRuntime` 内部的实现决策，不构成另一套产品契约。在新 V2 Session 执行接口
足以替换上表之前，Mohist 不调用
`client.v2.session.prompt/wait/compact/interrupt`。

## 深模块边界

`OpenCodeRuntime` 是 Runner 内部的深模块，负责：

- OpenCode Server 与 Client 生命周期；
- 就绪状态与 model catalog；
- 物理 Session 创建、查询、复用与中断；
- Prompt 执行、Follow-up、Compact 与 Reset；
- event subscription、message snapshot 核对和事件规范化；
- OpenCode error 与兼容性诊断。

`mohist/opencode` Action、AgentJob execution adapter 与 Session command handler 只依赖
由 Mohist 定义的 request / result 类型，不暴露生成的 SDK 类型。Runtime 接收已经组装
好的回合输入与 Session 绑定；它不接收 Mohist Agent ID / name，也不加载 Mohist Agent
定义。Model string 解析、SDK DTO 构造、调用顺序、重连和 OpenCode error 解释
全部封装在该模块内。

它不是逐方法透传的 SDK wrapper。调用者请求 run turn、follow up、compact、reset 等
Mohist 能力，由模块决定使用哪些 SDK operation 和状态核对步骤才能完成该能力。

## 进程拓扑与就绪

每个 Runner 进程拥有一个 OpenCode Server 和一个 Client，由所有 OpenCode Session
共享。使用官方 `createOpencodeServer()` 与 `createOpencodeClient()` API，不直接 spawn
或解析 OpenCode 进程。每次 Session SDK 调用显式传递工作目录，并启用
`throwOnError` 让失败进入统一错误规范化；不为每个 Action 创建独立进程。

Mohist 认为物理 Session 的 directory 不可变。工作目录变化时创建新的物理 Session，
不移动现有 Session。

Runner 注册或领取工作前必须：

1. 启动共享 OpenCode Server；
2. 通过 OpenCode health check；
3. 成功加载 model catalog。

OpenCode Server 退出后，Runner 停止领取新工作，并重建 Server、Client 与全局事件
订阅。受影响的执行中回合直接失败，不能自动 replay。只有 health 与 catalog
检查重新通过后，Runner 才恢复 ready。

Mohist 固定 SDK package 版本，OpenCode CLI 由安装者提供。Mohist 不安装、升级或强制
CLI 精确匹配 SDK 版本；不兼容行为必须形成可操作的 readiness error。原生 workspace
配置和 plugins 正常加载，不使用 `--pure`，也不清理 `.opencode` lockfile。

## Session 绑定

AgentSession 所有权与来源见 [`agent-execution.md`](../agent-execution.md)，Runtime 身份
字段命名见 [`conventions.md`](../conventions.md)。`OpenCodeRuntime` 接收已经解析好的逻辑
Session 目标，不能创建或改变其来源。

由 Workflow 拥有的工作从 WorkflowRun 与 session name 解析目标；省略 name 时使用
Work ID。由 AgentJob 拥有的工作直接接收 dispatch 时创建的 AgentSession ID。

逻辑 AgentSession 尚无物理绑定时，adapter 先请求 `OpenCodeRuntime` 创建物理 Session，
把返回的 Session ID 持久化为当前绑定，再用该绑定执行首个 Prompt。持久化失败时不得
提交 Prompt；否则一次已执行但未绑定的回合会让后续 task 无法证明应复用哪个上下文。
重复持久化同一个 Session ID 必须幂等，不得形成新的 lineage。

物理 Session 的复用只由逻辑 AgentSession 的当前绑定、Runtime 和工作目录决定。同一
WorkflowRun 中的同名 session 跨 task、retry 和 Runner 重启都必须解析到当前绑定。
Runtime 变化、工作目录变化与 Reset 会创建新物理绑定并追加 lineage，不迁移上下文；
Compact 与 model / variant 变化必须保持同一物理 Session ID。

Model 与 variant 是回合执行参数，不能进入 Session cache key，不能作为是否调用
`resumeSession` 的门槛，也不能触发 attach replacement 或追加 lineage。复用已有 Session
时，Runtime 在原物理 Session 上应用本次 model / variant 后执行 Prompt。持久绑定存在但
Runtime 无法恢复该物理 Session 时，本次工作失败并提示 Reset；不得隐式调用 create
伪造连续上下文。

worktree cleanup follow-up 是原 task 的后续回合。executor 必须再次调用原 task 已解析的
Action，并保留相同 WorkflowRun、session name、Work ID 与工作目录，让它走同一 Runtime
和物理 Session；不得把 cleanup 硬编码到另一种 Action 或 ACP fallback。cleanup 不是
Reset，也不能以 housekeeping 为理由替换绑定。

每个逻辑 AgentSession 同时最多运行一个由工作发起的 Prompt，无论工作所有者是 TaskRun
还是 AgentJob。不同逻辑 Session 可以并行。用户 Follow-up 是 Session 命令，可以在
工作回合执行期间被接收。

## 回合执行

Workflow Action adapter 或 AgentJob executor 请求的回合按以下顺序执行：

1. 通过 `client.session.create()` 解析或创建当前物理 Session；
2. 解析可选 model string，并在 Runtime 内构造 SDK model DTO；
3. 调用并等待 `client.session.prompt()`，传入 Session ID、directory、prompt parts、
   可选 model 与可选 variant；
4. 把返回的 assistant message 和收到的 events 投影到 AgentSession；
5. 需要确认最终 transcript snapshot 时，读取 `client.session.messages()` 核对；
6. 向调用者返回规范化完成事实。

`client.session.prompt()` 本身就是携带完成结果的请求，不存在第二次 `wait()`。
`OpenCodeRuntime` 不执行 Workflow expectations，也不判断 AgentJob 成功。Workflow task
executor 在 Action 返回后应用 `expect`、artifacts、`failIf`、Action Output 与 recovery
语义；AgentJob executor 通过由 Agent 拥有的契约校验和报告自己的结果。

SSE 沉默不表示失败，`idle` event 也不是完成权威。等待完成的 Prompt 响应决定回合
是否结束。工作回合的执行期限由 Workflow task executor 与 AgentJob executor 各自
声明：未显式指定时，单个 Prompt 的默认期限为 60 分钟，显式期限可以覆盖该默认值。
期限的收尾与终止按「回合期限与两段式收尾」执行。移除 ACP liveness probe
后，`OpenCodeRuntime` 不做静默/空闲检测；悬挂回合由 executor 期限兜底，而 provider 错误可在到达期限前按 `session.status` retry 事实提前失败（见「Provider 错误失败策略」）。

Runner 生命周期内可以 retry startup 与 readiness 操作。Prompt submission 以及任何
接收状态不确定的响应都不能盲目 retry。保留现有 in-process dispatch deduplication；
redelivery 在 crash window 内可能造成重复回合，这是已接受限制，不增加 deterministic
Prompt ID 或 replay reconstruction。

## 回合期限与两段式收尾

期限值由 executor 声明，`OpenCodeRuntime` 对每个声明了期限的 Prompt 执行两段式
收尾协议。时钟粒度是单次 Prompt 执行，不是 TaskRun 或 Stage。

1. 期限前 5 分钟，对当前物理 Session 调用 `client.session.promptAsync()` 注入一条
   收尾警告后立即返回，不等待其完成。期限不足 5 分钟时，警告在回合开始即注入。
2. 期限到达时调用 `client.session.abort()` 终止回合，向调用者返回 interrupted
   result。

警告文案任务无关，大意固定、措辞由实现维护：你将在约 5 分钟后被中断——立即停止
新工作，提交当前改动，在本任务的进度渠道留下记录，然后结束。警告不引用具体
marker 或文件名；`unfinished`、progress.txt 等收尾契约由各任务自己的 prompt
定义，警告不复述。

注入的消息作为 user Follow-up 写入 Session 消息流，由运行中的回合在迭代边界
（当前模型调用及其工具调用完成后）拾取处理——这与用户 Follow-up 的接收路径相同
（见「Session 命令 / Follow-up」）。正在执行的长工具调用会延迟拾取；期限到达仍
abort，最坏情况退化为无警告的直接终止。警告与终止都投影进 transcript，在 UI
可见。

每个 Prompt 执行只警告一次。agent 被警告后提前正常结束回合的，不再 abort；其
结果按各任务自己的完成契约评估（如报 `unfinished` 则任务失败、按现有 retry
语义处理），但现场是已提交、有记录的。

不做的事：

- 不把期限值暴露给 prompt：agent 没有可靠时钟，静态数字不可执行；可执行的
  「即将终止」信号由警告在需要时送达。
- 不在终止后自动提交或回滚残留现场；现场处理维持现状。
- 不为 housekeeping prompt（如 worktree cleanup follow-up）引入「回合角色」
  概念：警告文案与其指令（提交或还原）语义相容，统一适用。

## 事件与状态核对

Runner 为共享 OpenCode Server 维护一个 `client.global.event()` 订阅。`OpenCodeRuntime`
按 Session ID 与 directory 路由事件。已知 typed event 被规范化为 Mohist 稳定的
transcript、tool、usage、model、status 与 compaction 事实；未知 OpenCode event 只进入
诊断信息，不改变 Workflow 或 Session 状态。

实时 event 只优化展示延迟，不作为持久化执行协议：

- 使用 OpenCode message ID 与 part ID 保证投影幂等；
- event stream 在仍有订阅者时断开，订阅层重新建立唯一的 global event stream；
- 新 stream 连接后，运行中回合按自己的 Session ID 与 directory 读取
  `session.status()`，并与相关 `session.get/messages()` snapshot 核对；
- 一个回合只消费属于自己 Session ID 的 retry 事实，其他 Session 的事件不能改变其
  provider 错误判定；
- Prompt 完成后，如 event 缺失或需要确认最终用户可见 transcript，再核对 messages。

Mohist 不保存 V2 history cursor、aggregate sequence 或 event replay state。Workflow
task executor 根据 Action result，再应用 Mohist expectation、artifact、`failIf` 与
recovery 语义判断 Workflow 成功；AgentJob 是否完成由其 executor 独立判断。

## Provider 错误失败策略

provider 错误仅当判为不可恢复时让回合失败；可恢复错误（瞬时 429、5xx、网络抖动）交
OpenCode 重试，Mohist 不主动失败。失败信号来自 `session.status` 事件（`type:"retry"`，
携带 `attempt`、`message`、`action`、`next`）、重连后的 status snapshot 与回合最终的
prompt reject，不扫描日志。两类不可恢复判定都归一到 abort 回合并失败：

- 按性质不可恢复：优先使用 retry status 的结构化 `action.reason`；没有可用分类时，
  `message` 命中 quota、credit、billing、usage limit、额度、余额、使用上限或重置限额等
  模式即 abort+失败。普通 rate limit / too many requests 不因文案兜底在首次出现时失败。
  默认模式集覆盖常见 provider 的中英文额度措辞，runner 级可配置追加。
- 按证据不可恢复：可恢复错误连续重试，`attempt` 达到阈值 N（默认 5，runner 级可配置）
  而回合仍未完成，重新判为不可恢复，abort+失败。

可恢复错误在 N 次内恢复（回合完成）则继续，不失败。OpenCode 自身已判不可恢复的错误
（auth、invalid-request、context-overflow、content-policy）由 OpenCode 直接 reject
prompt，Mohist 不额外处理。连 retry 事件都不产生的静默卡死仍由 executor 期限兜底。

计数直接用 retry 事件的 `attempt` 字段（OpenCode 维护、每回合重置）；runner 重启或
event stream 重连后用 `session.status()` snapshot 恢复，不另建状态。命中或超阈值时，
Runtime 使用当前锁定 SDK 的类型化调用面执行
`client.session.abort({ sessionID, directory }, { throwOnError: true })`。只有 abort 返回
`data: true`，且同一 directory 的 status snapshot 中该 Session 不存在或为 idle，才算
确认停止；随后向调用者返回带原始 provider message 的失败事实。AgentSession 与物理
Session 绑定保持不变，不提示 Reset。

abort 请求失败、返回值不确认成功，或 status 仍为 busy/retry 时，Runtime 返回
`abort-unconfirmed` 诊断，不声称回合已经停止。OpenCode 是第三方依赖；Mohist 不修改其
重试实现，因此结构化分类不足时长期保留 message 兜底与 Mohist 自己的重试上限。

## Session 命令

Session command 是从 Web 或 CLI 经 Server 到 Runner 的请求 / 响应操作。持久化的
Runtime 绑定是路由事实，Runner 内存 cache 只是优化。

命令结果必须区分「确定没有开始」与「可能已经开始但结果未知」。Server 未找到目标
Runner 连接、Runner 尚未取得 Runtime connection，或命令在进入 Runtime 前被拒绝时，
返回 `notStarted`；Server 可以结束这次 reservation，让后续请求创建新 operation。
一旦 Runtime 调用可能已经开始，timeout、连接丢失和无法确认的 Runtime reply 都返回
`unavailable`；Server 必须保留原 operation，后续投递继续使用同一 operation id，不能
通过放弃 reservation 来猜测副作用没有发生。

### Follow-up

- 对当前物理 Session 调用 `client.session.promptAsync()`，传入 prompt 和可选的当前
  model / variant 选择。
- Endpoint 接收请求后立即返回；完成过程继续通过 Session events 呈现。
- Session active 时接收的 Follow-up 加入当前 OpenCode execution；Session idle 时立即
  开始处理。
- Routing 或 admission 失败必须返回给用户，不能自动 retry 或 replay。

### Compact

只有逻辑 Session idle 时才允许 Compact；Session 有执行中的工作回合时返回 conflict，
与 Reset 使用同一并发边界。先从 OpenCode Session 读取当前 model，再调用
`client.session.summarize({ sessionID, providerID, modelID })`。Compact 不创建新的物理
Session，也没有 Mohist 侧的 synthetic summary fallback。Session 没有当前 model 时返回
可操作错误，不能猜测。产生的 Session 与 message events 继续核对进 transcript。

### Reset

只有逻辑 Session idle 时才允许 Reset。先读取当前 model / variant（如果存在），再在同一
工作目录创建新的空 OpenCode Session。创建成功后才替换逻辑 Session 绑定，并把新物理
绑定追加到 lineage。旧 Session 保留查询和审计能力，但其上下文不进入新
Session。

每个命令携带 expected current binding。Server 只在该绑定仍是 current 时应用返回的
replacement，防止过期 Reset result 覆盖更新的绑定。OpenCode Session 缺失时明确报错，
并提示 Reset；不能隐式创建 replacement。

Compact 与 Reset 都不轮换 AgentSession ID：命令响应返回同一稳定 `sessionId`，只有
Reset 替换 Runtime 绑定。API 响应形状与 CLI 文案不得再表述为"返回新 session id"。

## 权限与错误

OpenCode 原生 permission 配置是权威。Mohist 不自动批准请求，也不把 OpenCode
permission prompt 转成 Workflow Approval。Headless turn 出现无法完成的交互式
permission request 时，abort 当前回合并返回可操作错误。

在 `OpenCodeRuntime` 边界把 SDK error 规范化为少量 Mohist result：`invalid input`、
`unavailable runtime`、`missing Session`、`incompatible runtime`、
`permission required`、`interrupted` 与 `turn failed`。Provider-specific detail 只作为
诊断信息，不成为 Action output 字段。不要建立全局 Workflow error enum；各调用者通过
自己的 TaskRun 或 AgentJob 契约报告失败。

## 模型目录

通过 `client.v2.model.list()` 与 `client.v2.provider.list()` 加载结构化 model / provider
catalog；OpenCode TUI 也使用这组 read-only API。Runner registration 报告 model /
variant catalog，Server 与 Web 将它用于配置辅助，但它不是最终权威。省略 model 时使用
当前 OpenCode 选择或默认值；选定 model 是否有效仍由 OpenCode 最终校验。

## 测试

默认测试不能启动真实 OpenCode，也不能使用真实 process、network、filesystem config
或 clock。注入 fake `OpenCodeRuntime` 或 fake generated Client / Server factory，确定性
驱动事件、snapshot、完成状态、process loss 与 error。

覆盖至少包括：

- Action Input expansion，并确认不存在隐藏 `vars.agent` fallback；
- model string 内含多层 `/`，variant 保持独立；
- Workflow 与 AgentJob 拥有的回合共享 Runtime code，但不共享工作 / Session 身份；
- 物理 Session reuse 与 rotation 不变量；
- model / variant 变化不触发 rotation；
- 全局 event routing、duplicate suppression 与 snapshot reconciliation；
- Prompt completion、interruption、uncertain admission 与 no-replay 行为；
- async Follow-up、原生 summarize、Reset、restart routing 与 stale-binding rejection；
- permission、missing Session、compatibility 与 process-loss failure；
- 最小 `{ promise }` Workflow Action Output 与现有 expectation 语义；
- 两段式收尾：期限前警告注入（仅一次、fire-and-forget）、期限不足 5 分钟时回合
  开始即警告、期限到达 abort、被警告后提前结束不再 abort；全部以 fake clock 驱动。

## 完整替换

实现改动直接移除，而不是保留 deprecated 路径：

- `@agentclientprotocol/sdk`；
- `mohist/acp-agent` 与 ACP Action tree；
- 共享 ACP connection / session management；
- ACP liveness probes 及其配置；
- OpenCode log scanning 与 CLI model parsing；
- ACP private compaction metadata 与 synthetic Session rebinding；
- `.opencode` lockfile cleanup；
- 所有 `acpSessionId` wire、Server 与 Web 术语。

内置 Workflow 原子切换为 `mohist/opencode` 与 `options: ${{ vars.agent }}`。现有
AgentJob dispatch 移除硬编码的 `mohist/acp-agent` Action name；Agent launch 组装 Agent
snapshot 与 prompt 后，携带由 Agent 拥有的 OpenCode execution request，由 executor
直接调用 `OpenCodeRuntime`。这不会引入 `mohist/agent`，也不提供 feature flag、
compatibility alias 或 ACP fallback。

### 存量数据与配置的过渡行为

不做存量数据重写，过渡行为必须明确而不是静默：

- 已持久化的 AgentSession（含 `acpSessionId`、历史 Compact / Reset 轮换出的旧
  Session 记录）保持可查询与可审计；ACP 时代的 Runtime 绑定在替换后视为
  "当前 Runtime Session 不存在"，Session 操作明确失败并提示 Reset。
- 旧结构 Workflow Profile 不被静默忽略，也不自动改写：`uses: mohist/acp-agent` 的任务
  在 dispatch 时以可操作错误失败——该 Action 已移除。`with.expect`、`with.agent` 等旧
  输入键归 Action 契约处理，definition 校验不检查 `with` 内部。
- 切换前已开始的 WorkflowRun 不自动迁移；其后续 agent task dispatch 以可操作错误
  失败，由用户 rerun 受影响 stage。
- issue 级 `agentConfig` 配置面收敛为 model / variant（`type` 与 ACP liveness 字段从
  API / CLI / Web 移除）；已持久化 `vars.agent` 中的遗留键由 Action Input 的
  忽略 + 诊断规则兜底。

## 上游边界

决策时使用的依赖是 `@opencode-ai/sdk/v2` 1.17.18，但其中两个 namespace 的成熟度不同。
OpenCode Web UI 与 TUI 使用 `client.session.*` 完成 create、Prompt、abort、summarize 与
Session synchronization；TUI 正在逐步使用 `client.v2.*` 读取 catalog 与其他数据。新的
V2 Session execution core 仍把 `wait` 和 `compact` 报告为 unavailable，完成与恢复能力
也尚未完整。

Mohist 跟随这些真实内部调用路径，而不是假设每个生成的 V2 方法都可用。SDK access
封装在 `OpenCodeRuntime` 内；以后迁移到完整 V2 Session 执行接口时，只改变
一个深模块，不改变 Workflow Action 或 Session 产品契约。

实现开始时必须先锁定 SDK package 版本，并对上表断言的调用面在真实 OpenCode 上做
一次冒烟验证；发现漂移时先修订本表，再进入实现。T-001 已在真实 OpenCode 1.18.3
服务器上对上表每个调用做了一次冒烟验证（详见
[`openspec/changes/issue-409/sdk-smoke-verification.json`](../../openspec/changes/issue-409/sdk-smoke-verification.json)）：
表内 `client.session.*`、`client.global.event()`、`client.v2.model.list()`、
`client.v2.provider.list()` 全部可用；`client.v2.session.wait()` 与
`client.v2.session.compact()` 仍返回 `ServiceUnavailableError`，确认不进入执行链。
实际锁定的 SDK 版本见实装差距小节。

## 实装差距

issue-407 已落地 OpenCode 替换所需的稳定身份与命令契约：Compact 保持当前 Runtime
绑定；Reset 只在 Session 空闲且 expected binding 仍为 current 时应用 replacement；
两者的 API 与 CLI 响应都保持同一稳定 `sessionId`。Canonical wire 使用 `runtime` +
`runtimeSessionId`，当前 Runtime Session 缺失时命令明确失败并提示 Reset。

issue-409 已为 Workflow 来源落地 `OpenCodeRuntime` 深模块并完成 Native OpenCode
替换：Workflow 来源的回合执行改为 `client.session.create/prompt/abort`，
Workflow schema 把 `expect` 抬到 task 顶层（#408），内置 profile 全部切换到
`mohist/opencode`；Workflow 来源的配置、Session state、命令请求 / 结果与用户可见
诊断不再暴露 `acpSessionId` 或 ACP Action 身份，wire 字段统一使用 `runtimeSessionId`
而非历史 ACP 字段名。Runner 为 AgentJob 路径继续保留 `mohist/acp-agent` 注册，
其 ACP 痕迹、依赖与配置面的最终清理由 issue-410 负责；本 issue 不为 Workflow
来源引入 feature flag、compatibility alias 或 ACP fallback。

「回合期限与两段式收尾」在 `OpenCodeRuntime` 落地后由独立 issue 跟进；当前期限
到达直接终止回合，agent 没有收尾机会。

T-001 完成时实际锁定的 SDK 版本是 `@opencode-ai/sdk@1.18.3`（与安装在 PATH 上的
`opencode` CLI 版本一致），不是 1.17.18。决策文本保留 1.17.18 作为该节撰写时点的
参考版本；后续 T-002+ 实现时按 1.18.3 进行。冒烟记录在
[`openspec/changes/issue-409/sdk-smoke-verification.json`](../../openspec/changes/issue-409/sdk-smoke-verification.json)。

issue-409 范围之外、当前仍未实装：

- AgentJob 执行路径仍使用 `mohist/acp-agent` Action 与 ACP bridge；
  最终迁移与 ACP 依赖移除由 issue-410 完成。
- Workflow 来源的 Session 命令（Follow-up / Compact / Reset / Cancel）
  的 Runtime 替换按 T-005 推进；T-005 落地后该路径同样不再暴露 ACP 身份。
