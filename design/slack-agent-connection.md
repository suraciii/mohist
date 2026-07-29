---
status: wip
---

# Slack Agent Connection

Slack Agent Connection 把一个已经配置好的 Mohist Agent 作为独立身份接入一个 Slack
workspace。Slack 是交互入口，Mohist 仍是 Agent、工作、会话和结果的权威。

产品行为——接入条件、Setup 步骤、访问策略、线程用法、回复呈现、生命周期异常——全部由
[`../docs/agent-connections.md`](../docs/agent-connections.md) 定义，本文不复述。统一调用边界见
[`agent-api.md`](agent-api.md)。本文只记录组件边界与必须长期成立的取舍。

## 核心决策

| 主题 | 决策 | 理由 |
|---|---|---|
| Agent 与 Slack 的关系 | Agent 先独立可用；Connection 只是同一个 Agent 的外部入口 | Slack 不能成为 Agent 能工作的前提 |
| Slack 身份 | 一个 Agent 在一个 workspace 中对应一个独立 App / Bot | 用户看到谁就知道调用谁，不用共享 Bot 猜目标 Agent |
| `mohist-slack` 独立进程 | 是，但只是工具链选择 | Slack 的一等客户端在 Node，与 runner 同一套 TS 工具链；.NET 侧要自维护 Socket Mode 与事件模型 |
| adapter 是否持久化 | 否 | 进程边界不等于状态边界；Server 已经是唯一状态权威 |
| 入站、对话映射与出站投递 | Server 持有 | 与 Session 落同一个备份边界，消除双权威与跨进程结果未知 |
| Agent 配置 | Connection 不保存另一份 Instructions、Runtime、Model 或 Skills | 执行定义只有一份 |
| 访问控制 | Connection 只决定谁能调用，不削减或扩张 Agent 已配置的执行权限 | 调用范围与执行能力是两件事 |
| 对话映射 | 频道根提及建立 Session、thread 回复继续；DM 普通消息继续 current Session，New task 才切换 | 遵循两个 Slack 场景各自的对话习惯 |
| 可靠性 | Slack 允许重复投递；Mohist 去重并保留已接受输入 | 不能靠丢旧消息腾容量 |
| Web 的角色 | 配置、诊断和接管的备用平面 | 不是使用 Slack Agent 的必经工作站 |

## 系统边界

```text
Slack member
    │ message / action
    v
Slack App / Bot
    │
    v
mohist-slack ── Connection boundary ── Agent API ── Agent / Job / Session ── Runner
 (stateless)          │
                      └── provider inbox / conversation mapping / outbound outbox
```

| 组件 | 负责 | 不负责 |
|---|---|---|
| Slack | 成员身份、频道和消息交互、事件与回复传输 | Agent 配置、执行和工作结果 |
| `mohist-slack` | Slack wire protocol 与规范化 Connection command / delivery intent 之间的翻译 | 持久状态、thread 归属判断、运行 Agent、裁定工作状态 |
| Server Connection boundary | Provider 身份与访问决策、持久入站、conversation mapping、待投递，并调用 Agent API | Slack SDK / wire payload、Agent 执行和结果裁定 |
| Agent API | 统一启动、继续、观察和停止 Agent | Slack mention、thread、成员目录或平台限流 |
| Runner | 按 Mohist 已解析的 Agent 定义执行 | Slack 身份、访问策略和 thread 路由 |

每个 Mohist Server 运行一个 `mohist-slack`，集中承载该 Server 管理的所有 Slack Connection。
每个 Connection 仍使用独立 App / Bot 凭据；共享进程不意味着共享 Bot 身份。

### adapter 为什么无状态

独立进程的理由是语言生态，不是状态归属。一旦 adapter 也持久化 thread 映射和待投递消息，
就出现第二个恢复语义、第二套备份对象，以及「Server 认为已发、adapter 认为未发」这类只能靠
对账解决的问题。所以：

- **入站**：adapter 把 Slack 事件转成带稳定 provider 身份的规范化 envelope，交给 Connection
  boundary。Server 快速判断是否忽略、拒绝或持久接纳到 provider inbox；得到确定结果后 adapter
  才向 Slack 确认，不等待 thread 历史、附件下载或 Agent API 完成。Server 不可达或结果未知时
  不确认，由 Slack 以同一身份重投。Slack ack 只表示 Mohist 持久接管了 provider 事件，不等于
  用户输入已经成为 SessionInput；后者仍由 Bot 的接受回复明确表达。
