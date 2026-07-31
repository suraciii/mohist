# OpenCode Runtime

## 决策

`mohist/opencode` 是直接基于 `@opencode-ai/sdk/v2` 实现的 Runtime 特有 Action。它与
Agent / Session 的所有权模型（Inline Agent、工作所有者、共享 Runtime 不制造依赖等
不变量）见 [`agent-execution.md`](../agent-execution.md)。

ACP adapter 直接移除，不保留 fallback。现有 AgentJob 执行也必须离开 ACP，通过
由 Agent 拥有的 executor 使用同一个 `OpenCodeRuntime` 能力，而不是依赖 Workflow Action
契约。本设计不定义 `mohist/agent` Action，也不重新设计 Mohist Agent 产品。

OpenCode 与 Pi 各自实现独立的 Runtime 深模块，不引入通用 `AgentRuntime` 接口。稳定
边界是 Workflow Action 契约、AgentJob 执行契约和 Session 命令，不是假想的跨 Runtime
SDK wrapper。

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

Runner 在调用 Action 前的执行入口把 `prompt` 渲染为非空字符串后再交给 OpenCode Runtime
处理；本节只描述 Action 接收的输入形状与 Runtime 行为，不承担渲染权威（见
[`task-dispatch.md`](../workflow/task-dispatch.md)）。`model` 使用
OpenCode 的 `providerID/modelID` 形式。OpenCode model ID 自身可能继续包含 `/`，因此
Runtime 只按第一个 `/` 分割。`variant` 始终是独立字段，不能拼进 model ID。

输入没有 OpenCode `agent`，也没有 `kind` 或 `type`。`uses: mohist/opencode` 已经选择
Runtime。OpenCode 的默认 agent、tools、plugins、permissions 与自动压缩策略
继续由 OpenCode 原生配置负责。

`options` 中除 `model` 与 `variant` 之外的键被忽略并记入诊断，不使执行失败；这让
仍含遗留键（如 `type`、liveness 配置）的已持久化 `vars.agent` 可以继续绑定，直到
写入路径完成收敛。`model` 或 `variant` 存在但不是字符串时，返回 invalid input。

Action 不读取 Workflow variables。模板求值时机由 [`task-dispatch.md`](../workflow/task-dispatch.md)
统一规定：Server dispatch 不再展开 `with` / `expect`，Runner 在调用 Action 前的执行入口
按 attempt 快照渲染原始 `with`，再交给 manifest 校验和 Action 输入。本节只描述 OpenCode
Action 接收的输入形状与 Runtime 行为，不承担渲染权威。

`variant` 可以和 `model` 一起提供，也可以单独提供；省略 `model` 时，OpenCode
把它应用到当前或默认 model。

创建新的物理 Session 时，把显式 model 传给 Session creation 和第一个 Prompt。复用
现有物理 Session 时，每个 Prompt 携带本次指定的 model 与 variant；成熟 Session API
会在创建 user message 时更新 Session 选择，不需要单独调用 switch。省略 options
时保留当前 Session 选择；首次没有选择时使用 OpenCode 默认值。改变 model
或 variant 不会轮换物理 Session。

Runner 的 Workflow task executor 在 attempt 快照上渲染 `with`，把渲染并 manifest 校验
后的结果作为 `OpenCodeActionInput` 交给 Action；`expect` 与 artifact 声明独立于 Action
输入，由 executor 在 Action 返回后应用。Action 与 Runtime 都不读取 Workflow Variables
或完整 dispatch context。只有命中 promise 时才把对应值作为 Action Output 暴露；`{ promise }`
由 task executor 依据 Workflow 拥有的 `expect` 合成，Action 与 Runtime 都不产生该字段。
Runtime 身份、transcript、model、usage、诊断信息与 expectation 明细保存在原有 state /
read model 中，不塞进 Action output。

规范化执行事实包含最终 assistant 文本。task executor 用它评估
`path: _output` 的 expect marker；该文本经由 Action result 的执行事实提供，
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
| 执行并等待 Workflow / AgentJob Prompt | `client.session.prompt()` |
| 提交用户 Follow-up 并立即返回 | `client.session.promptAsync()` |
| 中断执行 | `client.session.abort()` |
| 压缩 context | `client.session.summarize()` |
| 读取 Session 状态 | `client.session.get()`、`client.session.messages()`、`client.session.status()` |
| 接收实时事件 | `client.global.event()` |
| 回应一次性权限 | `client.permission.reply()` |
| 释放 directory Instance | `client.instance.dispose()` |

