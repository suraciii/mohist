---
status: wip
---

# Slack

Slack 集成把一个已经配置好的 Mohist Agent 作为独立身份接入一个 Slack workspace。Slack
是交互入口，Mohist 仍是 Agent、工作、会话和结果的权威。

产品行为——接入条件、Setup 步骤、访问策略、线程用法、回复呈现、生命周期异常——全部由
[`../docs/slack.md`](../docs/slack.md) 定义，本文不复述。统一调用边界见
[`agent-api.md`](agent-api.md)。本文只记录组件边界与必须长期成立的取舍。

本文分两层：**数据面**（Connection ↔ 一个 Bot 的本机 Socket Mode 运行通道）与
**控制面**（workspace 级 Mohist App 安装/运维每个 Agent App）。两者共享 Server 中的
权威状态，但不共享职责。

## 核心决策

| 主题 | 决策 | 理由 |
|---|---|---|
| Agent 与 Slack 的关系 | Agent 先独立可用；Connection 只是同一个 Agent 的外部入口 | Slack 不能成为 Agent 能工作的前提 |
| Slack 身份 | 一个 Agent 在一个 workspace 中对应一个独立 App / Bot | 用户看到谁就知道调用谁，不用共享 Bot 猜目标 Agent |
| 安装权威 | Server 是 Slack 控制面的状态权威；`mohist-slack` 只做 Socket wire protocol | App 创建、Slack 安装和凭据验证都是需要恢复的产品事实，必须落在 Server |
| `mohist-slack` 独立进程 | 是，但只是工具链选择 | Slack 的一等客户端在 Node，与 runner 同一套 TS 工具链；.NET 侧要自维护 Socket Mode 与事件模型 |
| adapter 是否持久化 | 否 | 进程边界不等于状态边界；Server 已经是唯一状态权威 |
| 入站、对话映射与出站投递 | Server 持有 | 与 Session 落同一个备份边界，消除双权威与跨进程结果未知 |
| 出站投递通道 | 同一份 outbox，只由持有有效 Socket lease 的 adapter executor 领取 | 一个 App 同时只有一个发送者，避免重复投递 |
| Agent 配置 | Connection 不保存另一份 Instructions、Runtime、Model 或 Skills | 执行定义只有一份 |
| 访问控制 | Connection 只决定谁能调用，不削减或扩张 Agent 已配置的执行权限 | 调用范围与执行能力是两件事 |
| 对话映射 | 频道根提及建立 Session、thread 回复继续；DM 普通消息继续 current Session，New task 才切换 | 遵循两个 Slack 场景各自的对话习惯 |
| 可靠性 | Slack 允许重复投递；Mohist 去重并保留已接受输入 | 不能靠丢旧消息腾容量 |
| 运行模式 | 仅本机 Socket Mode；Mohist App 与每个 Agent App 都有独立 Bot/App-level token | self-hosted 不要求公共入站地址，也不引入 Mohist 托管控制面 |
| Agent App 凭据来源 | 纯本机部署由 CLI 受保护地接收安装后的 Bot token 与手工生成的 App-level token | Slack OAuth callback 要求 HTTPS，公开 App 管理响应也不返回可用的 App-level token；不得索取登录 session |
| Web 的角色 | 配置、诊断和接管的备用平面 | 不是使用 Slack Agent 的必经工作站 |
| Mohist App 的形态 | Mohist App 绑定一个名为 `mohist-slack` 的内置 Mohist Agent：预置 Instructions 与 Slack 管理 Skill，经统一 Agent 执行路径运行，管理操作落在同一 application service | 能力只在 Server 一份；CLI 与对话只是两个入口，不发明第二份安装语义 |
| 安装 DSL | `mo slack setup` 安装 workspace 级 Mohist App；`mo slack install-agent <agent>` 安装已有 Agent | `setup-agent` 与 Agent Readiness setup 混淆；`create` 错称在创建 Agent 或 Connection，而用户意图是把 Agent 安装到 Slack |
| 对话式创建 Agent | Mohist App 最多追问名字与日常职责，用默认配置直接创建 Agent 并引导安装到 Slack | Mohist App 私聊已是授权边界，不需要额外 draft 审核态 |
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
    v  (Socket Mode，由本机主动建立)
mohist-slack ── Server ingress ── Connection boundary ── Agent API ── Agent / Job / Session ── Runner
                                  │
                                  └── provider inbox / conversation mapping / outbound outbox

Slack control plane（Server 内，独立于 wire adapter）
    │ manages
    v
SlackWorkspaceEnrollment ── manages ──> ManagedSlackAgentApp（每个受管 Agent App）
                                              │ references
                                              v
                                        AgentConnection（Agent + team + app + bot + 访问/启停）
