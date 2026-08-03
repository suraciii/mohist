---
status: wip
---

# Slack

Slack 集成把一个已经配置好的 Mohist Agent 作为独立身份接入一个 Slack workspace。Slack
是交互入口，Mohist 仍是 Agent、工作、会话和结果的权威。

产品行为——接入条件、Setup 步骤、访问策略、线程用法、回复呈现、生命周期异常——全部由
[`../docs/slack.md`](../docs/slack.md) 定义，本文不复述。统一调用边界见
[`agent-api.md`](agent-api.md)。本文只记录组件边界与必须长期成立的取舍。

本文分两层：**数据面**（Connection ↔ 一个 Bot 的运行通道）随 Epic #61 已落地，不替换；
**控制面**（workspace 级 Manager 安装/运维每个 Agent App）是新增工作，分切片落地。

## 核心决策

| 主题 | 决策 | 理由 |
|---|---|---|
| Agent 与 Slack 的关系 | Agent 先独立可用；Connection 只是同一个 Agent 的外部入口 | Slack 不能成为 Agent 能工作的前提 |
| Slack 身份 | 一个 Agent 在一个 workspace 中对应一个独立 App / Bot | 用户看到谁就知道调用谁，不用共享 Bot 猜目标 Agent |
| 安装权威 | Server 是 Manager 控制面的状态权威；`mohist-slack` 只做 Socket wire protocol | App 创建、OAuth、安装状态都是需要恢复的产品事实，必须落在 Server |
| `mohist-slack` 独立进程 | 是，但只是工具链选择 | Slack 的一等客户端在 Node，与 runner 同一套 TS 工具链；.NET 侧要自维护 Socket Mode 与事件模型 |
| adapter 是否持久化 | 否 | 进程边界不等于状态边界；Server 已经是唯一状态权威 |
| 入站、对话映射与出站投递 | Server 持有 | 与 Session 落同一个备份边界，消除双权威与跨进程结果未知 |
| 出站投递通道 | 同一份 outbox，按 Connection 的 transport 选唯一 executor | 不建第二套 HTTPS outbox；Socket 与 HTTPS 不能竞争同一条投递 |
| Agent 配置 | Connection 不保存另一份 Instructions、Runtime、Model 或 Skills | 执行定义只有一份 |
| 访问控制 | Connection 只决定谁能调用，不削减或扩张 Agent 已配置的执行权限 | 调用范围与执行能力是两件事 |
| 对话映射 | 频道根提及建立 Session、thread 回复继续；DM 普通消息继续 current Session，New task 才切换 | 遵循两个 Slack 场景各自的对话习惯 |
| 可靠性 | Slack 允许重复投递；Mohist 去重并保留已接受输入 | 不能靠丢旧消息腾容量 |
| 子 App 凭据来源 | 托管路径全凭 API 取得；本机路径的 App-level token 必须人工生成一次 | Slack 不经 API 返回 App-level token，公开接口也没有生成/读取能力 |
| Web 的角色 | 配置、诊断和接管的备用平面 | 不是使用 Slack Agent 的必经工作站 |
| Manager 的形态 | Manager 绑定一个名为 `mohist-slack` 的内置 Mohist Agent：预置 Instructions 与 Slack 管理 Skill，经统一 Agent 执行路径运行，管理操作落在既有 API/CLI 资源上 | 能力只在 API/CLI 一份；对话是自然语言界面，不发明第二份管理语义；Manager 自身走统一执行路径，不多一条执行分支 |
| 对话式创建 Agent | Manager 最多追问名字与日常职责，用默认配置直接创建 Agent 并引导挂载 | Manager 私聊已是授权边界，不需要额外 draft 审核态 |
| 回复位置 | Server 决定投递目标，并随输入把回复锚点（thread root / 触发消息 / DM）注入给 Agent | 不让模型凭记忆猜 thread；锚点是系统事实不是模型判断 |
| 中断语义 | 执行中到达的同会话新输入默认 Steer（并入当前 Turn 或排队等待）；只有显式 Stop 是 Interrupt | 与 SessionInput / AgentTurn 模型一致，新消息不意外打断长任务 |
| 协作规范 | 以内置 Skill 随接入注入：禁空洞确认、完成委派回调 @委派者、沉默为默认、回复自包含、不猜回复位置 | 行为规范可查看、随产品演进，不硬编码进 adapter 或渲染层 |
| 过程透明 | Slack 只承载 liveness 与最终答案；过程透明指向 Web 会话时间线，Open in Mohist 直达 Session 时间线 | 频道不刷屏与 owner 可看全程是两个信号，各归其位 |