- **出站**：Server 持有有界的 delivery intent，不保存 Slack wire payload。adapter 领取一条、
  渲染发送并回报结果；发送结果无法确认时由 Server 记录并展示，不在 adapter 侧留悬空状态。
- **重启**：adapter 不重建本地状态；重连后继续领取 Server 中尚未收敛的投递。

Server 不可用时 adapter 不缓存事件。两者属于同一个 self-host 安装和信任域，但可以独立重启；
adapter 本地缓存无法把消息变成已接受的 Agent 输入，只会新增另一套恢复语义。此时依赖 Slack
自身的重投窗口，超出窗口的消息由用户重发——这一点对用户可见，见产品文档。

adapter 只限制瞬时并发，不拥有持久队列或产品级背压。Server 在确认入站事件前判断 provider
inbox 容量，之后再按 Agent API 的 Session 输入容量接受或拒绝用户输入。出站 outbox 同样有界：
尚未发送的可替代进度可以合并为最新状态，最终结果、明确失败和需要用户操作的消息不能静默丢弃；
无法继续容纳这类消息时，Connection 进入 Degraded（Backpressured）并停止接受新的 Slack 输入。

## Connection 在领域中的位置

AgentConnection 属于 Agent 域：它的绑定、访问策略和生命周期是持久的、面向产品的行为。
Provider inbox、Slack conversation mapping 与待投递记录是 Server infrastructure 持有的集成记录，
不是 AgentConnection 或 AgentSession 的业务事实。Socket 连接、当前请求和发送中的调用才是
adapter 可以持有的瞬时协议状态。

Connection 引用一个 Agent，但不复制或修改它的执行定义；接入 Slack 不给 Mohist Agent 增加任何
provider 字段。Connection 确认 workspace、App 和 Bot 后不能改绑到另一个 Agent 或 workspace。

一个 Connection 同时表达四类互不替代的事实：外部安装是否完成（Setup progress）、操作者希望它
Enabled 还是 Disabled（Desired state）、Slack 侧当前是否健康（Connection health）、被绑定的 Agent
是否具备执行配置（Agent Readiness）。不能用一个 `Connected` 覆盖这四类事实——Connection 可以已经
连接但 Agent 仍 Needs setup，Agent 也可以 Ready 而 Slack 侧暂时离线。

产品面可以分别读出这四类事实，但不能把它们做成四个互相竞争的总状态。Connection 汇总区每次
只突出一个当前状态和唯一下一步。

## Session 边界的取舍

Buzz 以 channel 复用 Agent Session，因为 channel 是它自身的持续协作边界。Slack 的消息体验把一次
对话组织为 thread，因此 Mohist 选择 `Agent + thread` 作为 Session 边界，而不是让整个 channel 永久
共享上下文。

由此派生两条必须成立的规则：

- 一个 thread 可以同时有多个 AgentSession，每个 Agent 各自拥有映射和上下文；第一次在已有 thread
  中提及新 Agent，不切换也不污染原 Agent 的 Session。
- 归属无法判断时不启动工作。一条消息提及多个 Mohist Bot、或 thread 已绑定多个 Bot 而回复未提及
  目标，都要求用户明确选择，而不是猜。

DM 是这条规则的例外场景：Slack 私聊里没人用 thread，把每条消息都当作新工作会把连续两句话拆成两
项工作。Server 因此为每个 Connection 的 DM conversation 记录一个 current AgentSession；普通消息
继续它，明确的 **New task** 操作才建立并切换到新 Session。切换不取消旧工作，旧工作迟到的回复必须
带上可辨认的 Job / Session 身份，不能被误认成 current Session 的结果。

不同 Mohist Server 之间不共享 thread 路由，因此第一版不承诺协调同一 workspace 中由不同 Server
管理的多个 Bot。

## 可靠性契约

Slack 到 adapter 是 at-least-once 的外部传输，不能宣称端到端 exactly-once。Mohist 的目标是：重复
事件不重复产生领域效果，已确认回复不重复发送，无法确认时把不确定性暴露出来。