```

| 组件 | 负责 | 不负责 |
|---|---|---|
| Slack | 成员身份、频道和消息交互、事件与回复传输 | Agent 配置、执行和工作结果 |
| `mohist-slack` | Slack Socket Mode protocol 与规范化 ingress / delivery intent 之间的翻译；维护 Server 授予的短 lease | 持久状态、thread 归属判断、运行 Agent、裁定工作状态、App 创建/安装 |
| Server Connection boundary（数据面） | Provider 身份与访问决策、持久入站、conversation mapping、待投递，并调用 Agent API | Slack SDK / wire payload、Agent 执行和结果裁定 |
| Server Slack control plane | workspace enrollment、Mohist App 与 Agent App 的外部生命周期/授权/manifest/凭据引用/审计 | Agent 执行、thread 归属、wire protocol |
| Agent API | 统一启动、继续、观察和停止 Agent | Slack mention、thread、成员目录或平台限流 |
| Runner | 按 Mohist 已解析的 Agent 定义执行 | Slack 身份、访问策略和 thread 路由 |

每个 Mohist Server 运行一个 `mohist-slack`，集中承载工作区 Mohist App 和全部 Agent App 的
Socket 连接。每个 App 仍使用独立凭据；共享进程不意味着共享 Bot 身份。Server 控制面不把
状态权威交给 adapter；App 就绪后由 adapter 取得短 lease 与运行凭据，建立或恢复 Socket。

### Mohist App 的对话形态

Slack 控制面对用户呈现为 Slack 中的 **Mohist App**，其实体是一个名为 `mohist-slack`
的内置 Mohist Agent：Server 级保留名称，随 `mo slack setup` 确保存在，不占用 Project 中
用户可命名的空间，不可被普通归档或删除。它预置 Instructions 与 Slack 管理 Skill，经统一
Agent 执行路径运行；管理操作全部落在既有资源上
（Agent、AgentConnection、SlackWorkspaceEnrollment、ManagedSlackAgentApp），不产生第二份
管理语义，也不新增执行路径。

注意 `mohist-slack` 同时是 adapter 进程的名称：同名不同物——一个是 Slack 协议收发的
本机服务，一个是 Mohist App 背后的管理 Agent。二者同属 Slack 集成，命名共享不造成
实现耦合。

Mohist App 的 Slack 收发复用数据面：主 App 与 Agent App 一样经 adapter / ingress / outbox
流转，但它的访问决策固定为「有权管理目标资源的 Mohist 操作者」，不使用普通 Connection 的
Owner / Allowlist / Anyone 策略。永久删除 Slack App 等高危动作不出现在对话中，只在 Web
与 CLI 以显式确认完成。对话式创建 Agent 直接用默认配置创建真实 Agent：能驱动 Mohist App 的
私聊操作者本身已是授权边界，不再引入 draft 审核态。

Mohist App 的每条私聊先进入标准 Agent Session/Turn。需要查询或改变资源时，内置 Agent 只能在
自己的终态输出中请求受限的管理 tool；Server 从该 Session 的不可变 Slack 来源恢复操作者，
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

### 分阶段绑定（install-agent 路径）

`install-agent` 路径要求 Connection 在 Agent App 创建**之前**就存在，作为安装记录的稳定目标；
安装凭据校验成功后再把外部 App/Bot 身份补上去：

- `AgentId + WorkspaceTeamId` 在 Connection 创建后不可变。
- `AppId + BotUserId` 只允许从「都空」**原子地**变为「都非空」**一次**；之后三者（team、app、bot）
  都不可改绑。
- 禁止半绑定（只写其中一个）、禁止 team 改绑、禁止二次 app/bot 改绑。
- 同一 Project/Agent/team 仍最多一个未删除 Connection。

当前实现已按此模型落地（`Agent/Services/AgentConnectionStore.cs`）：

- `CreateStagedAsync` 创建时要求真实 `WorkspaceTeamId`（由 enrollment 确定）且 app/bot 都为空，
  `CreateAsync` 拒绝带 app/bot 的创建。
- `BindSlackIdentityAsync` 已收敛为「补齐 app+bot」的窄命令：补齐前重新校验半绑定、team 一致
  与唯一性，原子地把 `AppId + BotUserId` 从「都空」补成「都非空」一次；同一身份重投幂等，
  改绑被拒。
- `HasBoundIdentity` 要求 app/bot 都非空才算已绑定；`UpdateAsync` 的 `ImmutableBindingFields`
  仍把绑定字段排除在通用更新之外，「创建即固定」与「一次性补齐」由窄命令与通用更新分离。

一个 Connection 同时表达四类互不替代的事实：外部安装是否完成（安装进度）、操作者希望它
Enabled 还是 Disabled（Desired state）、Slack 侧当前是否健康（Connection health）、被绑定的 Agent
是否具备执行配置（Agent Readiness）。不能用一个 `Connected` 覆盖这四类事实——Connection 可以已经
连接但 Agent 仍 Needs setup，Agent 也可以 Ready 而 Slack 侧暂时离线。

产品面可以分别读出这四类事实，但不能把它们做成四个互相竞争的总状态。Connection 汇总区每次
只突出一个当前状态和唯一下一步。

## Slack 控制面

控制面是 Server 内、Slack integration supporting context 的两个独立聚合，不属于 Agent 域，也不
是 `mohist-slack` 的职责。它们持有外部 App 的持久产品事实；数据面（inbox/mapping/outbox）仍是
Server infrastructure 的集成记录，二者不混。

### SlackWorkspaceEnrollment

workspace 级聚合。key **默认不带 Project**：一个 workspace 的 Mohist App 是 Server 安装级控制面，
多个 Project 可引用同一个 enrollment。若产品明确要 Project 隔离，需在产品 spec 里先改成
「每 Project enrollment」，不要由表结构偶然决定。

它拥有：

- 稳定 `team_id`、Mohist App 外部身份、enrollment lifecycle；
- Mohist App 能力（能否执行 Agent App 管理）与最后验证事实、plan/容量诊断；
- Mohist App 凭据引用（**不保存明文**，见「安全边界」）；
- Mohist 管理操作者触发的审计事实。

它**不**拥有 Agent、Connection 或 Agent App，也不把 Slack 成员变成 Mohist 管理员。

### ManagedSlackAgentApp

每个受管 Agent App 一个独立聚合。`install-agent` 是应用层动作，不是聚合名；安装动作会协调
「App 创建」「workspace 安装授权」「Mohist binding」，而聚合只保存 Agent App 自己的外部事实。
`ManagedSlackAgentApp` 引用目标
`AgentConnectionId`，但不是 Connection 的子对象；两者不能在同一事务修改。Connection 仍是
Agent/workspace/provider identity、访问策略和启停生命周期的权威；AgentApp 是 Slack 外部 App
生命周期与管理事实的权威。

它拥有：

- `enrollment_id`、稳定 Agent App ID、外部 `app_id`；
- desired / applied manifest version + canonical hash 与已验证 scopes；
- App 创建/删除事实、安装/审批事实、Socket 配置事实；
- operation fence、unknown outcome、错误分类、审计。

**这里不另建 durable process-manager 聚合。** Slack create/delete 是 AgentApp 自身的一次外部
副作用，fence 就保存在 AgentApp 聚合内。架构对 process manager 的限制是「只存未决命令，不存
业务事实」（[`architecture.md`](architecture.md)「持久化应用协调者」节）；AgentApp 恰恰必须保存
业务事实，因此不能套成该 process manager。跨 AgentApp → Connection 的绑定用
「AgentApp 提交事实 → durable handler → Connection 幂等命令」推进，不跨聚合事务。

### 四轴状态 + 唯一 next action

AgentApp 状态**不要做成一个巨型 enum**，至少分四轴，并派生唯一 next action：

1. **app lifecycle**：`not-created` / `creating` / `create-unknown` / `created` / `deleting` /
   `delete-unknown` / `deleted`。
2. **authorization**：`not-started` / `awaiting-user` / `pending-admin` / `authorized` /
   `expired-or-cancelled` / `revoked`。
3. **manifest**：`desired` / `applied` / `drift-known`。
4. **Socket readiness**：Bot token 与 App-level token 已持久化且身份验证通过、adapter lease
   存活。缺任一运行凭据时不得 Ready。

unknown（`create-unknown` / `delete-unknown`）只能由 reconcile 或显式人工裁决离开；进程重启后
**不得自动**再次 create/delete。definite failure 可在同一 AgentApp 上生成新 attempt，但不新建
Connection/Bot 目标。安装取消、授权过期或待审批都 Resume 同一个 AgentApp，不新建 Bot。

### 凭据所有权

凭据按真正拥有者寻址，Connection **不拥有或复制**Agent App 运行凭据：

- Mohist App runtime credentials → Enrollment 地址。`ManagerCredentialRef` 是 enrollment 持久化的
  opaque 引用，不由 CLI 或 HTTP caller 提供地址组成部分；Bot token 与 App-level token 使用同一
  owner ref 下不同的 `SecretKind`，不得合并为一个无类型 secret。
  `mo slack setup` 是唯一常规 provision/repair/rotation 入口。Server 根据活动 enrollment 的 workspace team
  反查已持久化引用；重复 setup 恢复同一记录，不新建 Mohist App。
- Agent App client/signing secret、App-level token (`xapp-`)、Bot token (`xoxb-`) → AgentApp 地址；
- Connection 只通过 active AgentApp binding 取得数据面所需凭据。

原因是 remove Connection 默认不删除 Slack App；若凭据继续按 Connection 地址寻址，现有
`AgentConnectionStore.DeleteAsync` 会删除 App/Bot token，与 AgentApp 可独立保留/删除
的生命周期冲突。当前 `SecretStoreAddress(ProjectId, ConnectionId, Kind)`（
`Infrastructure/Security/Secrets/SecretStoreAddress.cs`）最终应泛化为 typed owner address 或新增
Slack integration secret address；**P0.1 只在 spec/model 层定义引用与所有权，不迁移现有生产
secret 路径。**

secret provision endpoint 只允许 operator-authenticated loopback 请求，body 只能表达目标安装和
该步骤规定的凭据。caller 不能提交 credential ref、secret kind 或 secret address。
凭据必须来自 CLI 隐藏输入，或来自用户专属、受保护且非符号链接的文件；HTTP response、status、错误、
日志、审计 DTO 和文档示例都不包含凭据。Server 只返回非敏感的 workspace 与 provisioned confirmation。
状态分别暴露 Bot/App-level credential 是否已 provisioned 与验证事实；只有两者都有效且 Socket
hello 已确认时才允许 Mohist App 进入 ready。

安装凭据提交的收敛顺序必须保证：Bot/workspace 先验证；App-level token 只能以 unverified candidate
写入拥有者的 secret address，并且 validation lease 不能接纳业务流量；Socket App 身份验证完成后才
允许绑定和 runtime lease。跨 secret store 与 DB 失败要可恢复，不能出现「Connection 已绑定可用但
token 未落盘」。重复提交同一组已验证凭据返回同一结果，不重复绑定；不同 App/team/Bot 的候选
secret 必须删除并保持不可用。

### App 供给凭据（Slack Configuration Token）

`SlackAppManagementPort` 的正式实现以一组**工作区级 Configuration access/refresh token pair**
为供给凭据，负责 Mohist App 与全部 Agent App 的 manifest create/update 与外部生命周期查询。
它与 Mohist App 运行凭据（Slack 安装后得到的 bot token）是两枚不同凭据：前者授予「创建/维护
App」，后者授予「以 Bot 身份收发消息」；寻址、轮换、失效互不影响，不得混用或互相推导。

- **所有权与寻址**：Configuration token pair 是「提供者身份 × 工作区」级的外部凭据，归
  Enrollment 地址保存（与 Mohist App 凭据引用同址，作为其供给部分）。DB 只存引用与
  元数据（提供时间、来源、轮换代数）；序列化、审计、错误、日志均不得含明文。
- **供给路径只有一条**：setup 引导用户在 Slack App 管理页生成 Configuration access token 与
  refresh token，并以受保护输入提交一次（不回显、可撤销、可重供）。不假设用户环境存在 Slack 官方
  CLI 或任何其他工具；产品文档与 CLI 引导均不出现它们。
- **轮换**：access token 到期前由 Server 用 refresh token 调用 tooling token rotation；响应中的
  新 access/refresh pair 与 provider 返回的 `team_id` 必须原子替换旧 pair。该 API 的结果携带下一枚
  一次性 refresh token，网络结果未知时不得盲目重试；标记 `credential-rotation-unknown`，要求用户
  重新提供一组 pair。轮换失败只降低 App 管理能力，不中断已经安装 App 的 Socket 数据面。
- **失效语义**：Slack 侧撤销或过期表现为 App 管理调用鉴权失败；Enrollment 的 Mohist App
  能力进入 Degraded，next action 为「重新运行 `mo slack setup` 供给 App 供给凭据」。已有 AgentApp 与
  Connection 的数据面（bot token）不受影响，但新建、续装与 manifest 修复全部阻塞，
  直到重新供给；不得自动重试放大外部失败。
- **审计**：每次用供给凭据发起的外部写操作（create/update manifest、token rotate）记录
  操作者、对象与结果，不记录 token 本身。
- **纯本机授权边界**：Manifest API 返回 App 身份、client credentials 与安装链接，但安装者仍要
  在 Slack 页面确认授权。Slack OAuth callback 要求 HTTPS；本机 headless 部署不提供公共 callback，
  因此 CLI 通过隐藏输入或受保护文件接收安装页显示的 Bot token。不得用用户 Slack 登录 session、
  Slack CLI 凭据或浏览器自动化绕过该边界。
- **Socket token 边界**：App-level token 由用户在 App 设置页生成，scope 只能是
  `connections:write`。Mohist 验证它能为预期 App 建立 Socket 后才保存 readiness 事实。
- **当前状态**：四个 outbound port 已接生产 adapter（`SlackApiTransport` + App
  management / Configuration credential rotation / Bot identity verification / member
  identity），`setup` / `install-agent` 已接入真实 port。Allowlist/Anyone 门禁经生产
  `SlackMemberIdentityPortAdapter` 调 `users.info` / `conversations.info` 做活体成员与 Bot
  频道成员资格校验，owner/DM 快路径不触 Slack API，校验失败按 deny 处理。OAuth redirect
  回填路径与 `ISlackOAuthCredentialSink`
  的 Unavailable 占位已随旧路由退役；目标流程不经 OAuth callback，token 由 CLI 受保护输入
  进入。

Server 的 Slack control plane 只经四个窄 outbound port 访问 Slack HTTPS API：

- `SlackConfigurationCredentialPort`：rotate Configuration token pair，返回新的 pair、`team_id` 与
  expiry；不承担 App 管理。
- `SlackAppManagementPort`：validate/create/update/export/delete manifest，返回 App 身份、client
  credentials、安装链接和确定/未知结果；不安装 App，也不建立 Socket。
- `SlackBotIdentityVerificationPort`：用候选 Bot token 返回 team/Bot/scopes 的已验证事实；不发送
  用户消息。
- `SlackMemberIdentityPort`：用已验证 Bot token 经 `users.info` / `conversations.info` 返回发送者
  成员资格与 Bot 频道成员资格；仅 Allowlist/Anyone 门禁调用，owner/DM 快路径不触 Slack API。

这些 port 的 production adapter 位于 Server infrastructure，domain/application service 不依赖 Slack
SDK 或 HTTP shape。Socket `apps.connections.open`、hello、event、interaction 和消息投递只属于
`mohist-slack`；Server 不为验证 xapp 临时实现第二套 WebSocket client。

### setup / install-agent 编排

CLI 与 Mohist App 不各自实现安装。两者调用同一个 Server application service；service 每次读取
当前聚合事实、执行至多一个尚未确认的外部写入，然后返回完整 progress 与唯一 next action。

首次 `mo slack setup` 在外部 App 创建前先从 Configuration token rotation 的成功结果取得 provider
确认的 workspace `team_id`，并以它作为幂等键；已有 enrollment 直接按已保存 identity 或显式
`--workspace-team` 恢复，只有 token 临近过期或用户重供时才 rotation：

1. 受保护地接收 Configuration access/refresh token pair，执行一次 rotation 验证并原子保存返回的
   新 pair 与 `team_id`，再建立或恢复 `SlackWorkspaceEnrollment`。rotation 结果未知时不创建 App。
2. 生成 Mohist App canonical manifest，通过 app-management port 执行 validate/create；create
   结果未知时写入 fence 并停止，不能重发。
3. 持久化 `app_id`、client/signing secret 与安装链接，再要求用户确认 Slack 安装。
4. 受保护地接收 Bot/App-level token，先校验 workspace 与 Bot，再把候选 secret 写入 enrollment
   secret address；adapter 通过 validation lease 报告预期 App 的首次 Socket hello 后，凭据才标为
   verified，enrollment 才进入 ready。mismatch 会删除候选 secret。
5. 确保内置 `mohist-slack` Agent 与管理 actor（代码名 `ManagerActor`）的 binding 存在。重跑 setup 只修复 drift、缺失凭据
   或连接，不创建第二个 Mohist App；对就绪记录显式重供有效凭据即轮换。

`mo slack install-agent <agent>` 的幂等键是 `(enrollment_id, AgentId)`：

1. 解析并授权读取已有 Mohist Agent；如果同一 workspace 已有 non-deleted Connection，返回并续跑
   它。没有时先创建 team 已固定而 app/bot 为空的 Connection，再创建对应
   `ManagedSlackAgentApp`。
2. 生成 Agent App canonical manifest，validate 后执行一次 create，并在任何用户可见链接之前
   durable 保存返回的 `app_id`、client/signing secret、manifest hash 与 operation fence。
3. 返回安装链接；用户确认或管理员批准后，同一命令接收 Bot/App-level token。Server 先通过
   control-plane verification port 用 `auth.test` 与 Bot identity lookup 校验 team/Bot，再把候选 secret
   写入 AgentApp secret address，但保持 unverified 且不绑定 Connection。
4. adapter 取得一次 validation lease，用候选 App-level token 调 `apps.connections.open` 并报告 Socket
   `hello.app_id`。Server 校验预期 App 后才把凭据标为 verified；mismatch 时删除候选 secret 并保持
   Connection 未绑定。AgentApp 随后提交可绑定事实，durable handler 幂等补齐 Connection 的 app/bot
   identity；adapter 首次取得 runtime lease 后，安装投影才显示 ready。

重跑 `install-agent` 只修复 drift、缺失或失效凭据与连接，不创建第二个 Connection 或 Agent App：
已保存凭据重新校验失败即回到凭据步骤；对 ready 记录显式重供有效凭据即轮换，身份必须仍解析
为原 team/app/bot，否则拒绝。

Mohist App 中的“安装 Agent”tool 只执行上述流程的非 secret 步骤并返回同一 progress。遇到安装
确认或凭据步骤时，它给出链接和本机 `mo slack install-agent <agent>` 继续命令；聊天文本永远不是
secret input channel。

### Canonical manifests

Mohist App manifest 启用 Socket Mode、App Home message tab、`message.im` 事件与 interactivity；
Bot scopes 只包含管理私聊所需的 `chat:write`、`im:history`、`users:read`。Agent App manifest
启用 Socket Mode、App Home message tab、`app_mention` / `message.im` 事件与 interactivity；Bot
scopes 固定为 `app_mentions:read`、`channels:history`、`channels:read`、`chat:write`、
`groups:history`、`groups:read`、`im:history`、`reactions:read`、`reactions:write`、`users:read`。
`channels:read` 与 `groups:read` 支撑 Allowlist/Anyone 门禁经 `conversations.info` 核对 Bot 的
频道成员资格，发送者成员校验经 `users.info`，由既有 `users:read` 覆盖；DM 快路径与 owner
校验不调 `conversations.info`，因此不请求 `im:read` / `mpim:read`。第一版不支持 group DM，
因此不请求 `mpim:history`。interactivity 使用 Socket Mode
回传，不配置 Request URL。

manifest 先 canonical serialize，再以 manifest version、产品 capability version 和 identity snapshot
计算 hash。只有 version/capability/identity 或 canonical 内容变化才产生 drift；Slack 对 omitted
boolean 保留的 `true` 按 true-or-omitted 语义比较，不制造假 drift。

### Socket lease 与 adapter discovery

Server 发放两类短 lease：validation lease 只允许 adapter 用候选 App-level token 建立一次 Socket、
报告 `hello.app_id`，不能接纳 ingress 或领取 outbox；runtime lease 只发给凭据已验证且目标 active
的 Mohist App，或凭据已验证且 Connection Enabled 的 Agent App。`mohist-slack` 使用
operator-authenticated loopback transport 发现目标和续租；lease response 才可包含 secret，
状态/list/view DTO 永不包含。adapter 失联或 lease 过期后 Server 停止向它发放 delivery intent，
新的 adapter 可以在旧 lease 失效后接管。Mohist App 的 runtime ingress 路由到受限管理 actor，
而不是普通 Agent Connection 访问策略。

lease 记录在签发时钉住它签发的凭据代际（凭据的 SHA-256 指纹，不是明文也不是可逆派生值）：
validation lease 钉住候选 App-level token，runtime lease 钉住 verified pair。候选被重供或
verified pair 被轮换后，旧 lease 的 renew 与 hello 一律 fail closed（renew 拒绝、hello 返回 stale，
且旧 token 的 hello 不得触发对新 candidate 的 reject/删除），holder 必须重新 acquire 拿新凭据。
acquire 在写入 lease store 之前完成 secret 解析与目标状态复检：解析失败、candidate 缺失或目标已
离开 leasable 状态时，不签发 lease，也不挤掉现有 holder（失败路径不留下 inert active lease）。
promote 崩溃窗口（candidate 已清理但未标 Verified）下目标停留在 AwaitingSocket，validation
acquire 干净失败，operator 重供候选后同一 hello 流程可收敛。

Socket envelope 必须携带并校验 `api_app_id + team_id`，再反查 enrollment 或 Connection；未知
App/team 只确认并拒绝，不按 Bot 名称路由。ack 仍只表示 Server durable accept，不能等待 Agent
执行完成。

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

Slack 控制面的 create/delete 同样是 at-least-once 外部副作用：重复 attempt 不重复创建/删除 App，
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

- 所有 Slack ingress 使用 Socket Mode，不要求或打开公共入站端点。Slack 方向可显式配置代理，
  但 adapter 到 loopback Server 的 transport 不得经过 Slack 代理。
- `mohist-slack` 是高权限本机组件，与 Mohist Server 同信任域部署；它只获得调用固定 Connection、
  读取结果和回送消息所需的权限。
- Stop 请求的签名钥与 Connection 的 BotToken 假定处于同一个 self-host 安装信任域：adapter 可以
  持有并使用签名钥转发请求，但 Server 仍必须重新核验签名、目标 Connection、操作者和执行中
  Turn 后才裁定停止；二者都不能被当作 Slack 用户凭据，也不能跨信任域共享。
- App 与 Bot 凭据、Mohist App credential、Agent App client secret 与 signing secret 由 Server 加密保存，
  **按各自拥有者寻址**（见「凭据所有权」），不进入 Agent Instructions、transcript、日志、客户端可见
  状态、durable row、DTO 或审计序列化。CLI 临时读取的 secret buffer 在提交后立即释放，不进入
  shell history、命令参数或进程环境。
- 成员校验以 Slack 的稳定 workspace 身份为准，不以显示名、头像或消息文本判断授权。
- 频道调用权实质等于借用 Agent 已配置的执行能力（仓库写入、工具、凭据）。Access policy 因此是
  权限决策，不是便利开关；导入的 thread 历史同样是不可信输入，其影响上限由 Agent 配置决定。
- 每次调用记录 workspace、conversation 和成员身份用于审计，但这些身份不自动成为 Mohist 管理员。
  Mohist App 安装者、Agent owner、Connection owner 与普通 caller 是四个不同角色。

第一版不建设公开 App Marketplace、多租户托管、计费或跨组织身份联邦。那些需求会改变安装、授权和
运营模型，应作为独立产品阶段设计。

## 非目标

- 不让 Slack Bot 运行 Agent Runtime 或拥有另一份 Agent 配置。
- 不让 adapter 持有任何需要备份或恢复的状态。
- 不让 Mohist App 代替 Agent 发送回复，或成为多 Agent 共享的执行身份。
- 不在 Slack 中复制 Agent 编辑器、Workflow 看板或完整诊断工作台。
- 不让共享 Bot 根据自然语言猜测目标 Agent。
- 第一版不做 Slack 原生 Agent 体验（Agent Messages、Agent Home、流式回复）。
- 本机 Socket Mode 不承诺零步骤全自动：安装者要确认 Slack 安装，工作区策略要求时要等待
  管理员审批，并通过本机受保护输入提供安装结果与 App-level token。不得要求用户提供 Slack
  登录 session，也不得把 Slack CLI 当作产品前提。
- 本文不固定 API 路径、存储字段、锁和租约、Slack SDK 版本或精确重试时间。

## 已交付 correctness kernel

本节记录 correctness kernel 已交付的纯模型部分。四个 outbound port 的生产 adapter 已接线
（见「App 供给凭据」当前状态注），但默认测试仍全部使用 fake port 与 fixed `TimeProvider`，不触
真实网络。

### 模型不变量

- `SlackWorkspaceEnrollment`：active enrollment 的 `team_id` 唯一；拥有 Mohist App 身份/能力/lifecycle
  与 credential refs；不带 Project（除非产品 spec 改成每 Project）。
- 模型名 `ManagedSlackAgentApp`：引用
  `AgentConnectionId`；拥有四轴状态、desired/applied manifest + hash、verified scopes、operation
  fence、unknown outcome、error class、audit、Agent App secret refs。
- Connection 分阶段绑定：`AgentId + WorkspaceTeamId` 创建即固定；`AppId + BotUserId` 从「都空」
  原子补成「都非空」一次；禁止半绑定、team 改绑、二次 app/bot 改绑。
- AgentApp → Connection：durable fact + idempotent bind，不跨聚合事务；Connection 已删或已绑其它
  身份时，AgentApp 保留可诊断状态，不回滚外部 App。

### DB constraints（兜底，不靠 service 先查后写）

- active enrollment `team_id` 唯一。
- `(team_id, app_id)` 唯一。
- 一个 AgentApp 只绑定一个 Connection。
- 同一 Project/Agent/team 只有一个未删除 Connection。
- Connection 软删；AgentApp 历史引用保留；「移除绑定」不级联删 AgentApp。

### fake app-management port

生产代码只能经一个窄 port 调 Slack create/delete（ArchTest 兜底）。目标态按上文拆为 Configuration
credential、app-management、Bot identity verification 与 member identity 四个窄 port；生产
adapter 已接线，
默认测试仍使用 fake 实现，覆盖：

- create 成功 / definite 失败 / **unknown**（超时或 internal_error）；
- delete 成功 / definite 失败 / **unknown**；
- 越权（非 Mohist 创建的 App）update/delete 拒绝；
- managed-App 数量上限（`managed_app_limit_reached`）。

fake 不触真实网络；与 fixed `TimeProvider` 一起驱动 application service。

### 测试矩阵（spec + unit，全部走 fake + fixed time）

1. 并发/fence：同 AgentApp 双 create 只调 fake 一次；stale attempt 结果不覆盖新 fence；重启后
   `create-unknown` 不自动 create。
2. unknown 对称：create/delete 都有 unknown；只有 reconcile 或显式人工裁决能离开 unknown；definite
   failure 可在同 AgentApp 生成新 attempt，不新建 Connection/Bot 目标。
3. staged binding：team reservation 可补 app+bot 一次；半绑定、team/app/bot mismatch、二次改绑全部
   拒绝且无部分写入。
4. 跨聚合收敛：AgentApp 成功事实重投不重复绑定；Connection 已删/冲突时 AgentApp 保留可诊断状态。
5. 安装授权：取消、过期或 pending approval 恢复同一 AgentApp；任何 team/app/bot mismatch 不保存
   `xoxb-`、不绑定 Connection；凭据重复提交幂等。
6. secret 安全：model/store/manifest/error/audit 序列化均不含 plaintext；缺 Bot token 或 `xapp-`
   均不得 ready。
7. manifest determinism：同输入 canonical bytes/hash 完全相同；字段顺序不影响 hash；
   capability/version 或身份快照变化才形成明确 drift；只输出 live schema，禁止旧 schema 与 Mohist
   metadata 进入 manifest。
8. DB constraints：上述 4 条约束在并发下成立。
9. 生命周期：Disable 只改 Connection；Remove binding 清理数据面但保留 AgentApp/管理事实；Permanent
   delete 需二次确认 + 审计 + 无 active binding，且 definite/unknown outcome 分开。
10. ArchTests：Agent 域不依赖 Slack integration model/port；Slack integration 不依赖
    `packages/mohist-slack`；Enrollment/AgentApp 不进入 Agent/Session aggregate；生产代码只能经
    app-management port 调 Slack create/delete；现有 inbox/mapping/outbox 边界不动。

### 允许 / 禁止

允许：更新 `docs/slack.md`、`design/slack.md`，并同步
`design/architecture.md` / `design/domain-analysis.md`；Server 内新增 Slack-specific、
Socket-specific 的 Enrollment / AgentApp model/store、deterministic manifest generator、
app-management port + fake、`TimeProvider` 驱动的 application service；为 staged workspace reservation
做最小 AgentConnection model/store 变更与迁移。

禁止：改 provider inbox/mapping/outbox；把 AgentApp 字段塞进 `SetupProgress` 或 Agent 定义；在 durable
row/DTO/audit/log 保存 plaintext secret；为 generator 先迁移现有生产 secret
路径；跨 provider 通用化（这是 Slack-specific 模型，不是通用 provider
install）。

### 目标流程验收

默认测试全部使用 fake port 与 fake time，不访问 Slack、进程、systemd 或真实 secret：

1. `setup` 重跑只恢复一个 enrollment/Mohist App；Configuration rotation、manifest create、安装等待、
   两种运行凭据与 Socket hello 每步都能在进程重启后继续，unknown 外部写入不自动重放。
2. `install-agent` 并发或重跑只产生一个 Connection/AgentApp；CLI 和 Mohist App tool 发起时落到同一
   application service、同一 progress 和同一 next action。
3. TTY 只用隐藏输入；非 TTY 缺少 secret file 时立即非零退出；错误、stdout、JSON、日志、审计与
   snapshot 均不含 token。Configuration 与 runtime credential schema 不能互换。
4. bot/app token mismatch、错误 workspace、缺 scope、Socket hello App 不符都在 credential verified
   和 Connection bind 前失败；失败的 candidate secret 被删除，成功后的 handler 重投不重复绑定。
5. adapter 为 Mohist App 和所有 Agent App 分别建立 Socket，续租、重连、代理和 outbox claim 不改变
   Server 权威；loopback Server transport 不走 Slack proxy。
6. manifest contract test 锁定 scopes、events、Socket Mode、interactivity 与 canonical hash；平台
   true-or-omitted round-trip 不产生 drift。

发布前的真实 Slack 验收是独立、显式的人工 E2E，不进入默认测试：在隔离 workspace 依次跑
`setup`、`install-agent`、Bot mention、thread 锚点回复、同 Session follow-up 与 Stop，核对 Mohist App
和 Agent App 两条 Socket lease，最后清理测试 App、消息、频道和 enrollment。报告只保留脱敏身份与
状态证据。

## 实装差距与顺序

### 当前实装

数据面已具备 `AgentConnection` 的 Setup progress、Desired state、Connection health 与 Agent
Readiness 分离，以及 Server 持有的 provider inbox、conversation mapping 和 outbound outbox。无状态
`mohist-slack` adapter 负责把稳定 delivery identity 投影为 post、update 与 reaction；未知 mutation
会依据该 identity 核对，update 的明确失败只产生一次 fallback。终态 delivery 由 Server 基于 session
和管理员配置的 external web URL 构造 link block，adapter 不解析 Agent 文本为 Slack 控制对象。

控制面已有 Slack-specific Enrollment、`ManagedSlackAgentApp` 聚合、claim
与 ManagerActor 边界。operator setup 签发
一次性 claim，Manager ingress 先 durable accept，再按认领的 actor 和目标资源授权。内置
`mohist-slack` Agent 沿用标准 SessionInput、AgentTurn 与 Runner dispatch；受控工具可创建带默认
runtime 的普通 Project Agent 后委托同一 Manager application service 挂载。删除、解除绑定、凭据和
投递重发工具不在 Manager 对话 catalog 中。

`mo slack setup` 向导经 `/setup/configuration` 路由轮换并原子保存 Configuration token pair（
`ProtectedSlackConfigurationCredentialStore`），经 `/setup/runtime-credentials` 校验 workspace 与
Mohist App 身份后写入 enrollment secret address；`mo slack install-agent` 经 `/install-agent/credentials`
校验 Bot 身份并写入 AgentApp secret address。无文件时 CLI 使用隐藏输入；有文件时只接受受保护的
用户专属非符号链接文件。CLI 不暴露 token flag。

普通 Slack 输入拥有不可变的 reply anchor 与协作 Skill，dispatch-only context 不进入 Agent 配置。
普通 follow-up 始终走既有接纳路径；Stop interaction 由 Server 签名、去重并在重读 executing Turn
和 actor 后才调用既有 stop operation。所有这些路径的测试使用 fake port、in-memory store、fixed
`TimeProvider` 与 deterministic runner probe。

### 仍未实装

`setup` / `install-agent` 的本机安装向导、四个 outbound port 的生产 adapter、adapter lease
routes 与 Mohist App / Agent App 的 Socket Mode ingress 均已接入：CLI 从持久进度幂等续跑，
完成 Configuration token 轮换、manifest create、安装引导、运行凭据验证与 Socket hello 确认；
manifest 只输出 Socket Mode（无 HTTPS transport / `PublicIngressBaseUrl`），Agent App scopes 已
固定为不含 `mpim:history` 的目标 contract。与真实 Slack 的端到端验收尚未进行：默认测试全部
走 fake port 与 fixed `TimeProvider`，发布前仍需在隔离工作区人工跑通 `setup`、
`install-agent`、Bot mention、thread 锚点回复与 Stop，并核对两条 Socket lease。

Server 的旧面已退役：OAuth redirect 路径（`begin-authorization` / `authorization-progress` /
`authorize`，连同 `SlackOAuthStateService` / `SlackOAuthAuthorizationService` 与
`ISlackOAuthCredentialSink` 的 Unavailable 占位）、旧 `slack-manager/credentials` 登记路由、
数据面 `rotate-credentials` 与明文 token 的 `adapter-session` 路由均不再映射，
`SlackLegacyRouteRetirementSpecs` 以路由表与 HTTP 404 双重锁定缺席；新 CLI 与 Manager 对话
不依赖 OAuth callback。

公开应用市场、多租户托管、跨 Mohist Server 协调、Slack 原生 Agent 入口、App Home 以及完整的
规模化和运维体验仍属于后续阶段。后续能力仍必须经 Agent API 与既有 Connection boundary 进入，
不得让 adapter 解析 Runner 日志、覆盖 Agent 配置或直接写 Mohist 数据库。