## 系统边界

```text
Slack member
    │ message / action
    v
Slack App / Bot
    │
    v  (Socket Mode 出站连接  或  HTTPS Events 入站签名)
mohist-slack / Server ingress ── Connection boundary ── Agent API ── Agent / Job / Session ── Runner
                                  │
                                  └── provider inbox / conversation mapping / outbound outbox

Manager control plane（Server 内，独立于数据面）
    │ manages
    v
SlackWorkspaceEnrollment ── manages ──> ManagedSlackChildApp（每个受管 Agent App）
                                              │ references
                                              v
                                        AgentConnection（Agent + team + app + bot + 访问/启停）
```

| 组件 | 负责 | 不负责 |
|---|---|---|
| Slack | 成员身份、频道和消息交互、事件与回复传输 | Agent 配置、执行和工作结果 |
| `mohist-slack` | Slack wire protocol 与规范化 Connection command / delivery intent 之间的翻译 | 持久状态、thread 归属判断、运行 Agent、裁定工作状态、App 创建/OAuth |
| Server Connection boundary（数据面） | Provider 身份与访问决策、持久入站、conversation mapping、待投递，并调用 Agent API | Slack SDK / wire payload、Agent 执行和结果裁定 |
| Server Manager control plane | workspace enrollment、受管子 App 的外部生命周期/授权/manifest/transport/凭据引用/审计 | Agent 执行、thread 归属、wire protocol |
| Agent API | 统一启动、继续、观察和停止 Agent | Slack mention、thread、成员目录或平台限流 |
| Runner | 按 Mohist 已解析的 Agent 定义执行 | Slack 身份、访问策略和 thread 路由 |

每个 Mohist Server 运行一个 `mohist-slack`，集中承载该 Server 管理的所有 Slack Connection。
每个 Connection 仍使用独立 App / Bot 凭据；共享进程不意味着共享 Bot 身份。Manager 控制面
在 Server 内，不依赖 `mohist-slack`；它创建的子 App 在运行期由数据面消费。

### Manager 的对话形态

Manager 控制面对用户呈现为 Slack 中的 **Mohist App**，其实体是一个名为 `mohist-slack`
的内置 Mohist Agent：Server 级保留名称，随 `mo slack setup` 确保存在，不占用 Project 中
用户可命名的空间，不可被普通归档或删除。它预置 Instructions 与 Slack 管理 Skill，经统一
Agent 执行路径运行；管理操作全部落在既有资源上
（Agent、AgentConnection、SlackWorkspaceEnrollment、ManagedSlackChildApp），不产生第二份
管理语义，也不新增执行路径。

注意 `mohist-slack` 同时是 adapter 进程的名称：同名不同物——一个是 Slack 协议收发的
本机服务，一个是 Mohist App 背后的管理 Agent。二者同属 Slack 集成，命名共享不造成
实现耦合。

Mohist App 的 Slack 收发复用数据面：主 App 与 Agent App 一样经 adapter / ingress / outbox
流转，但它的访问决策固定为「有权管理目标资源的 Mohist 操作者」，不使用普通 Connection 的
Owner / Allowlist / Anyone 策略。永久删除 Slack App 等高危动作不出现在对话中，只在 Web
与 CLI 以显式确认完成。对话式创建 Agent 直接用默认配置创建真实 Agent：能驱动 Manager 的
私聊操作者本身已是授权边界，不再引入 draft 审核态。

