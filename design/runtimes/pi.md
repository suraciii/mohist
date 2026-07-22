# Pi Runtime

## 决策

`mohist/pi` 是基于 `@earendil-works/pi-coding-agent` SDK **进程内**实现的 Runtime 特有
Action。它与 Agent / Session 的所有权模型（Inline Agent、工作所有者、共享 Runtime
不制造依赖等不变量）见 [`agent-execution.md`](../agent-execution.md)；与
`OpenCodeRuntime` 的关系是平行深模块，不共享接口、不互相包装。

接入方式的取舍：

- **不用 ACP**。Pi 不原生支持 ACP；现存的 ACP 通路全部是社区 adapter（桥到
  `pi --mode rpc` 或内嵌 SDK），引入它等于在 SDK 之上再加一层第三方移动部件。这与
  移除 `mohist/acp-agent`、不保留 ACP fallback 的既有决策一致。
- **不用 `--mode rpc`**。RPC 模式的 `prompt` 响应只确认受理，完成要靠事件流判断；
  一个 RPC 进程同时只持有一个活跃 Session，多会话并发要管理多个子进程；并且没有
  类型安全。SDK 的 `session.prompt()` 直接 await 到回合结束，与 `OpenCodeRuntime`
  的「prompt 响应即唯一完成判据」语义同构。若未来出现进程隔离的硬需求（Pi 崩溃
  不拖垮 Runner），可以再评估 RPC 模式；换底只改变 `PiRuntime` 内部，不改变
  Workflow Action 或 Session 产品契约。
- **不引入通用 `AgentRuntime` 接口**。稳定边界是 Workflow Action 契约、AgentJob
  执行契约和 Session 命令。`PiRuntime` 的 boundary types 独立一份
  （`runtime: "pi"`），与 `runtime/opencode/types.ts` 形状平行是有意的冗余，不是
  遗漏的抽象。

与 OpenCode 的责任边界差异：Pi 是 Runner 的 npm 依赖，随 Runner 发布并锁定版本，
安装者不需要提供 Pi CLI。provider 凭证走 Pi 自己的机制（环境变量与 Pi auth
存储），Mohist 不管理 API key。SDK authentication manager 是凭证值的唯一读取者；
Mohist 自有 request/result、事件、注册和 smoke artifact 都不携带凭证字段。

## Action 输入输出契约

```ts
type PiActionInput = {
  prompt: PromptSpec
  session?: string
  options?: {
    model?: string
    variant?: string
  }
}

type PiActionOutput = null | {
  promise: string
}
```

输入形状、展开时机、输出投影与 `mohist/opencode` 完全一致（见
[`opencode.md`](opencode.md) 的「Action 输入输出契约」），差异只有两点：

- `model` 使用 Pi 的 `provider/model` 形式，同样只按第一个 `/` 分割；模型是否合法
  由 Pi 最终校验。
- `variant` 映射为 Pi 的 thinking level（`off` / `minimal` / `low` / `medium` /
  `high` / `xhigh` / `max`）。它始终是独立字段，不能拼进 model ID；非法取值由 Pi
  拒绝并规范化为回合失败，Mohist 不预校验档位集合。

`options` 中除 `model` 与 `variant` 之外的键被忽略并记入诊断，不使回合失败；这让
含有 `runtime` 键（供 Mohist Agent 路径读取）或遗留键的已持久化 `vars.agent` 可以
继续绑定到本 Action。`options` 不携带 `runtime`：Workflow 路径的后端选择点就是
`uses`。

## SDK 调用面

依赖包是 `@earendil-works/pi-coding-agent`（npm scope 已从 `@mariozechner/*` 迁至
`@earendil-works/*`），要求 Node ≥ 22.19。SDK 为纯进程内调用：LLM 请求与内置工具
执行都发生在 Runner 进程内，没有独立 Server 进程。

| 能力 | SDK operation |
|---|---|
| 创建物理 Session | `SessionManager.create(cwd, sessionDir?)`，配合 `createAgentSession({ sessionManager, modelRuntime, settingsManager, resourceLoader, ... })` |
| 恢复物理 Session | `SessionManager.open(sessionFile)`，配合同一组显式服务创建 `AgentSession` |
| 执行并等待 Workflow / AgentJob 回合 | `await session.prompt(text, { expandPromptTemplates: false })` |
| 回合中注入收尾警告 | `session.steer(text)` |
| 提交用户 Follow-up（回合执行中） | `session.steer(text)` |
| 提交用户 Follow-up（Session 空闲） | `session.prompt(text)`，不等待其完成 |
| 中断执行 | `await session.abort()`；停止确认读取 `session.isStreaming`，不是 abort 返回值 |
| 压缩 context | `session.compact()` |
| 应用回合模型与推理档位 | `session.setModel()`、`session.setThinkingLevel()` |
| 读取 Session 状态与消息 | `session.sessionId`、`session.sessionFile`、`session.messages`、`session.isStreaming` |
| 接收实时事件 | `session.subscribe(listener)` |
| 读取 model catalog | `ModelRuntime.create({ ... })` 后 `await modelRuntime.getAvailable()` |