- 去重发生在 Server：已经成为输入的同一个 Slack 消息身份始终回到同一个 SessionInput。
- provider inbox 与 SessionInput 分别去重；请求结果丢失后重投不会重复接纳事件或输入。
- 已被 Mohist 接受的输入是 SessionInput，不能被 drop-oldest 之类策略删除。容量不足时拒绝新输入。
- 出站 outbox 有界；可替代的中间进度可以合并，最终结果、明确失败和用户操作不能静默丢弃。
- Slack 投递失败不改变 AgentJob 或 AgentTurn 的结果。执行结果的裁判只有 Server。
- 长时间离线可能超过 Slack 的事件保留窗口，因此不承诺补回所有消息；恢复后必须显示可能存在缺口。

## 安全边界

- 第一版使用 self-host 私有 Slack App 和 Socket Mode，不要求 Mohist 暴露公共入站端点。
- `mohist-slack` 是高权限本机组件，与 Mohist Server 同信任域部署；它只获得调用固定 Connection、
  读取结果和回送消息所需的权限。
- App 与 Bot 凭据由 Server 加密保存，不进入 Agent Instructions、transcript、日志或客户端可见状态。
- 成员校验以 Slack 的稳定 workspace 身份为准，不以显示名、头像或消息文本判断授权。
- 频道调用权实质等于借用 Agent 已配置的执行能力（仓库写入、工具、凭据）。Access policy 因此是
  权限决策，不是便利开关；导入的 thread 历史同样是不可信输入，其影响上限由 Agent 配置决定。
- 每次调用记录 workspace、conversation 和成员身份用于审计，但这些身份不自动成为 Mohist 管理员。

第一版不建设公开 App Marketplace、多租户托管、计费或跨组织身份联邦。那些需求会改变安装、授权和
运营模型，应作为独立产品阶段设计。

## 非目标

- 不让 Slack Bot 运行 Agent Runtime 或拥有另一份 Agent 配置。
- 不让 adapter 持有任何需要备份或恢复的状态。
- 不在 Slack 中复制 Agent 编辑器、Workflow 看板或完整诊断工作台。
- 不让共享 Bot 根据自然语言猜测目标 Agent。
- 第一版不做 Slack 原生 Agent 体验（Agent Messages、Agent Home、流式回复）。
- 本文不固定 API 路径、存储字段、锁和租约、Slack SDK 版本或精确重试时间。

## 实装差距与顺序

实施顺序遵循依赖关系和可独立验证的产品价值：

1. 完成 Agent 从 Web / CLI 独立启动、继续、观察和停止的统一语义。
2. 建立 AgentConnection、无状态 adapter 与可恢复 Setup，但不同时追求所有 Slack 表面。
3. 先交付 Owner-only DM 垂直路径，证明真实 Agent 可从 Slack 使用。
4. 再加入频道、thread、多 Agent 路由、访问策略、附件和故障恢复。

当前进度：第 2、3 步已落地——AgentConnection 领域对象（Setup progress、Desired state、
Connection health、Agent Readiness 四类事实分离）、无状态 `mohist-slack` adapter、可恢复
Setup、Server 持有的 provider inbox / conversation mapping / outbound outbox、Owner-only
DM 垂直路径均已实装，真实 Agent 已可从 Slack 私聊使用。第 1 步的跨入口契约仍未完整（见
[`agent-api.md`](agent-api.md)）。第 4 步尚未开始：当前仅 Owner 可调用、无访问策略；无
频道与 thread 路由、多 Agent 归属判定；无附件边界。

Slack 原生 Agent 体验是后续阶段：它换的是 Slack 侧的入口和呈现，不改变 Agent 能力、执行结果或
本文的任何边界。它会引入不可回退的 App 类型选择，因此要等 Standard Bot 路径被真实使用验证之后
再评估。

每一步都必须保持 Slack 路径最终调用 Agent API。若某项 Slack 能力要求 adapter 解析 Runner 日志、
覆盖 Agent 配置或直接读写 Mohist 数据库，说明前置边界仍未完成，应先修正 Connection boundary
或 Agent API。
