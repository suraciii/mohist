---
status: wip
---

# Slack Agent Connection

Slack Agent Connection 把一个已经配置好的 Mohist Agent 作为独立身份接入一个 Slack
workspace。Slack 是交互入口，Mohist 仍是 Agent、工作、会话和结果的权威。

用户行为见 [`../docs/agent-connections.md`](../docs/agent-connections.md)，统一调用边界见
[`agent-api.md`](agent-api.md)。本文记录 Slack 集成的产品形态、系统边界与关键取舍，不固定
Slack SDK、API 路径、数据表或重试时序。

## 核心决策

| 主题 | 决策 |
|---|---|
| Agent 与 Slack 的关系 | Agent 先独立可用；Connection 只是同一个 Agent 的外部入口 |
| Slack 身份 | 一个 Agent 在一个 workspace 中对应一个独立 App / Bot，不用共享 Bot 猜目标 Agent |
| 多 workspace | 同一 Agent 可以建立多个 Connection；身份、Owner、权限和健康彼此独立 |
| 部署形态 | 第一版是 self-host 私有 App，由每个 Mohist Server 的一个 Slack 服务统一承载 |
| Agent 配置 | Connection 不保存另一份 Instructions、Runtime、Model 或 Skills |
| 访问控制 | Connection 只决定谁能调用，不削减或扩张 Agent 已配置的执行权限 |
| 对话映射 | 新根消息启动工作；同一 thread 的后续消息继续同一 AgentSession |
| 多 Agent | 一个 thread 可绑定多个 Agent；有歧义时必须明确提及目标 Bot |
| 可靠性 | Slack 允许重复投递；Mohist 去重并保留已接受输入，不能靠丢旧消息腾容量 |
| Web 的角色 | 配置、诊断和接管的备用平面，不是使用 Slack Agent 的必经工作站 |

## 系统边界

```text
Slack member
    │ message / action
    v
Slack App / Bot
    │
    v
mohist-slack ── Agent API ── Mohist Server ── Runner
    │                                │
    └── provider recovery state      └── Agent / Connection / Job / Session authority
```

| 组件 | 负责 | 不负责 |
|---|---|---|
| Slack | 成员身份、频道和消息交互、事件与回复传输 | Agent 配置、执行和工作结果 |
| `mohist-slack` | Slack 协议、身份映射、thread 映射、消息呈现与投递恢复 | 运行 Agent、修改 Agent 配置、裁定工作状态 |
| Mohist Server | AgentConnection、访问决策、AgentJob、AgentSession 和执行状态 | 猜测 Slack UI 状态或直接处理 Runtime 协议 |
| Runner | 按 Mohist 已解析的 Agent 定义执行 | Slack 身份、访问策略和 thread 路由 |

每个 Mohist Server 运行一个 `mohist-slack` 服务，集中承载该 Server 管理的所有 Slack
Connection。每个 Connection 仍使用独立 App / Bot 凭据；共享服务不意味着共享 Bot 身份。

`mohist-slack` 需要持久保存尚未完成的平台投递和 thread 映射，以便重启后恢复。它保存的只是
外部协议事实；Agent、Job、Session、Input 和结果始终由 Mohist Server 持有。

## Connection 模型

AgentConnection 属于一个 Project 中的一个 Agent，并固定绑定一个 provider 身份。Slack
Connection 确认 workspace、App 和 Bot 后不能改绑到另一个 Agent 或 workspace；需要改变
绑定时应新建 Connection。

一个 Connection 同时表达四个互不替代的方面：

| 方面 | 回答的问题 |
|---|---|
| Setup progress | 外部 App、凭据、身份和 Owner 是否已配置完成 |
| Desired state | 操作者当前希望它 Enabled 还是 Disabled |
| Connection health | Slack 服务、凭据和消息能力当前是否健康 |
| Agent Readiness | 被绑定的 Agent 是否具备执行本次任务的配置 |

不能用一个 `Connected` 覆盖这四类事实。Connection 可以已经连接但 Agent 仍 Needs setup；
Agent 也可以 Ready 而 Slack 服务暂时离线。

同一个 Agent 在同一个 workspace 最多保留一个未删除 Connection。一个 Bot 可以被邀请进多个
频道，不为每个频道复制 Connection。不同 workspace 的 Connection 不共享 Owner、Allowlist、
thread 映射或故障状态。

## Slack 体验档

Connection 创建时选择一个版本化体验档。体验档由 Mohist 提供完整 App 配置，用户不逐项
拼装权限。