实现开始时必须先锁定 SDK package 版本，并对上表断言的调用面在真实 Pi 上做一次冒烟
验证（含事件载荷形状）；发现漂移时先修订本表，再进入实现。0.80.10 的真实验证记录在
`openspec/changes/issue-450/sdk-smoke-verification.json`，冒烟记录参照
[`openspec/changes/archive/2026-07-18-issue-409/sdk-smoke-verification.json`](../../openspec/changes/archive/2026-07-18-issue-409/sdk-smoke-verification.json)
的做法留存。

## 深模块边界

`PiRuntime` 是 Runner 内部的深模块，负责：

- SDK 服务装配（`ModelRuntime`、`SettingsManager`、`DefaultResourceLoader`）与
  model catalog；
- 就绪状态与兼容性诊断；
- 物理 Session 创建、按绑定恢复、实例缓存与中断；
- Prompt 执行、Follow-up、Compact 与 Reset；
- 事件订阅与规范化投影；
- Pi error 与版本兼容性诊断。

边界规则与 `OpenCodeRuntime` 相同：`mohist/pi` Action、AgentJob execution adapter 与
Session command handler 只依赖 Mohist 定义的 request / result 类型
（`runtime/pi/types.ts`，`runtime` 字面量为 `"pi"`），不暴露 SDK 类型。Runtime 接收
已经组装好的回合输入与 Session 绑定；它不接收 Mohist Agent ID / name，也不加载
Mohist Agent 定义。model string 解析、`Model` 对象构造、调用顺序、实例缓存和 Pi
error 解释全部封装在该模块内。

它不是逐方法透传的 SDK wrapper。调用者请求 run turn、follow up、compact、reset 等
Mohist 能力，由模块决定使用哪些 SDK operation 才能完成该能力。

回合输入按纯文本提交：`PiRuntime` 不加载 prompt templates，也不做斜杠命令展开——
以 `/` 开头的工作流 prompt 仍须原样进入模型。0.80.10 的 `prompt()` 默认会展开
文件型 prompt template，因此每次 Workflow 调用必须显式传入
`{ expandPromptTemplates: false }`。

## 进程拓扑与就绪

每个 Runner 进程拥有一个 `PiRuntime`，由所有 Pi Session 共享。每个活跃物理 Session
对应一个进程内 `AgentSession` 实例：按绑定首次使用时创建并缓存，Runner 重启后从
持久化绑定 lazy 恢复。不为每个 Action 创建独立进程，也不为每个回合重建 Session
实例。

Mohist 认为逻辑 AgentSession 的 workDir 与物理 Session 的 directory 都不可变。工作目录
变化时拒绝本次执行；调用者必须使用新的逻辑 Session 身份，不能在原 AgentSession 上创建
替代绑定。Pi 的 session 文件按 cwd 分目录存放（默认
`~/.pi/agent/sessions/<cwd 编码>/`），与目录不可变语义天然一致；Mohist 不引入独立
的 session-dir 配置。

Runner 注册或领取工作前必须：

1. 完成 SDK 服务装配；
2. 成功加载 model catalog。

catalog 加载成功即 ready；catalog 为空（没有任何已配置凭证的 provider）记 warning
诊断但不阻止 ready——模型合法性始终由 Pi 在回合时最终校验。服务装配失败或 catalog
加载失败时，`PiRuntime` 不 ready，Runner 停止领取新工作并重建；这与
`OpenCodeRuntime` 的就绪 gate 对齐，两个 Runtime 的就绪状态都纳入领活条件。

与 OpenCode 的进程拓扑差异及其语义：Pi 在 Runner 进程内执行，Runner 进程终止时所有
执行中的 Pi 回合随之终止——不存在独立 Server 的退出、重建与事件流重连。持久化的
物理 Session（JSONL 文件）不受影响，Runner 重启后按绑定恢复；已终止的回合不自动
replay，由工作所有者的 redelivery 语义兜底。