Manager 的每条私聊先进入标准 Agent Session/Turn。需要查询或改变资源时，内置 Agent 只能在
自己的终态输出中请求受限的 Manager tool；Server 从该 Session 的不可变 Slack 来源恢复操作者，
重新验证目标资源，再委托既有 application service 或 store。工具结果作为同一 Session 的后续
输入交还给 Agent 生成自然语言回复。模型文本既不能绕过授权，也不能把未经 Server 确认的状态
宣布为就绪。

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
provider 字段。

### 分阶段绑定（Manager 路径）

Manager 路径要求 Connection 在子 App 创建**之前**就存在，作为安装记录的稳定目标；OAuth
校验成功后再把外部 App/Bot 身份补上去。现有实现不能直接支持这一点，必须改不变量：

- `AgentId + WorkspaceTeamId` 在 Connection 创建后不可变。
- `AppId + BotUserId` 只允许从「都空」**原子地**变为「都非空」**一次**；之后三者（team、app、bot）
  都不可改绑。
- 禁止半绑定（只写其中一个）、禁止 team 改绑、禁止二次 app/bot 改绑。
- 同一 Project/Agent/team 仍最多一个未删除 Connection。

当前代码与此冲突的具体点（实现者须修改）：

- `HasBoundIdentity`（`Agent/Services/AgentConnectionStore.cs:278-286`）只要 workspace/app/bot
  任一非空就视为「已绑定」，于是 Manager 路径先固定 team 后补 app/bot 会被当作 immutable binding
  拒绝。需改成：只有 team 固定、app/bot 仍空时，允许一次原子补齐。
- `ImmutableBindingFields`（同文件 `:12-20`）把 `appId`/`botUserId` 与 `workspaceTeamId` 一起列为
  不可变；需区分「创建即固定」与「一次性补齐」。
- `CreateAsync`（`:128-142`）以 `WorkspaceTeamId` 判重。Manager 路径不得用空 team 创建多个无法
  区分目标 workspace 的 pending Connection：team 必须在创建时即由 enrollment 确定为真实值。
- `BindSlackIdentityAsync`（`:202-240`）是当前「一次性绑定全部身份」的路径；新模型把它收敛为
  「补齐 app+bot」的窄命令，并在补齐前由 Connection 重新校验 team 一致与唯一性。

一个 Connection 同时表达四类互不替代的事实：外部安装是否完成（安装进度）、操作者希望它
Enabled 还是 Disabled（Desired state）、Slack 侧当前是否健康（Connection health）、被绑定的 Agent
是否具备执行配置（Agent Readiness）。不能用一个 `Connected` 覆盖这四类事实——Connection 可以已经
连接但 Agent 仍 Needs setup，Agent 也可以 Ready 而 Slack 侧暂时离线。

产品面可以分别读出这四类事实，但不能把它们做成四个互相竞争的总状态。Connection 汇总区每次
只突出一个当前状态和唯一下一步。

## Manager 控制面

控制面是 Server 内、Slack integration supporting context 的两个独立聚合，不属于 Agent 域，也不
是 `mohist-slack` 的职责。它们持有外部 App 的持久产品事实；数据面（inbox/mapping/outbox）仍是
Server infrastructure 的集成记录，二者不混。

### SlackWorkspaceEnrollment

workspace 级聚合。key **默认不带 Project**：一个 workspace 的 Manager 是 Server 安装级控制面，
多个 Project 可引用同一个 enrollment。若产品明确要 Project 隔离，需在产品 spec 里先改成
「每 Project enrollment」，不要由表结构偶然决定。

它拥有：

- 稳定 `team_id`、Manager 外部身份、enrollment lifecycle；
- Manager 能力（能否执行子 App 管理）与最后验证事实、plan/容量诊断；
- Manager credential 引用（**不保存明文**，见「安全边界」）；
- Mohist 管理操作者触发的审计事实。