依赖仍然是 `@opencode-ai/sdk/v2`。选择成熟 Session namespace 是隐藏在
`OpenCodeRuntime` 内部的实现决策，不构成另一套产品契约。在新 V2 Session 执行接口
足以替换上表之前，Mohist 不调用
`client.v2.session.prompt/wait/compact/interrupt`。

## 深模块边界

`OpenCodeRuntime` 是 Runner 内部的深模块，负责：

- OpenCode Server 与 Client 生命周期；
- 就绪状态；
- directory Instance 的使用跟踪、空闲判定与释放；
- 物理 Session 创建、查询、复用与中断；
- Prompt 执行、Follow-up、Compact 与 Reset；
- event subscription、message snapshot 核对和事件规范化；
- OpenCode error 与兼容性诊断。

`mohist/opencode` Action、AgentJob execution adapter 与 Session command handler 只依赖
由 Mohist 定义的 request / result 类型，不暴露生成的 SDK 类型。Runtime 接收已经组装
好的执行输入与 Session 绑定；它不接收 Mohist Agent ID / name，也不加载 Mohist Agent
定义。Model string 解析、SDK DTO 构造、调用顺序、重连和 OpenCode error 解释
全部封装在该模块内。

它不是逐方法透传的 SDK wrapper。调用者请求 execute prompt、follow up、compact、reset 等
Mohist 能力，由模块决定使用哪些 SDK operation 和状态核对步骤才能完成该能力。

## 进程拓扑与就绪

每个 Runner 进程拥有一个 OpenCode Server 和一个 Client，由所有 OpenCode Session
共享。OpenCode Server 在同一进程内按 resolved directory 缓存多个 Instance；Instance
持有该目录的配置、plugin、LSP、MCP 等运行资源。它不是 Runner 的 Git workspace，也不是
AgentSession 或物理 Session。

使用官方 `createOpencodeServer()` 与 `createOpencodeClient()` API，不直接 spawn 或解析
OpenCode 进程。每次 Session SDK 调用显式传递工作目录，并启用 `throwOnError` 让失败进入
统一错误规范化；不为每个 Action 创建独立进程。

Mohist 认为物理 Session 的 directory 不可变。工作目录变化时创建新的物理 Session，
不移动现有 Session。

Runner 注册或领取工作前必须：

1. 启动共享 OpenCode Server；
2. 通过 OpenCode health check；
3. 建立全局 event subscription。

OpenCode Server 退出后，Runner 停止领取新工作，并重建 Server、Client 与全局事件
订阅。受影响的执行直接失败，不能自动 replay。替换 Server 重新通过 health
并建立事件订阅后，Runner 即恢复 ready，不等待模型发现。

Mohist 固定 SDK package 版本，OpenCode CLI 由安装者提供。Mohist 不安装、升级或强制
CLI 精确匹配 SDK 版本；Server / SDK 不兼容必须形成可操作的 readiness error，CLI
模型发现不兼容只记录诊断并保留 best-effort 语义。原生 workspace 配置和 plugins
正常加载，不使用 `--pure`，也不清理 `.opencode` lockfile。若 plugin 持有的资源导致
CLI 在截止时间前未退出，但 stdout 已包含可解析的非空目录，该结果只能标记为不完整
快照并记录诊断，不能伪装成一次正常完成的发现。

## Directory Instance 回收

共享 Server 不以 WorkflowRun 终态自动释放 directory Instance。Runner 因此在现有
workspace 周期维护中回收已终结 WorkflowRun 的 OpenCode Instance。此回收是执行平面的
资源治理，不是 Workflow 状态变化，也不删除磁盘 workspace。

### 候选与成本

一次 OpenCode Server generation 指共享 Server 从启动成功到退出或关闭的生命周期。
`OpenCodeRuntime` 按当前 generation 跟踪自己实际访问过的 resolved directory。任何带
directory 的 SDK 操作在进入 OpenCode 前都把该目录记为已使用。一次
成功的 `client.instance.dispose({ directory })` 清除该 generation 的使用记录；之后同一
目录再次收到 Runtime 请求时重新记入。Server 退出、关闭或完成重建时，旧 generation
的全部记录一起清空，因为旧进程中的 Instance 已经不存在。使用记录只属于 Runner 进程
内存，不写入 `WorkspaceRegistry`；Runner 重启会同时失去旧 Server 进程和对应记录，无需恢复。