## Session 绑定

AgentSession 所有权与来源见 [`agent-execution.md`](../agent-execution.md)，Runtime 身份
字段命名见 [`conventions.md`](../conventions.md)。逻辑 Session 目标解析、绑定创建时序
（先创建物理 Session，持久化绑定成功后才提交首个 Prompt；持久化幂等）、复用不变量
（跨 task、retry 与 Runner 重启解析到当前绑定；工作目录不同则在提交 Prompt 前以可
操作错误拒绝）与 `OpenCodeRuntime` 完全一致，本节只定义 Pi 特有部分。

物理绑定的 `runtimeSessionId` 持久化 **Pi session 文件的绝对路径**
（`session.sessionFile`；`SessionManager.create()` 路径下必有值，取不到文件路径视为
`incompatible-runtime`）。SDK 的恢复入口 `SessionManager.open()` 以文件路径为键，
没有按 uuid 打开的调用面；session uuid（`session.sessionId`）只进入诊断信息。

物理 Session 实例的恢复是 lazy 的：进程内缓存命中直接使用；未命中时用绑定中的
session 文件路径 `SessionManager.open()` 恢复，messages、model 与 thinking level 由
SDK 自动还原。绑定存在但 session 文件缺失或损坏时，本次工作失败并提示 Reset；不得
隐式调用 create 伪造连续上下文。

Pi 在 session 出现第一条 assistant 消息之前不落盘 session 文件。首个 Prompt 执行中
Runner 崩溃会留下「绑定存在、文件从未生成」的状态，重启后的恢复因此按上段的文件
缺失规则失败并提示 Reset。这是已接受限制：丢失的至多是一个提交状态本就不确定的
未完成回合，Reset 没有任何上下文损失；与 redelivery 可能重复回合的限制同属一类。

Runtime 变化与 Reset 会创建新物理绑定并追加 lineage，不迁移上下文；Compact 与
model / variant 变化必须保持同一 session 文件。model 与 thinking level 是回合执行
参数：复用已有 Session 时，Runtime 在原物理 Session 上 `setModel()` /
`setThinkingLevel()` 应用本次选择后执行 Prompt，不触发 attach replacement 或追加
lineage。

worktree cleanup follow-up 的处理与 `OpenCodeRuntime` 相同：executor 再次调用原
task 已解析的 Action，走同一 Runtime 和物理 Session，不得替换绑定。

## 回合执行

Workflow Action adapter 或 AgentJob executor 请求的回合按以下顺序执行：

1. 按当前绑定解析或创建进程内 `AgentSession` 实例（见「Session 绑定」）；
2. 解析可选 model string，在 Session 上应用本次 model 与 thinking level；
3. 调用并等待 `session.prompt(text)`；
4. 把收到的事件投影到 AgentSession；
5. 从 `session.messages` 最后一条 assistant 消息提取最终文本；
6. 向调用者返回规范化完成事实。

`session.prompt()` resolve 即整个 agent run（含工具循环与自动重试）结束，它就是
唯一完成判据，不存在第二次 wait；`agent_end` 事件只用于投影，不作为完成权威。
`PiRuntime` 不执行 Workflow expectations，也不判断 AgentJob 成功。调用者必须声明工作
回合的 duration。issue #450 的 Workflow task executor 通过 Runner-private Action context
固定提供 60 分钟，`mohist/pi` Action Input 不可见也不能覆盖。Action 完成 open/bind、输入
报告与 model/thinking 应用后，把 duration 交给 `runTurn`；Runtime 在调用
`session.prompt()` 前读取注入时钟并形成绝对 deadline。队列等待、绑定与输入报告不占 Prompt
预算；cleanup Prompt 是独立回合并取得新的 60 分钟。AgentJob executor 的期限由其所属
issue 单独定义。

in-process 调用没有 transport timeout；executor 的 AbortSignal 与声明的期限是单一
回合期限权威。期限到达时 Runtime 将回合结果固定为 `deadline-exceeded`，随后调用
`session.abort()` 收尾；迟到 resolve 的 `prompt()` 不能翻转该结果。任何失败都不
自动重放提交状态不确定的 Prompt；redelivery 在 crash window 内可能造成重复回合，
这是与 OpenCode 一致的已接受限制。

## 回合期限与两段式收尾

期限协议与 [`opencode.md`](opencode.md) 的「回合期限与两段式收尾」相同：期限前 5
分钟注入一次任务无关的收尾警告（期限不足 5 分钟时回合开始即注入），期限到达先
固定 `deadline-exceeded` 再中断收尾。Pi 侧的差异只是通道：