| 体验档 | 定位 | 入口 |
|---|---|---|
| Standard Bot | 第一条交付路径，适用于允许安装私有 App 的普通 workspace | 一对一 DM、频道提及、thread、操作按钮 |
| Slack Agent | workspace 支持原生 Agent 体验时使用 | Standard 能力，加 Agent Messages 与按成员隔离的 Agent Home |

两种体验档调用同一个 Mohist Agent，不改变 Agent 能力或执行结果。Slack Agent 体验只增强
Slack 中的呈现：

- 不把成员正在浏览的频道或页面静默加入上下文；需要内容时必须明确发送或提及；
- Agent Home 是当前成员自己的最近工作和诊断入口，不展示其他成员的私有会话；
- 在 Mohist 拥有明确的 starter prompt 产品概念前，不从描述或历史自动猜 suggested prompts；
- Slack 平台不允许原生 Agent App 原地切回 Standard Bot 时，用户应重建 Connection / App，
  而不是让一个 Connection 同时承担两种不兼容身份。

第一版不支持 group DM。需要多人讨论时使用 channel 和 thread。

## Setup 与身份

Setup 是可恢复流程，而不是一次性向导：

1. 用户从 Agent 详情页或 CLI 创建 Connection，选择体验档，并预览 Slack 名称、头像与说明。
2. Mohist 提供完整 App 配置入口，用户在 Slack 创建、安装 App，并把凭据交回 Mohist。
3. Slack 服务验证 workspace、App、Bot 与体验档要求；服务离线时保留已有进度。
4. 验证通过后，未来 Owner 在 Bot 一对一 DM 中完成一次明确认领。
5. Owner 选择频道访问策略，把 Bot 邀请进目标频道并完成真实测试。

Setup 只展示已经确认的事实和唯一下一步：

| 进度 | 含义 |
|---|---|
| Create app & add credentials | Mohist 尚未得到足以确认 Slack 安装的凭据 |
| Waiting for Slack service | 配置已保存，但本机 Slack 服务暂时不可用 |
| Fix Slack setup | 已确认凭据、身份或体验档能力不匹配 |
| Claim owner | App 身份已确认，等待 workspace 成员在 DM 中认领 |
| Complete | 身份、Owner 和基础消息路径已确认 |

Mohist 无法观察用户是否只是在 Slack 网页中点完某一步，因此不制造虚假的中间完成状态。
Web 与 CLI 读取同一 Connection，任何时候都应能显示当前事实和下一步。

Owner 认领同时解决两个问题：把一个 Mohist 操作者配置的 App 交给一个经过 Slack 验证的成员，
并证明 Bot 的私聊收发路径真实可用。认领凭证短期、单次有效，明文只展示一次；只有安装
workspace 中仍有效的正式人类成员可以成为 Owner。

Slack 中的显示名称、头像和说明是 Agent 身份的外部投影，不是新的 Agent 定义。两侧发生
偏差时显示 identity drift 并引导用户修复；Mohist 不为同步外观索取额外管理权限。

## 访问与 Owner

访问策略沿用 Buzz 的简单模型，并把 DM 作为更严格的个人入口：

| 策略 | 一对一 DM | 频道提及和已绑定 thread |
|---|---|---|
| Owner only | 仅 Owner | 仅 Owner；默认值 |
| Allowlist | 仅 Owner | Owner 与明确选择的 workspace 成员 |
| Anyone | 仅 Owner | 已确认属于安装 workspace 的有效成员 |

`Anyone` 不是“任何看见消息的人”。Slack Connect 外部参与者、身份归属不明者、Bot、guest
和停用成员在第一版均不能调用。Owner 始终隐含在 Allowlist 中。

访问策略只回答“谁可以使用这个 Connection”，不改变 Agent 的 Runtime、Skills、仓库或工具
权限。相反，Slack 消息也不能临时扩张这些能力。

Owner 可以在 Slack 中管理自己的 Connection Allowlist，但以下操作仍属于 Mohist 操作者：

- 编辑或归档 Agent；
- 改变 Agent 执行配置；
- 转移 Connection Owner；
- 轮换凭据或删除 Connection。

停止当前执行只允许 Connection Owner 或该 Session 的最初发起者。Allowlist 成员可以继续
对话，但不能停止别人发起的工作。

Owner 离开 workspace、被停用或变为 guest 时，Connection 进入 `Owner unavailable` 的
Degraded 状态，不按显示名自动找替代者。Owner-only、DM 和 Owner 管理操作暂停；频道中的
Allowlist / Anyone 仍可按原策略工作，直到 Mohist 操作者发起新的 Owner 认领。