它**不**拥有 Agent、Connection 或子 App，也不把 Slack 成员变成 Mohist 管理员。

### ManagedSlackChildApp

每个受管 Agent App 一个独立聚合（命名建议 `ManagedSlackChildApp`，不要叫 `Install`——后者会把
「App 创建」「workspace OAuth installation」「Mohist binding」混成一件事）。它引用目标
`AgentConnectionId`，但不是 Connection 的子对象；两者不能在同一事务修改。Connection 仍是
Agent/workspace/provider identity、访问策略和启停生命周期的权威；ChildApp 是 Slack 外部 App
生命周期与管理事实的权威。

它拥有：

- `enrollment_id`、稳定 child ID、外部 `app_id`；
- desired / applied manifest version + canonical hash 与已验证 scopes；
- App 创建/删除事实、OAuth/审批事实、transport 配置事实；
- operation fence、unknown outcome、错误分类、审计。

**这里不另建 durable process-manager 聚合。** Slack create/delete 是 ChildApp 自身的一次外部
副作用，fence 就保存在 ChildApp 聚合内。架构对 process manager 的限制是「只存未决命令，不存
业务事实」（[`architecture.md`](architecture.md)「持久化应用协调者」节）；ChildApp 恰恰必须保存
业务事实，因此不能套成该 process manager。跨 ChildApp → Connection 的绑定用
「ChildApp 提交事实 → durable handler → Connection 幂等命令」推进，不跨聚合事务。

### 四轴状态 + 唯一 next action

ChildApp 状态**不要做成一个巨型 enum**，至少分四轴，并派生唯一 next action：

1. **app lifecycle**：`not-created` / `creating` / `create-unknown` / `created` / `deleting` /
   `delete-unknown` / `deleted`。
2. **authorization**：`not-started` / `awaiting-user` / `pending-admin` / `authorized` /
   `expired-or-cancelled` / `revoked`。
3. **manifest**：`desired` / `applied` / `drift-known`。
4. **transport readiness**：HTTPS 所需材料与 Socket 所需材料分别计算。**HTTPS 不要求 App-level
   token；Socket 缺 App-level token 时不得 Ready。**

unknown（`create-unknown` / `delete-unknown`）只能由 reconcile 或显式人工裁决离开；进程重启后
**不得自动**再次 create/delete。definite failure 可在同一 child 上生成新 attempt，但不新建
Connection/Bot 目标。OAuth cancel/expiry/pending approval 都 Resume 同一个 child，不新建 Bot。

### 凭据所有权

凭据按真正拥有者寻址，Connection **不拥有或复制**子 App 运行凭据：

- Manager credential → Enrollment 地址；
- child client secret、signing secret、App-level token (`xapp-`)、Bot token (`xoxb-`) → ChildApp 地址；
- Connection 只通过 active ChildApp binding 取得数据面所需凭据。

原因是 remove Connection 默认不删除 Slack App；若凭据继续按 Connection 地址寻址，现有
`AgentConnectionStore.DeleteAsync`（`:243-267`）会删除 App/Bot token，与 ChildApp 可独立保留/删除
的生命周期冲突。当前 `SecretStoreAddress(ProjectId, ConnectionId, Kind)`（
`Infrastructure/Security/Secrets/SecretStoreAddress.cs`）最终应泛化为 typed owner address 或新增
Slack integration secret address；**P0.1 只在 spec/model 层定义引用与所有权，不迁移现有生产
secret 路径。**

OAuth 成功的收敛顺序必须保证：身份先验证；secret durable 后才让 Connection 可用；跨 secret store
与 DB 失败要可恢复，不能出现「Connection 已绑定可用但 token 未落盘」。回调重放必须返回同一结果，
不重复交换/绑定。

### 入站通道选择