周期维护只遍历这个“当前 generation 已使用且尚未成功释放”的集合，并按 resolved path
在 [`WorkspaceRegistry`](../runner.md#本地-workspace-生命周期) 做身份查询。只有 phase 为
`eligible` 或 `stuck` 的 workflow
workspace 才是回收候选。`active`、未注册目录、普通 AgentJob 目录和 Runtime 启动目录都
不回收。

不能每轮扫描全部历史 WorkflowRun 或全部 `eligible` 注册表条目，也不能对当前 generation
未使用的目录调用 dispose。OpenCode 会按 directory 建立 Instance；盲目探测或重复 dispose
可能为了“清理”反而创建待清理的 Instance。一次成功回收后，该目录不再产生周期成本；
后来确有新请求时才重新进入集合。

### 空闲与并发

WorkflowRun 终态只提供回收资格，不证明 OpenCode 已空闲。`Stopped` 不隐含 Runner 已经
中断旧工作，成功返回的 async Follow-up 也可能仍在 OpenCode 内执行。

Runtime 必须把同一 directory 的 SDK 操作 admission 与 Instance dispose 串行化。回收在
该 directory 的独占边界内按以下顺序执行：

```text
if directory has an admitted local operation:
  defer

statuses = client.session.status({ directory })
if statuses is missing, malformed, or contains busy / retry / unknown:
  defer

disposed = client.instance.dispose({ directory })
if disposed is not exactly true:
  defer

forget directory for this Server generation
```

状态 map 为空或只包含 `idle` 才允许 dispose。独占边界保持到 dispose 响应确认；新的
Prompt、Follow-up、Cancel、Compact、Reset 或 Session 查询只能在它结束后进入。新请求
进入时会重新记录该 directory，因此 dispose 不会永久禁止后续使用。

### Session 与删除边界

Instance dispose 只释放该 directory 的进程内资源。它不删除 OpenCode Session、
AgentSession、current binding、transcript 或磁盘 workspace，也不能据此把 Session activity
改成 `idle` 或 closed。后续请求仍使用持久 binding；OpenCode 重新建立 directory Instance
后，按既有 Session resolve 与 missing recovery 规则继续。

同一轮 Runner workspace 维护必须先尝试 Instance 回收，再应用磁盘 retention / budget。
当前 Server generation 已使用过的终态目录，在 dispose 尚未成功、状态仍忙或状态未知时，
不得自动删除 workspace 或移除其注册表条目。手动 workspace cleanup 使用同一释放能力和
顺序。没有当前 generation 使用记录表示该 OpenCode 进程没有需要为此目录释放的已知
Instance，不应为了确认而调用 dispose。

### 失败与范围

状态读取或 dispose 失败时保留使用记录，由后续周期重试。单目录回收失败不改变
WorkflowRun、TaskRun 或 AgentSession 结果，也不调用 `/global/dispose`，不打断其它目录。
transport failure 仍按共享 Server 的既有规则触发 Runtime rebuild；旧 Server generation
结束后，对应 Instance 与使用记录一起消失。

回收 pass 必须 single-flight；上一轮未结束时不重叠启动下一轮。每轮只记录有界的候选数、
busy / failed / disposed 数量和聚合诊断，避免一个持续失败的目录制造无界日志。

本设计不承诺 `instance.dispose` 后进程 RSS 立即归还给操作系统，也不新增按目录空闲时长、
mtime 或 Workflow 历史推断终态的 TTL。若 per-directory dispose 后共享 Server 仍持续增长，
进程级 idle recycle 是独立的后续保护，不用 `/global/dispose` 冒充。

## Session 绑定

AgentSession 所有权与来源见 [`agent-execution.md`](../agent-execution.md)，Runtime 身份
字段命名见 [`conventions.md`](../conventions.md)。`OpenCodeRuntime` 接收已经解析好的逻辑
Session 目标，不能创建或改变其来源。逻辑 Session 目标解析、绑定创建时序（先创建物理
Session、持久化绑定成功后才提交首个 Prompt；持久化幂等）、复用不变量（跨 task、retry
与 Runner 重启解析到当前绑定；工作目录不同则在提交 Prompt 前以可操作错误拒绝）以及
缺失恢复的 expected binding 裁决与操作矩阵以
[`agent-execution.md`](../agent-execution.md#runtime-session-缺失恢复) 为唯一权威，本节
只定义 OpenCode 特有部分。

提交新的独立输入前，只有 current binding 的 `runnerId` 对应的 `OpenCodeRuntime` 可以用
`client.session.get()` 核对持久绑定。请求落在其它 Runner 时必须先路由回绑定所属 Runner
或明确失败，其本地 404 不构成该 binding 的 missing 证据。只有绑定所属 Runner 上的
OpenCode 返回结构化 Session-not-found / HTTP 404，才产生 `definitely-missing` 事实。
网络失败、超时、认证或权限失败、5xx，以及成功响应缺少预期 ID 都不能归类为 missing；
缺少 ID 是 SDK / Server 不兼容证据，必须失败，不能创建 replacement。

收到 `definitely-missing` 后，Runtime 在同一 directory 调用 `client.session.create()`。
创建时使用本次输入已解析的 model；本次没有显式 model 时使用 OpenCode 默认值，variant
仍在 Prompt 上应用。新 Session 立即再次 missing、创建失败或 binding 被并发改变时，
本次执行失败，不做第二轮 create。

Model 与 variant 是执行参数，不能进入 Session cache key，不能作为是否调用
`resumeSession` 的门槛，也不能触发 binding replacement。复用已有 Session 时，Runtime
在原物理 Session 上应用本次 model / variant 后执行 Prompt。

worktree cleanup follow-up 是原 task 的后续执行。executor 必须再次调用原 task 已解析的
Action，并保留相同 WorkflowRun、session name、Work ID 与工作目录，让它走同一 Runtime
和物理 Session；不得把 cleanup 硬编码到另一种 Action 或 ACP fallback。cleanup 不是
Reset，也不能以 housekeeping 为理由替换绑定。

每个逻辑 AgentSession 同时最多运行一个由工作发起的 Prompt，无论工作所有者是 TaskRun
还是 AgentJob。不同逻辑 Session 可以并行。用户 Follow-up 是 Session 命令，可以在
工作执行期间被接收。

## Prompt 执行

Workflow Action adapter 或 AgentJob executor 请求的 Prompt 按以下顺序执行：

1. 解析可选 model string，并在 Runtime 内构造 SDK model DTO；
2. 无 binding 时创建物理 Session；有 binding 时用 `client.session.get()` 核对，并按
   通用缺失恢复规则选择原 ID 或一个已重新绑定的新 ID；
3. 等待 Session 确认当前 binding 已持久化；
4. 以确认后的 Runtime Session ID 记录并持久化本次输入；
5. 调用并等待 `client.session.prompt()`，传入 Session ID、directory、prompt parts、
   可选 model 与可选 variant；
6. 把返回的 assistant message 和收到的 events 投影到 AgentSession；
7. 需要确认最终 transcript snapshot 时，读取 `client.session.messages()` 核对；
8. 向调用者返回规范化完成事实。

`client.session.prompt()` 本身就是携带完成结果的请求，不存在第二次 `wait()`。
`OpenCodeRuntime` 不执行 Workflow expectations，也不判断 AgentJob 成功。Workflow task
executor 只在 Action 成功后应用 `expect`、artifacts、`failIf`、Action Output 与 recovery
语义；Action 失败、取消或超时时保留原始失败，不读取文件或 marker。AgentJob executor
通过由 Agent 拥有的契约校验和报告自己的结果。

SSE 沉默不表示失败，`idle` event 也不是完成权威。等待完成的 Prompt 响应决定执行
是否结束。工作执行的期限由 Workflow task executor 与 AgentJob executor 各自
声明：未显式指定时，单个 Prompt 的默认期限为 60 分钟，显式期限可以覆盖该默认值。
期限的收尾与终止按「Prompt 期限与两段式收尾」执行。移除 ACP liveness probe
后，`OpenCodeRuntime` 不做静默/空闲检测；悬挂执行由 executor 期限兜底，而 provider 错误可在到达期限前按 `session.status` retry 事实提前失败（见「Provider 错误失败策略」）。

`prompt()` 到本机 OpenCode Server 的 HTTP client 不得另设比 executor 更短的 header 或
body timeout；executor 的 AbortSignal 是单次执行期限权威。该设置只属于 OpenCode Client，
不得改变 Runner 其它 HTTP 调用的全局 dispatcher。任何 transport failure 都必须先请求
abort 并确认当前物理 Session 已停止，随后才向调用者报告失败；不自动重放提交状态不确定
的 Prompt。

Runner 生命周期内可以 retry startup 与 readiness 操作。Prompt submission 以及任何
接收状态不确定的响应都不能盲目 retry。保留现有 in-process dispatch deduplication；
redelivery 在 crash window 内可能造成重复执行，这是已接受限制，不增加 deterministic
Prompt ID 或 replay reconstruction。

## Prompt 期限与两段式收尾

期限值由 executor 声明，`OpenCodeRuntime` 对每个声明了期限的 Prompt 执行两段式
收尾协议。时钟粒度是单次 Prompt 执行，不是 TaskRun 或 Stage。

1. 期限前 5 分钟，对当前物理 Session 调用 `client.session.promptAsync()` 注入一条
   收尾警告后立即返回，不等待其完成。期限不足 5 分钟时，警告在执行开始即注入。
2. 期限到达时 runner 立即将执行结果固定为 `deadline-exceeded`，随后调用
   `client.session.abort()` 收尾。abort 与状态核对只能补充诊断，不能改变 timeout 主结果；
   迟到的 Prompt 响应也不能翻转该结果。

警告文案任务无关，大意固定、措辞由实现维护：你将在约 5 分钟后被中断——立即停止
新工作，提交当前改动，在本任务的进度渠道留下记录，然后结束。警告不引用具体
marker 或文件名；`unfinished`、progress.txt 等收尾契约由各任务自己的 prompt
定义，警告不复述。

注入的消息作为 user Follow-up 写入 Session 消息流，由当前执行在迭代边界
（当前模型调用及其工具调用完成后）拾取处理——这与用户 Follow-up 的接收路径相同
（见「Session 命令 / Follow-up」）。正在执行的长工具调用会延迟拾取；期限到达仍
abort，最坏情况退化为无警告的直接终止。警告与终止都投影进 transcript，在 UI
可见。

每个 Prompt 执行只警告一次。agent 被警告后提前正常结束执行的，不再 abort；其
结果按各任务自己的完成契约评估（如报 `unfinished` 则任务失败、按现有 retry
语义处理），但现场是已提交、有记录的。

不做的事：

- 不把期限值暴露给 prompt：agent 没有可靠时钟，静态数字不可执行；可执行的
  「即将终止」信号由警告在需要时送达。
- 不在终止后自动提交或回滚残留现场；现场处理维持现状。
- 不在终止后替换、清除或重建 Runtime Session 绑定；此时只有用户显式 Reset 可以主动
  换绑。后续独立输入仍在提交前按缺失恢复规则准备 binding。
- 不为 housekeeping prompt（如 worktree cleanup follow-up）引入额外的执行类别
  概念：警告文案与其指令（提交或还原）语义相容，统一适用。

## 事件与状态核对

共同的 activity 与 transcript 契约以
[`agent-execution.md`](../agent-execution.md#activity-与-transcript) 为准；本节只定义
OpenCode 信号如何成为这些规范事实。

Runner 为共享 OpenCode Server 维护一个 `client.global.event()` 订阅。`OpenCodeRuntime`
按 Session ID 与 directory 路由事件。已知 typed event 被规范化为 Mohist 稳定的
transcript、tool、usage、model、status 与 compaction 事实；未知 OpenCode event 只进入
诊断信息，不改变 Workflow 或 Session 状态。

实时 event 只优化展示延迟，不作为持久化执行协议：

- 使用 OpenCode message ID 与 part ID 保证投影幂等；
- event stream 在仍有订阅者时断开，订阅层重新建立唯一的 global event stream；
- 新 stream 连接后，当前执行按自己的 Session ID 与 directory 读取
  `session.status()`，并与相关 `session.get/messages()` snapshot 核对；
- 一次执行只消费属于自己 Session ID 的 retry 事实，其他 Session 的事件不能改变其
  provider 错误判定；
- Prompt 完成后，如 event 缺失或需要确认最终用户可见 transcript，再核对 messages。

Mohist 不保存 V2 history cursor、aggregate sequence 或 event replay state。Workflow
task executor 根据 Action result，再应用 Mohist expectation、artifact、`failIf` 与
recovery 语义判断 Workflow 成功；AgentJob 是否完成由其 executor 独立判断。

## Provider 错误失败策略

provider 错误仅当判为不可恢复时让执行失败；可恢复错误（瞬时 429、5xx、网络抖动）交
OpenCode 重试，Mohist 不主动失败。失败信号来自 `session.status` 事件（`type:"retry"`，
携带 `attempt`、`message`、`action`、`next`）、重连后的 status snapshot 与执行最终的
prompt reject，不扫描日志。两类不可恢复判定都归一到 abort 当前执行并失败：

- 按性质不可恢复：优先使用 retry status 的结构化 `action.reason`；没有可用分类时，
  `message` 命中 quota、credit、billing、usage limit、额度、余额、使用上限或重置限额等
  模式即 abort+失败。普通 rate limit / too many requests 不因文案兜底在首次出现时失败。
  默认模式集覆盖常见 provider 的中英文额度措辞，runner 级可配置追加。
- 按证据不可恢复：可恢复错误连续重试，`attempt` 达到阈值 N（默认 5，runner 级可配置）
  而执行仍未完成，重新判为不可恢复，abort+失败。

可恢复错误在 N 次内恢复（执行完成）则继续，不失败。OpenCode 自身已判不可恢复的错误
（auth、invalid-request、context-overflow、content-policy）由 OpenCode 直接 reject
prompt，Mohist 不额外处理。连 retry 事件都不产生的静默卡死仍由 executor 期限兜底。

计数直接用 retry 事件的 `attempt` 字段（OpenCode 维护、每次 Prompt 执行重置）；runner 重启或
event stream 重连后用 `session.status()` snapshot 恢复，不另建状态。命中或超阈值时，
Runtime 使用当前锁定 SDK 的类型化调用面执行
`client.session.abort({ sessionID, directory }, { throwOnError: true })`。只有 abort 返回
`data: true`，且同一 directory 的 status snapshot 中该 Session 不存在或为 idle，才算
确认停止；随后向调用者返回带原始 provider message 的失败事实。AgentSession 与物理
Session 绑定保持不变，不提示 Reset。

abort 请求失败、返回值不确认成功，或 status 仍为 busy/retry 时，Runtime 返回
`abort-unconfirmed` 诊断，不声称执行已经停止。对 runner deadline，该诊断附加在
`deadline-exceeded` 结果上，不覆盖 timeout。OpenCode 是第三方依赖；Mohist 不修改其
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
- AgentSession idle 时，Follow-up 在受理输入前走通用 binding 准备；当前物理 Session
  明确缺失时先创建并持久化 replacement。AgentSession active 或 unknown 时不得替换，
  因为 Follow-up 的目标仍是当前执行。
- Endpoint 接收请求后立即返回；完成过程继续通过 Session events 呈现。
- Session active 时接收的 Follow-up 加入当前 OpenCode execution；Session idle 时立即
  开始处理。
- Routing 或 admission 失败必须返回给用户，不能自动 retry 或 replay。

### Compact

只有逻辑 Session idle 时才允许 Compact；Session 有工作正在执行时返回 conflict，
与 Reset 使用同一并发边界。先从 OpenCode Session 读取当前 model，再调用
`client.session.summarize({ sessionID, providerID, modelID })`。Compact 不创建新的物理
Session，也没有 Mohist 侧的 synthetic summary fallback。Session 没有当前 model 时返回
可操作错误，不能猜测。产生的 Session 与 message events 继续核对进 transcript。

### Reset

只有逻辑 Session idle 时才允许 Reset。先读取当前 model / variant（如果存在），再在同一
工作目录创建新的空 OpenCode Session。创建成功后才替换逻辑 Session 的 current binding。
AgentSession 不保存旧 binding；已有 transcript 保留，新物理 Session 的上下文为空。

每个命令携带完整的 expected current binding。Server 只在该绑定仍是 current 时应用返回的
replacement，防止过期 Reset result 覆盖更新的绑定。读取旧 Session 时收到结构化 missing
不阻止 Reset：Runtime 跳过 model / variant 继承并用 OpenCode 默认值创建新 Session；
其它读取失败仍明确失败。

Compact 与 Reset 都不轮换 AgentSession ID：命令响应返回同一稳定 `sessionId`，只有
Reset 替换 Runtime 绑定。API 响应形状与 CLI 文案不得再表述为"返回新 session id"。

## 权限与错误

OpenCode 原生 permission 配置是权威。它已经允许的操作由 OpenCode 直接执行，明确
拒绝的操作保持拒绝。`ask` 表示 OpenCode 将本次操作的选择交给调用方；对属于当前
headless 执行中的 `permission.asked`，`OpenCodeRuntime` 使用
`client.permission.reply({ requestID, directory, reply: "once" })` 回应。

这个回应只影响该 permission request，不写入 OpenCode 配置或 Session permission
规则，也不建立 Workflow Approval。事件必须按当前物理 Session ID 路由；event 携带
directory 时还必须与当前 workDir 一致。相同 request ID 在同一次执行中最多回应一次。

回应调用抛错或未确认成功时，Runtime 立即 abort 当前执行并在确认停止后返回
`permission required`；不能把 permission request 留到 executor deadline 才显示为
`interrupted`。OpenCode 负责单个工具的 timeout 与 retry；Mohist 只保留执行 deadline
和 abort 确认。

在 `OpenCodeRuntime` 边界把 SDK error 规范化为少量 Mohist result：`invalid input`、
`unavailable runtime`、`missing Session`、`incompatible runtime`、
`permission required`、`deadline exceeded`、`interrupted` 与 `execution-failed`。Provider-specific detail 只作为
诊断信息，不成为 Action output 字段。不要建立全局 Workflow error enum；各调用者通过
自己的 TaskRun 或 AgentJob 契约报告失败。

已知的本地 transport code（例如 header/body timeout）映射为稳定、可操作的失败文案；
完整 SDK / provider payload 只保留在 diagnostics，避免把未审查的外部内容带入 TaskRun。

## 模型目录

模型目录属于 `RunnerHost`，不属于 `OpenCodeRuntime`。Host 在首次注册前 best-effort 执行
`opencode models --verbose`，由 `runtime/opencode-models.ts` 一次解析模型与 provider 定义的
variant key，并直接保存到 host 的 `coderModels` / `coderModelVariants` 字段。正常退出产生
完整快照；超时后留下的可解析非空 stdout 产生不完整快照。发现失败或结果为空时，首次注册
上报空字段；不完整非空快照可以作为首次注册的 best-effort 目录。两种情况都不影响健康
Runtime 继续领取工作。

命令边界使用不经过 shell 的异步缓冲执行，并在进程关闭后才解析一次 stdout；这同时保留
退出前写入的尾部数据并避免阻塞 Runner event loop。单次发现的 deadline 是 3 秒。

首次注册与启动 convergence 完成后，Host 注册独立的周期发现 timer；默认周期 30 分钟、
最小 60 秒，首次触发从 timer 注册时刻起算。周期发现不检查 Runtime readiness。空结果或
失败保留最后一次非空快照；完整非空结果替换旧快照。不完整非空结果只能把新模型和 variant
并入旧快照，不能据此删除旧成员。合并后的模型与 variant 集合确实变化时才替换两个字段并
尝试一次即时 heartbeat。run loop 终止时由 Host 清理 timer。

目录只用于 Server 与 Web 的配置辅助，不是执行合法性的最终权威。省略 model 时使用当前
OpenCode 选择或默认值；选定 model / variant 是否有效仍由 OpenCode 在执行时校验。
`OpenCodeRuntime` 不加载、存储或刷新目录，也不调用 SDK model / provider list API 或 CLI
发现命令，模型发现状态不参与 Runtime readiness。

## 测试

默认测试不能启动真实 OpenCode，也不能使用真实 process、network、filesystem config
或 clock。Runtime 测试注入 fake generated Client / Server factory；Host 模型发现与周期
workspace 维护测试注入 fake discovery / Runtime 并使用 fake timer，确定性驱动事件、
snapshot、完成状态、process loss、回收 tick 与 error。

覆盖至少包括：

- Action Input expansion，并确认不存在隐藏 `vars.agent` fallback；
- model string 内含多层 `/`，variant 保持独立；
- CLI 模型发现的完整 stdout、variant key、失败恢复、周期 cadence 与变更 heartbeat；
- Workflow 与 AgentJob 的执行共享 Runtime code，但不共享工作 / Session 身份；
- 物理 Session reuse 与 rotation 不变量；
- model / variant 变化不触发 rotation；
- `session.get()` 的结构化 404 触发一次 create，且 binding 持久化与 input 都先于 Prompt；
- timeout、5xx、权限失败和畸形成功响应不触发 create，stale binding 不提交 Prompt；
- 非绑定 Runner 不探测或替换 Session，其本地 404 不触发 create；
- Prompt 调用开始后的 missing 或 transport failure 不 create、不 replay；
- 全局 event routing、duplicate suppression 与 snapshot reconciliation；
- Prompt completion、interruption、uncertain admission 与 no-replay 行为；
- async Follow-up（含 idle missing recovery）、原生 summarize、Reset（含旧 Session
  missing）、restart routing 与 stale-binding rejection；
- permission 一次性回应、重复 suppression、回应失败、missing Session、compatibility 与 process-loss failure；
- directory Instance 回收：只处理当前 Server generation 已使用且 WorkflowRun 为
  `Completed` / `Stopped` 的目录；busy、retry、未知状态与并发请求均延后；成功 dispose
  后不重复调用，后续新请求会重新跟踪；Server rebuild 清空旧 generation；
- Instance 回收先于自动与手动 workspace 删除，未确认释放时保留目录和注册表身份；
  周期成本不随无关历史 WorkflowRun 或已释放目录增长；
- 最小 `{ promise }` Workflow Action Output 与现有 expectation 语义；
- 两段式收尾：期限前警告注入（仅一次、fire-and-forget）、期限不足 5 分钟时执行
  开始即警告、期限到达 abort、被警告后提前结束不再 abort；全部以 fake clock 驱动。

## 完整替换

实现改动直接移除，而不是保留 deprecated 路径：

- `@agentclientprotocol/sdk`；
- `mohist/acp-agent` 与 ACP Action tree；
- 共享 ACP connection / session management；
- ACP liveness probes 及其配置；
- OpenCode log scanning；
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

- 存量 AgentSession 数据只需收敛到 current binding 结构，不复制或保留物理 Session 历史。
  ACP 时代的 current binding 在替换后视为“当前 Runtime Session 不存在”；提交新的独立
  输入时按 missing recovery 建立 OpenCode binding。Compact / Cancel 仍明确失败，Reset
  可以直接建立新绑定。
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
Session synchronization；新的 V2 Session execution core 仍把 `wait` 和 `compact` 报告为
unavailable，完成与恢复能力也尚未完整。

Mohist 跟随这些真实内部调用路径，而不是假设每个生成的 V2 方法都可用。SDK access
封装在 `OpenCodeRuntime` 内；以后迁移到完整 V2 Session 执行接口时，只改变
一个深模块，不改变 Workflow Action 或 Session 产品契约。

实现开始时必须先锁定 SDK package 版本，并对使用的调用面在真实 OpenCode 上做一次冒烟
验证；发现漂移时先修订本表，再进入实现。T-001 已在真实 OpenCode 1.18.3 服务器上对
Session 与 global event 调用做了一次冒烟验证（详见
[`openspec/changes/archive/2026-07-18-issue-409/sdk-smoke-verification.json`](../../openspec/changes/archive/2026-07-18-issue-409/sdk-smoke-verification.json)）：
表内 `client.session.*` 与 `client.global.event()` 调用可用；
`client.v2.session.wait()` 与 `client.v2.session.compact()` 仍返回
`ServiceUnavailableError`，确认不进入执行链。
实际锁定的 SDK 版本见实装差距小节。`client.instance.dispose()` 尚未包含在该次记录中，
落地 Directory Instance 回收前必须补做真实 Server 冒烟验证。

## 实装差距

Directory Instance 回收尚未落地。当前 `OpenCodeRuntime` 不跟踪 current Server generation
访问过的 directory，也不调用 `client.instance.dispose()`；WorkflowRun 终态目前只驱动
磁盘 workspace 的 eligibility 与 retention / budget cleanup。对应实施 issue 待从本
spec 创建。

「Prompt 期限与两段式收尾」在 `OpenCodeRuntime` 落地后由独立 issue 跟进；当前期限
到达直接终止执行，agent 没有收尾机会。

缺失恢复尚未落地：当前 `client.session.get()` 的 missing 直接结束执行，Workflow 与
AgentJob 尚未共用“创建 candidate → expected binding 替换 → 记录输入”的准备流程；
OpenCode 的 `SessionCommand` dispatch 当前对 Compact 和 Reset 都返回 `unavailable`。
缺失恢复的实施 issue 必须同时让 Reset 复用同一 expected-binding replacement；Compact
保持独立实装差距，不进入该 issue。对应实施 issue 待从本 spec 创建。

T-001 完成时实际锁定的 SDK 版本是 `@opencode-ai/sdk@1.18.3`（与安装在 PATH 上的
`opencode` CLI 版本一致），不是 1.17.18。决策文本保留 1.17.18 作为该节撰写时点的
参考版本；后续 T-002+ 实现时按 1.18.3 进行。冒烟记录在
[`openspec/changes/archive/2026-07-18-issue-409/sdk-smoke-verification.json`](../../openspec/changes/archive/2026-07-18-issue-409/sdk-smoke-verification.json)。