## 对话与多 Agent 路由

| Slack 场景 | Mohist 行为 |
|---|---|
| 新的 DM 根消息 | 为该 Agent 创建新的 AgentJob 与 AgentSession |
| 新的频道根消息并提及 Bot | 为该 Agent 创建新的 AgentJob 与 AgentSession |
| 已绑定 thread 中的后续消息 | 作为同一 AgentSession 的 follow-up，不创建 AgentJob |
| 已有人类讨论的 thread 第一次提及 Bot | 在确认上下文完整后，为该 Bot 创建独立 Session |
| 同一 thread 已绑定一个 Mohist Bot | 未提及的成员回复自然继续该 Session |
| 同一 thread 已绑定多个 Mohist Bot | 未提及回复不调用 Agent；必须明确选择一个 Bot |
| 一条消息提及同一 Server 的多个 Mohist Bot | 不启动任何 Agent，只提示用户选择一个 |
| Bot 自己发送的消息 | 永不自动触发另一个 Mohist Bot |

一个 Slack thread 可以同时有多个 AgentSession，但每个 Agent 各自拥有映射和上下文。第一次
在已有 thread 中提及新 Agent，不会切换或污染原 Agent 的 Session。

Buzz 以 channel 复用 Agent Session，因为 channel 是它自身的持续协作边界。Slack 的 Agent
与消息体验则把一次对话组织为 thread，因此 Mohist 选择 `Agent + thread` 作为 Session 边界，
而不是让整个 DM 或 channel 永久共享上下文。Bot 的第一次回复进入 thread；用户在 thread 中
继续表示延续会话，在 DM 根部发送新消息则表示开始独立工作。

已有讨论只有在 adapter 能读取约定范围内的完整上下文时才导入。无法完整读取时拒绝本次
启动并说明原因，不能拿部分隐藏上下文启动。后续编辑或删除 Slack 消息不会改写已经接受的
Mohist 输入；用户通过新 follow-up 更正。

只发送 Bot mention 不启动工作，Bot 会要求补充任务。包含文本或明确附件即可；附件单独作为
输入时，系统不能暗中编造 prompt。

不同 Mohist Server 之间不共享 thread 路由，因此第一版不承诺协调同一 workspace 中由不同
Server 管理的多个 Bot。

## 文件与回复

输入文件必须由用户明确附在当前消息或明确导入的上下文中，并且 Bot 有权读取。adapter 把
文件交给 Agent API 的附件边界，不把 Slack 凭据或临时下载地址交给 Agent。无法读取的文件
必须逐项说明；普通文本 URL 不由 adapter 自动抓取。

第一版不把 Mohist artifact 自动上传成新的 Slack 文件。回复可以包含结论、证据摘要、下一步、
已有可访问链接和稳定工作标识。没有外部可访问的 Mohist Web 地址时，不发送 localhost 链接；
Slack 中的回复仍应足以让用户做决定。

一次正常交互至少包含：

- 已接受、排队或无法接受的即时确认；
- 对用户有价值的执行状态变化，而不是原始工具事件；
- 最终回复、明确失败或需要人工处理的原因；
- 由 Mohist 当前状态生成的真实操作，例如 Stop 或 Open in Mohist。

Agent 生成的文本只作为内容渲染，不能伪造 Slack 控件，也不能意外触发 `@channel`、`@here`
或其它平台级通知。Slack Agent 体验可以流式呈现回复，但流式状态不成为新的工作真相。

Slack 投递失败不会改变 AgentJob 或 AgentTurn 的结果。发送结果无法确认时显示
`Delivery uncertain`，先核对再允许人工重发，并明确提醒可能重复显示。

## 可靠性与生命周期