一个 Connection 在任一时刻只有**一种** transport 有资格 claim 其 outbox，避免 Socket 与 HTTPS
竞抢。模型只保存 transport kind 与 readiness，不锁死 worker 实现。第一切片不重写现有 Socket
worker；托管路径的 Server delivery executor 在后续切片加入，沿用同一 outbox 与 claim/ack/uncertain
语义。

HTTPS 入站在验证 timestamp、raw-body HMAC（`v0:{ts}:{body}` + HMAC-SHA256 over signing secret +
constant-time compare）后，按已验证的 `api_app_id + team_id` 反查 Connection，再复用现有规范化
ingress、provider inbox、conversation mapping 和 outbox。未知 App/team 只返回 unbound，不按名字路由。

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

Manager 控制面的 create/delete 同样是 at-least-once 外部副作用：重复 attempt 不重复创建/删除 App，
结果未知时暴露为 unknown，靠 reconcile 或人工裁决收敛，见「四轴状态」。

### 状态投影与消息身份

Server 是 AgentSession/AgentTurn 状态的唯一裁判。Slack provider 与 `mohist-slack` adapter 只把
Server 已确认的状态和结果投影到 Slack，不能从 Slack API 的成功响应推断工作成功，也不能读取
Runner 输出来裁定状态。

一个已接受的 Slack 输入使用稳定的 `SlackMessageIdentity` 作为输入身份，并派生一个贯穿整次
工作的 `DispatchRef`。对每个 `DispatchRef`，Server 只允许以下逻辑结果：

- 最多一个可替换的进度投影。创建后保存 provider message identity（目标 conversation 与 provider
  message timestamp）；Working、最新阶段和终态更新都指向同一个身份，不创建第二条进度消息。
- 最多一个终态结果投影。终态结果用稳定的 terminal delivery key 去重；如果已有进度消息，
  Completed、Needs attention 或 Failed 优先原位替换它，否则在同一 thread 发送唯一最终答案。
- 快速工作可以跳过进度消息，只投影 Received reaction 和唯一最终答案。平台不支持在用户原消息
  上加 reaction 时，Received reaction 改投影到唯一进度消息。

Reaction 的默认映射是 `Received=👀`、`Working=⏳`、`Completed=✅`、异常状态 `⚠️`。Reaction
只是 liveness 提示，不是 Session/Turn 事实；reaction 缺失、延迟或成功都不改变 Server 的状态。
Reaction mutation 必须带目标的稳定 provider message identity；状态裁判不放入 provider 或 adapter。

### Delivery intent、claim/ack 与未知结果

Server 为每个逻辑投影持久化一个 `DeliveryIntent`。intent 至少包含 Connection、`DispatchRef`、
目标 conversation/thread、投影类型、稳定去重 key、当前 provider message identity（若已知）和
可安全重放的内容引用。投影类型只有可替换进度、终态结果/明确失败、用户操作和 reaction mutation；
工具调用或 Runner 日志不是用户消息。

intent 的生命周期如下：

1. **Pending**：Server 已持久化投影意图，但没有调用 provider。
2. **Claimed**：一个 adapter 获得该 intent 的短租约。claim 不是发送成功，第二个 adapter 不能同时
   投影同一个 intent。
3. **Delivered/Acked**：provider 返回确定成功，并且 Server 保存了 provider message identity 或
   mutation 已确认。重复 ack 必须幂等。
4. **Retryable**：provider 明确拒绝且可以重试；回到同一个 intent，不能创建新的进度或最终答案。
5. **Uncertain**：请求超时、连接中断或响应无法解析，无法判断 provider 是否已经产生副作用。此时
   禁止盲目再发，必须先用稳定 identity reconcile；只有确认未产生副作用后才允许重试原 intent。
6. **Dead-letter/Needs attention**：经过明确的不可重试失败或人工介入后，保留原 intent、原因和
   可行动下一步；不能把 AgentTurn 的已确认结果改写成 provider 失败。