- 警告注入使用 `session.steer(text)`。steer 消息在运行中回合的迭代边界（当前模型
  调用及其工具调用完成后）被拾取，语义与 OpenCode 的 `promptAsync` 注入一致；
  正在执行的长工具调用会延迟拾取，期限到达仍 abort。
- 终止使用 `await session.abort()`，并通过 Session 事件与 `isStreaming` 核对确认停止；
  无法确认时返回中断未确认诊断，不声称回合已经安全停止。

0.80.10 没有独立的 stop-confirmation operation，也没有布尔型 `abort()` 返回值；
`abort()` 的 Promise 只表示中断请求已处理，停止确认必须观察 `isStreaming` 与事件序列。

## 事件与状态核对

共同的 Turn 生命周期、transcript 事件名称与结束规则以
[`agent-execution.md`](../agent-execution.md#turn-生命周期与-transcript-dsl) 为准；本节只定义
Pi 信号如何成为这些规范事实。

`PiRuntime` 对每个活跃 `AgentSession` 实例维护一个 `session.subscribe()` 订阅。
已知事件被规范化为 Mohist 稳定的 transcript、tool、usage、model、status 与
compaction 事实：

- `message_start` / `message_update`（`text_delta`、`thinking_delta`、
  `toolcall_start` / `delta` / `end`）/ `message_end` → transcript 与 tool 事实；
- `tool_execution_start` / `update` / `end` → 工具执行事实（按 `toolCallId` 关联）；
- assistant message 上的 `usage`（input / output / cacheRead / cacheWrite / thought / cost）
  → usage 事实；
- `compaction_start` / `compaction_end` → compaction 事实；
- `auto_retry_start` / `auto_retry_end` → provider 重试事实（见下节）。

投影使用 Pi 的 message id 与 `toolCallId` 保证幂等。未知事件只进入诊断信息，不
改变 Workflow 或 Session 状态。

事件通道是进程内回调，没有传输层，因此不存在 OpenCode 侧的断流重连与 snapshot
核对机制；回合的最终状态以 `prompt()` 的 resolve 值与 `session.messages` 为准。
Runner 进程终止即事件通道与执行中回合一并终止（见「进程拓扑与就绪」），重启后不
重建「回合仍在执行」的假象。

## Provider 错误失败策略

判定规则与 [`opencode.md`](opencode.md) 的「Provider 错误失败策略」相同：可恢复
错误交 Pi 重试，不可恢复错误 abort 回合并失败。Pi 侧的信号来源：

- `auto_retry_start` 事件携带 `attempt`、`maxAttempts`、`delayMs` 与 `errorMessage`，
  是重试事实的唯一来源；不扫描日志。
- 按性质不可恢复：`errorMessage` 命中 quota、credit、billing、usage limit、额度、
  余额、使用上限或重置限额等模式即 abort+失败（默认模式集与 OpenCode 相同，覆盖
  中英文额度措辞，runner 级可配置追加）。普通 rate limit 不因文案兜底在首次出现
  时失败。
- 按证据不可恢复：可恢复错误连续重试，`attempt` 达到阈值 N（默认 5，runner 级可
  配置）而回合仍未完成，abort+失败。计数直接消费事件的 `attempt` 字段，不另建
  状态。
- Pi 自己判不可恢复的错误（auth、invalid request、context overflow 等）结束自动
  重试并以 `stopReason: "error"` 完成回合，`prompt()` 正常 resolve；Runtime 从末条
  assistant 消息的 error 信息规范化出 `turn-failed`，不额外处理。

命中不可恢复判定时执行 `session.abort()` 并确认停止（见上节），随后向调用者返回
带原始 provider message 的失败事实。AgentSession 与物理 Session 绑定保持不变，不
提示 Reset。

## Session 命令

Session command 的通用语义（`notStarted` 与 `unavailable` 的区分、expected current
binding、不轮换 AgentSession ID）与 [`opencode.md`](opencode.md) 的「Session 命令」
相同。Pi 侧的通道映射：

### Follow-up

- 回合执行中：`session.steer(text)` 注入当前回合；Session 空闲：
  `session.prompt(text, { preflightResult })`；preflight 回调是「确认 Pi 已接收」的
  落点（Pi 的 RPC 模式使用同一钩子），preflight 拒绝（如 model 或凭证缺失）作为
  命令失败返回给用户；受理后立即返回，完成过程继续通过 Session 事件呈现。
- 可选的当前 model / variant 选择在注入前应用到 Session（`setModel()` /
  `setThinkingLevel()`），物理 Session 不轮换。
- Routing 或 admission 失败必须返回给用户，不能自动 retry 或 replay。

### Compact

只有逻辑 Session idle 时才允许 Compact，与 Reset 使用同一并发边界。调用
`session.compact()` 使用 Pi 原生压缩；压缩使用 Session 当前 model。Compact 不创建
新的物理 Session，session 文件身份不变，也没有 Mohist 侧的 synthetic summary
fallback。Pi 压缩失败时明确报错，不静默降级。产生的 compaction 事件继续核对进
transcript。

### Reset

只有逻辑 Session idle 时才允许 Reset。先读取当前 model / thinking level（如果存
在），再在同一工作目录用 `SessionManager.create(cwd)` 建立新的空 Pi Session。创建
成功后才替换逻辑 Session 绑定（新 session 文件路径），并把新物理绑定追加到
lineage。旧 session 文件保留查询和审计能力，但其上下文不进入新 Session。

### Cancel

对执行中的回合调用 `session.abort()`。`cancelled: true` 只表示中断请求已被 Runtime
接受并执行；回合是否立刻停下由 Pi 决定，Runtime 如实报告这次尝试。

## 权限、项目信任与错误

Pi 没有 per-tool 批准机制，也不提供沙箱：已配置的工具以 Runner 进程权限直接执行，
headless 下不存在人机交互阻塞。`OpenCodeRuntime` 的 `permission.asked` → 一次性
reply 路径在 Pi 侧不存在，对应的 `permission-required` 错误也不属于 Pi 的规范化
错误集合。

Pi 唯一的「批准」概念是 project trust：是否加载工作目录项目级 `.pi/` 资源
（settings、extensions、skills、prompts 等）。`PiRuntime` 固定以
`SettingsManager.create(cwd, agentDir, { projectTrusted: false })` 装配 `SettingsManager`，
并把同一个 manager、显式 `cwd` / `agentDir` 传给 `DefaultResourceLoader` 和
`createAgentSession`：项目级 `.pi/` 内的可执行资源不进入
执行，工作仓库无法通过携带 Pi 配置改变 Runner 的执行行为。仓库根部的 `AGENTS.md` /
`CLAUDE.md` 与 project trust 无关，仍作为上下文提供给模型——这与 OpenCode 的行为
一致：它们影响提示词上下文，不改变 Runner 的执行配置。Runner 用户的全局配置
（`~/.pi/agent`）正常加载。该取值不提供配置项，是无人值守执行的确定性保证。

Pi 边界复用 Runner 现有的 credential masking：SDK/provider 文本进入 task log、
diagnostic 或 runtime event 前统一脱敏，结构化 request/result 与 Runner registration
使用 Mohist 字段白名单而非序列化 SDK 对象。Action output 不含 diagnostic。真实 smoke
只记录版本、operation 名、布尔结果和脱敏后的字段名/类型摘要；不记录环境值、auth 文件、
原始 provider 响应、Prompt 或消息正文。

在 `PiRuntime` 边界把 SDK error 规范化为少量 Mohist result（kebab-case，与 wire 值
一致）：`invalid-input`、`unavailable-runtime`、`missing-session`、
`incompatible-runtime`、`deadline-exceeded`、`interrupted` 与 `turn-failed`。
Provider-specific detail 只作为诊断信息，不成为 Action output 字段。

## 模型目录

通过 `modelRuntime.getAvailable()` 加载 model catalog；它只返回已配置凭证的
provider 模型，这正是配置辅助需要的语义。catalog 中每个模型的 variant 列表是 Pi
的 thinking level 档位。Runner registration 把 Pi catalog 与 OpenCode catalog 按
runtime 并列上报，Server 与 Web 按执行后端分组展示。省略 model 时使用 Session 当前
选择或 Pi 默认值；选定 model 是否有效仍由 Pi 最终校验。

## Server 与 Web 触及面

Pi 是第二个 Runtime，以下既有单 Runtime 假设需要泛化（均不改变产品契约）：

- Server 的 runtime 注册表：`AgentSessionGrain` 的 `IsRuntimeRegistered` 注册
  `"pi"`；Reset 对未注册历史 runtime 的 fallback 行为不变。
- Agent launch：`AgentLauncher` 从 Agent 配置读取执行后端，不再硬编码
  `"opencode"`；后端随 Agent snapshot 固定到 AgentJob input。
- AgentJob executor：按 dispatch 携带的 runtime 分派到 `OpenCodeRuntime` 或
  `PiRuntime`，两条路径共享 Session 基础设施但不共享 Runtime 实例。
- Runner 的 open / attach 回写：runtime 值来自调用方解析结果，不再写死。
- Session usage：`AgentUsageSummary`、grain state/surrogate、runtime-event parser、API/read
  model 与 Web 共用类型新增独立 `cachedWriteTokens`；新增 Orleans field id 只追加不重排，
  缺省为 null/0 语义并与 `cachedReadTokens` 分别累加。
- TaskRun 分类：`mohist/pi` 与 `mohist/opencode` 同样归为 UserFacing。
- 模型 catalog API：opencode 专属路由泛化为按 runtime 查询，或并列新增 Pi 路由。
- Session 命令 handler（Follow-up / Cancel / Compact / Reset）：按 AgentSession 当前
  绑定的 runtime 路由到对应 Runtime。
- Runner host：构造并启动 `PiRuntime`，由 manifest 声明的 `agent-turn` capability 向
  Workflow Action 注入回合能力，并向 `AgentJobExecutor` 注入 Runtime；promise 投影按
  capability 驱动。#450 若先于能力收窄 issue #447 落地，会暂时沿用当前 runtime-bearing
  `ActionContext` 与按名投影机制；这是 #447 明确拥有的实现差距，不是本设计的目标接口。
- Web：Mohist Agent 编辑与 issue 模型选择增加执行后端维度；模型列表按所选后端
  出（OpenCode catalog / Pi catalog）。

## 测试

默认测试不能启动真实 Pi，也不能使用真实 process、network、filesystem config 或
clock。SDK 的全部依赖锁在 `PiRuntime` 模块内，经 factory seam 注入 fake
`PiRuntime` 或 fake SDK 工厂，确定性驱动事件、完成状态、进程终止与 error。

覆盖至少包括：

- Action Input expansion，并确认不存在隐藏 `vars.agent` fallback；
- `options` 未知键（含 `runtime`）忽略并记诊断，不使回合失败；
- model string 内含多层 `/`，variant 保持独立并映射 thinking level；
- Workflow 与 AgentJob 拥有的回合共享 Runtime code，但不共享工作 / Session 身份；
- 物理 Session 复用与 rotation 不变量；model / thinking level 变化不触发 rotation；
- 绑定恢复：缓存命中、lazy open、session 文件缺失时报 `missing-session` 并提示
  Reset，不隐式 create；
- prompt 完成、中断、提交状态不确定与 no-replay 行为；
- `steer` 注入（运行中 Follow-up 与期限警告）、空闲 Follow-up、原生 compact、
  Reset、stale-binding rejection；
- `projectTrusted: false` 装配断言：项目级 `.pi/` 资源不进入执行；
- provider 错误策略：模式命中即失败、阈值失败、Pi 自判不可恢复的 `turn-failed`
  规范化；
- 两段式收尾：期限前 steer 警告（仅一次）、期限不足 5 分钟时回合开始即警告、期限
  到达 abort、被警告后提前结束不再 abort；全部以 fake clock 驱动；
- 最小 `{ promise }` Workflow Action Output 与现有 expectation 语义。

## 上游边界

Pi 是 0.x 快速演进的依赖（约每周一个 minor），SDK 的 breaking change 集中在创建
与服务装配层（scope 迁移、runtime 装配重构、参数类型变更），事件协议相对稳定。
应对策略：

- 锁定 SDK package 版本，升级时逐条阅读 CHANGELOG 的 Breaking / Changed 节并跑
  集成冒烟；
- SDK access 全部封装在 `PiRuntime` 内，升级漂移只改变这一个深模块；
- 本表撰写时的参考版本是 `@earendil-works/pi-coding-agent` 0.80.10；实现开始时按
  「SDK 调用面」的要求重新锁定并冒烟验证。

## 实装差距

直接 Workflow 路径已经实装：`PiRuntime`、`mohist/pi` Action、runtime-aware Workflow
Session binding，以及现有 Session transcript/tool/status/compaction/model/usage/cost/
lineage 展示均已落地。以下设计触及面仍是实现差距：

- AgentJob executor 与 Agent 配置中的 runtime 选择仍未接入，Mohist Agent 固定使用 OpenCode。
- runtime-aware model catalog API 与 Web 模型选择 UI 仍未接入。