| 情况 | 设计决策 |
|---|---|
| Slack 重复投递 | 使用稳定消息身份回到同一个 Mohist 输入，不创建第二项工作 |
| Slack 服务重启 | 从持久 thread 映射和待投递状态恢复，不依赖进程内记忆 |
| Mohist 暂时不可用 | 保留尚未提交的 provider 事件并显示退化；超过平台保留窗口后要求用户重发 |
| Agent 暂时无容量 | Mohist 已接受的输入继续排队，并在 Slack 中显示背压 |
| 本地缓冲达到边界 | 不再接受新的平台消息；绝不丢弃已经被 Mohist 接受的输入 |
| 回复状态不确定 | 标记 Delivery uncertain，不把盲目重发当成可靠性 |
| Connection Disabled | 拒绝新 Slack 输入和输出；已接受执行继续，重新启用不回放禁用期间消息 |
| Connection Deleted | 删除 Connection、凭据和 provider 映射；保留 Agent、Job、Session 与审计记录 |
| 凭据失效或身份改变 | Connection 进入 Degraded，修复并重新验证；不能静默绑定到另一个 Bot |
| Agent Needs setup | Connection 健康保持独立；拒绝新工作并向普通成员显示安全摘要 |
| Agent Readiness Unknown | 可以接受并显示等待验证，最终由 Mohist 裁定是否可执行 |

Slack 到 adapter 是 at-least-once 的外部传输，不能宣称端到端 exactly-once。Mohist 的目标是
重复事件不重复产生领域效果，已确认回复不重复发送，无法确认时把不确定性暴露出来。

长时间离线可能超过 Slack 的事件保留或重试窗口，因此不能承诺补回所有消息。恢复后应明确
显示可能缺口，让用户重新发送关键委托。

## 安全与运营边界

- 第一版使用 self-host 私有 Slack App 和 Socket Mode，避免要求 Mohist 暴露公共入站端点。
- Slack 服务是高权限本机组件，第一版与 Mohist Server 作为同一信任域部署。
- App 与 Bot 凭据加密保存，不进入 Agent Instructions、transcript、日志或客户端可见状态。
- Slack 服务只获得调用固定 Connection、读取结果和回送消息所需的 Mohist 权限。
- 每次调用都记录 workspace、conversation 和成员身份，但这些身份不自动成为 Mohist 管理员。
- 成员校验以 Slack 的稳定 workspace 身份为准，不以显示名、头像或消息文本判断授权。

第一版不建设公开 App Marketplace、多租户托管、计费或跨组织身份联邦。那些需求会改变安装、
授权和运营模型，应作为独立产品阶段设计。

## 从 Buzz 借鉴的取舍

Mohist 直接采用 Buzz 已经验证清晰的部分：

- `Owner only / Allowlist / Anyone` 三档访问策略，默认 Owner only；
- DM 始终比频道更严格，只允许 Owner；
- Allowlist 用成员搜索和头像配置，不把外部 member ID 当作主要交互；
- provider 接收队列有明确边界，避免无限占用资源。

Mohist 不照搬 Buzz 的进程内队列语义。Slack 事件在成为 Mohist 输入前可以被 adapter 限流或
拒绝；一旦 Mohist 已确认接受，就成为可追踪的 SessionInput，不能通过 drop-oldest 之类策略
删除。这个差异来自 Mohist 作为执行和审计平面的产品责任。

## 非目标

- 不让 Slack Bot 运行 Agent Runtime 或拥有另一份 Agent 配置。
- 不在 Slack 中复制 Agent 编辑器、Workflow 看板或完整诊断工作台。
- 不让共享 Bot 根据自然语言猜测目标 Agent。
- 不把所有频道消息发送给 Mohist；只有 DM、明确提及和已绑定 thread 的回复触发。
- 第一版不支持 group DM、Slack Connect 外部成员调用或跨 Server 多 Bot 协调。
- 第一版不自动把 Mohist artifact 上传成 Slack 文件。
- 本文不固定 API 路径、存储字段、锁和租约、Slack SDK 版本或精确重试时间。

## 实装差距与顺序

当前仓库没有 Slack Connection 或 `mohist-slack` 服务。Web 与 CLI 已有 Agent 直接使用的基础
路径，但 Agent API 的跨入口契约也尚未完整。

实施顺序遵循依赖关系和可独立验证的产品价值：

1. 完成 Agent 从 Web / CLI 独立启动、继续、观察和停止的统一语义。
2. 建立 AgentConnection、Slack 服务与可恢复 Setup，但不同时追求所有 Slack 表面。
3. 先交付 Standard Bot 的 Owner-only DM 垂直路径，证明真实 Agent 可从 Slack 使用。
4. 再加入频道、thread、多 Agent 路由、访问策略、附件和故障恢复。
5. 最后加入 Slack Agent Messages、流式回复和按成员隔离的 Agent Home。

每一步都必须保持 Slack 只是 Agent API 的客户端。若某项 Slack 能力要求 adapter 解析 Runner
日志、覆盖 Agent 配置或直接读写 Mohist 数据库，说明前置边界仍未完成，应先修正 Agent API。