`chat.update`、reaction add/remove 和进度消息创建都遵守同一套 claim/ack/uncertain 语义。更新
一个已存在的状态消息时，provider message identity 丢失或结果未知，必须先 reconcile；若确认无法
更新，Server 只允许为该 `DispatchRef` 追加一次同 thread 的最终答案。fallback 也有自己的稳定
terminal delivery key，故重试、重连和重复入站不会再追加第二条最终答案。

### 切片责任边界

- **P0-B** 拥有 provider projection contract：稳定 provider message identity、投影去重、可替换
  progress、`chat.update`、reaction add/remove、unknown mutation reconciliation、update failure
  fallback，以及 outbox 的 claim/ack/uncertain 传输语义。B 不决定 AgentSession/AgentTurn 的状态。
- **P0-C** 只接收 Server 已确认的成功、部分完成、取消、阻塞或失败结果，渲染结果优先的文本。
  C 不创建或更新 Slack 消息，不操作 reaction，不重试 provider，也不把 raw tool stream、JSON 或
  隐藏推理交给用户。
- **P0-I** 负责 ingress 到 Agent API、Session/Turn、状态投影和最终结果之间的 orchestration，并
  验证 DM/@mention、thread follow-up、失败、取消、重复投递、update failure fallback、Agent 未就绪
  和 Connection Disabled。I 不绕过 B 的 provider mutation contract。

Connection Disabled 时，adapter/Server 仍必须在传输层确认 Slack event 已处理，并记录审计后丢弃
该事件。这个传输层确认不代表连接层接纳：连接层不得把事件创建或接纳为 `SessionInput`、
`AgentJob` 或任何 `DeliveryIntent`，也不得让它们在稍后重新启用时被重放。已经接受的 Agent 工作
继续由 Mohist 裁定，但禁用期间 adapter 不得 claim 或发送新的 Slack 回复。重新启用后只补齐仍有效
的当前状态或最终结果，不回放禁用期间已经过期的 Working 进度。Remove binding 与 Permanent delete
仍是两个独立、显式确认的生命周期动作：前者保留 Agent App 管理事实，后者要求无 active binding、
二次确认和审计；两者都不删除 Mohist Agent 或 AgentSession。

## 安全边界

- 本机路径使用 Socket Mode，不要求 Mohist 暴露公共入站端点；托管路径用签名校验替代对 App-level
  token 的依赖，但仍只暴露一个受控入站地址。
- `mohist-slack` 是高权限本机组件，与 Mohist Server 同信任域部署；它只获得调用固定 Connection、
  读取结果和回送消息所需的权限。
- App 与 Bot 凭据、Manager credential、子 App client secret 与 signing secret 由 Server 加密保存，
  **按各自拥有者寻址**（见「凭据所有权」），不进入 Agent Instructions、transcript、日志、客户端可见
  状态、durable row、DTO 或审计序列化。OAuth code 与 state nonce 同样不得落盘明文；state 只存 hash。
- 成员校验以 Slack 的稳定 workspace 身份为准，不以显示名、头像或消息文本判断授权。
- 频道调用权实质等于借用 Agent 已配置的执行能力（仓库写入、工具、凭据）。Access policy 因此是
  权限决策，不是便利开关；导入的 thread 历史同样是不可信输入，其影响上限由 Agent 配置决定。
- 每次调用记录 workspace、conversation 和成员身份用于审计，但这些身份不自动成为 Mohist 管理员。
  Manager 安装者、Agent owner、Connection owner 与普通 caller 是四个不同角色。

第一版不建设公开 App Marketplace、多租户托管、计费或跨组织身份联邦。那些需求会改变安装、授权和
运营模型，应作为独立产品阶段设计。

## 非目标

- 不让 Slack Bot 运行 Agent Runtime 或拥有另一份 Agent 配置。
- 不让 adapter 持有任何需要备份或恢复的状态。
- 不让 Manager 代替 Agent 发送回复，或成为多 Agent 共享的执行身份。
- 不在 Slack 中复制 Agent 编辑器、Workflow 看板或完整诊断工作台。
- 不让共享 Bot 根据自然语言猜测目标 Agent。
- 第一版不做 Slack 原生 Agent 体验（Agent Messages、Agent Home、流式回复）。
- 两条路径都不承诺「零步骤全自动」：安装者都要完成 Slack 安装授权，工作区策略要求时同样要过管理员审批；Socket Mode 在此基础上每个子 App 还需一次手工 App-level token。hosted 路径省去的是手工 token，不是授权或审批。
- 本文不固定 API 路径、存储字段、锁和租约、Slack SDK 版本或精确重试时间。

## P0.1 实施规格（correctness kernel）

本节是 P0.1 切片的硬约束，供实现者与 reviewer 对齐。P0.1 **不接生产网络**：无真实 Slack client、
无 OAuth endpoint、无 HTTPS ingress、无 Manager UI/API、不改 `packages/mohist-slack`、不改
inbox/mapping/outbox 语义。

### 模型不变量

- `SlackWorkspaceEnrollment`：active enrollment 的 `team_id` 唯一；拥有 Manager 身份/能力/lifecycle
  与 credential refs；不带 Project（除非产品 spec 改成每 Project）。
- `ManagedSlackChildApp`：引用 `AgentConnectionId`；拥有四轴状态、desired/applied manifest +
  hash、verified scopes、operation fence、unknown outcome、error class、audit、child secret refs。
- Connection 分阶段绑定：`AgentId + WorkspaceTeamId` 创建即固定；`AppId + BotUserId` 从「都空」
  原子补成「都非空」一次；禁止半绑定、team 改绑、二次 app/bot 改绑。
- ChildApp → Connection：durable fact + idempotent bind，不跨聚合事务；Connection 已删或已绑其它
  身份时，ChildApp 保留可诊断状态，不回滚外部 App。

### DB constraints（兜底，不靠 service 先查后写）

- active enrollment `team_id` 唯一。
- `(team_id, app_id)` 唯一。
- 一个 ChildApp 只绑定一个 Connection。
- 同一 Project/Agent/team 只有一个未删除 Connection。
- Connection 软删；ChildApp 历史引用保留；「移除绑定」不级联删 ChildApp。

### fake app-management port

生产代码只能经一个窄 port 调 Slack create/delete（ArchTest 兜底）。P0.1 只提供 fake 实现，覆盖：

- create 成功 / definite 失败 / **unknown**（超时或 internal_error）；
- delete 成功 / definite 失败 / **unknown**；
- 越权（非 Manager 创建的 App）update/delete 拒绝；
- managed-App 数量上限（`managed_app_limit_reached`）。

fake 不触真实网络；与 fixed `TimeProvider` 一起驱动 application service。

### 测试矩阵（spec + unit，全部走 fake + fixed time）

1. 并发/fence：同 child 双 create 只调 fake 一次；stale attempt 结果不覆盖新 fence；重启后
   `create-unknown` 不自动 create。
2. unknown 对称：create/delete 都有 unknown；只有 reconcile 或显式人工裁决能离开 unknown；definite
   failure 可在同 child 生成新 attempt，不新建 Connection/Bot 目标。
3. staged binding：team reservation 可补 app+bot 一次；半绑定、team/app/bot mismatch、二次改绑全部
   拒绝且无部分写入。
4. 跨聚合收敛：ChildApp 成功事实重投不重复绑定；Connection 已删/冲突时 ChildApp 保留可诊断状态。
5. OAuth：state 用 hash、单次、过期、绑定 child/team/app；callback replay 幂等；cancel/expiry/pending
   approval 恢复同 child；任何 mismatch 不保存 `xoxb-`、不绑定 Connection。本切片不做 callback endpoint，
   但在 model/application service + fake 上证明这些语义。
6. secret 安全：model/store/manifest/error/audit 序列化均不含 plaintext；HTTPS 不要求 `xapp-`，Socket
   未有 `xapp-` 不得 ready。
7. manifest determinism：同输入 canonical bytes/hash 完全相同；字段顺序不影响 hash；
   capability/version 或身份快照变化才形成明确 drift；只输出 live schema，禁止旧 schema 与 Mohist
   metadata 进入 manifest。
8. DB constraints：上述 4 条约束在并发下成立。
9. 生命周期：Disable 只改 Connection；Remove binding 清理数据面但保留 ChildApp/管理事实；Permanent
   delete 需二次确认 + 审计 + 无 active binding，且 definite/unknown outcome 分开。
10. ArchTests：Agent 域不依赖 Slack integration model/port；Slack integration 不依赖
    `packages/mohist-slack`；Enrollment/ChildApp 不进入 Agent/Session aggregate；生产代码只能经
    app-management port 调 Slack create/delete；现有 inbox/mapping/outbox 边界不动。

### 允许 / 禁止

允许：更新 `docs/slack.md`、`design/slack.md`，并同步
`design/architecture.md` / `design/domain-analysis.md`；Server 内新增 Slack-specific、
transport-variant-neutral 的 Enrollment / ChildApp model/store、deterministic manifest generator、
app-management port + fake、`TimeProvider` 驱动的 application service；为 staged workspace reservation
做最小 AgentConnection model/store 变更与迁移。

禁止：改 `packages/mohist-slack` 运行路径；真实 Slack client/OAuth/HTTPS ingress；Manager UI/API；
改 provider inbox/mapping/outbox；把 ChildApp 字段塞进 `SetupProgress` 或 Agent 定义；在 durable
row/DTO/audit/log 保存 plaintext secret/OAuth code/state nonce；为 generator 先迁移现有生产 secret
路径；跨 provider 通用化（这是 Slack-specific、transport-variant-neutral 模型，不是通用 provider
install）。

## 实装差距与顺序

### 当前实装

数据面已具备 `AgentConnection` 的 Setup progress、Desired state、Connection health 与 Agent
Readiness 分离，以及 Server 持有的 provider inbox、conversation mapping 和 outbound outbox。无状态
`mohist-slack` adapter 负责把稳定 delivery identity 投影为 post、update 与 reaction；未知 mutation
会依据该 identity 核对，update 的明确失败只产生一次 fallback。终态 delivery 由 Server 基于 session
和管理员配置的 external web URL 构造 link block，adapter 不解析 Agent 文本为 Slack 控制对象。

Manager 侧已有 Slack-specific Enrollment、ChildApp、claim 与 ManagerActor 边界。operator setup 签发
一次性 claim，Manager ingress 先 durable accept，再按认领的 actor 和目标资源授权。内置
`mohist-slack` Agent 沿用标准 SessionInput、AgentTurn 与 Runner dispatch；受控工具可创建带默认
runtime 的普通 Project Agent 后委托同一 Manager application service 挂载。删除、解除绑定、凭据和
投递重发工具不在 Manager 对话 catalog 中。

普通 Slack 输入拥有不可变的 reply anchor 与协作 Skill，dispatch-only context 不进入 Agent 配置。
普通 follow-up 始终走既有接纳路径；Stop interaction 由 Server 签名、去重并在重读 executing Turn
和 actor 后才调用既有 stop operation。所有这些路径的测试使用 fake port、in-memory store、fixed
`TimeProvider` 与 deterministic runner probe。

### 仍未实装

真实 Slack 子 App 创建、OAuth callback、托管 HTTPS ingress 和本机安装 wizard 尚未接到生产网络。
公开应用市场、多租户托管、跨 Mohist Server 协调、Slack 原生 Agent 入口、App Home 以及完整的
规模化和运维体验仍属于后续阶段。后续能力仍必须经 Agent API 与既有 Connection boundary 进入，
不得让 adapter 解析 Runner 日志、覆盖 Agent 配置或直接写 Mohist 数据库。
