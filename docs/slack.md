# Slack

Slack 是 Mohist 的交互界面之一：同一批 Agent 与 Workflow 也可以从 Web、CLI 或 CI 使用。
本文描述 Mohist 的 Slack 集成。

Mohist 在一个 Slack 工作区中呈现为两种 App。**Mohist App** 是管理入口——用户在 Slack
里与它对话，完成工作区安装、挂载与调整 Agent、查看状态、创建 Agent 等管理动作。
**Agent App** 是执行入口——每个接入的 Agent 拥有属于自己的独立 Slack App 与 Bot 身份，
在频道和私聊里直接接受工作并回复结果。管理动作与工作任务各有明确身份，互不代发。

接入让一个已经配置好的 Mohist Agent 以独立 Bot 身份出现在 Slack：用户在 Slack 里 `@`
它或给它发私信，消息会交给这个 Mohist Agent；结果再回到同一条 Slack 对话。

Slack 不运行模型，不保存另一份 Instructions、Runtime、Model 或 Skills，也不裁定工作
状态。移除 Slack 接入后，同一个 Agent 仍然可以从 Web UI、CLI、事件路由和评论提及使用。

Agent 接入与 Hermes 通知不同：通知只把变化单向推送到聊天工具；Agent 接入允许用户发起
工作、继续会话、停止当前执行并收到结果。

### 当前开发期管理面

当前 P0 的 Manager 生命周期 API 不提供 Mohist 登录、调用者认证或权限隔离。每次请求都按
部署管理操作处理；客户端不能通过 `ManagerExternalId`、`actor` 或请求头声明操作者，这些
字段也不会进入审计记录。Slack 的安装授权仍只用于确认实际的工作区与子 App，不代表 Mohist
调用者身份。

## 安装模型

一个 Slack 工作区只需要安装一次 **Mohist Manager**。Manager 是这个工作区的安装与运维
入口：它把工作区与一个 Mohist 部署安全绑定，之后每个要接入的 Agent 都由 Manager 创建
出**属于它自己的 Slack App 与独立 Bot 身份**，而不是多个 Agent 共用一个 Bot。Manager
对用户呈现为 Slack 中的 Mohist App。

因此用户看到谁，就确定调用谁：每个 Agent 仍是原生可 `@` 的独立身份，Agent 的回复也始终
由它自己的 Bot 发出，绝不由 Manager 代发。Manager 只负责发现、安装、续装、诊断、停用和
卸载这些 Agent 身份，不代替 Agent 执行工作，也不扩大或削减 Agent 已配置的能力。

Slack 仍是交互入口，Mohist 仍是 Agent、工作、会话和调用权限的事实来源。把工作区连接到
Manager、或让某个 Slack 成员成为 Owner，都不会自动获得 Mohist 管理权限。

### 两条安装路径

每个 Agent App 在创建后都要和 Slack 建立一条事件通道。根据 Mohist 部署能否暴露公共
入站地址，Manager 在创建 Agent App 时选择其中一条。两条路径都需要安装者完成 Slack
安装授权；工作区策略要求时同样要过管理员审批，托管路径也不能绕过这一步。

- **托管路径（HTTPS）**：Mohist 部署有公共入站地址。Manager 自动完成 App 创建，引导
  安装者完成 Slack 安装授权，并在授权返回后直接接收并保存该 App 运行所需的全部凭据；
  安装者无需复制任何子 App 运行凭据。
- **本机路径（Socket Mode）**：Mohist 部署不需要暴露公共入站地址，由本机服务主动向外
  建立 Slack 连接。Manager 同样自动完成 App 创建并引导安装授权，但 Slack 不经接口返回
  这一类 App 的 App-level token；因此安装者需要**在该 Agent App 的设置页生成一次
  App-level token 并粘贴回 Mohist**。这是本机路径在同样的授权与可能审批之外，每个 Agent
  多出的一次性人工步骤。

两条路径都保证：同一个 Agent 在一个工作区只产生一个 App、一个 Bot；中断或失败的安装可以
恢复到同一个 App，不会重复创建 Bot。

> **为什么本机路径多一步：** Slack 的 App 创建接口不会把 App-level token 交给调用方，公开
> 接口也没有生成或读取它的能力。这是 Slack 平台的限制，不是 Mohist 的选择。托管路径用
> 公共入站地址的签名校验替代了对这个 token 的依赖，因此省去这一步手工 token；但它仍需要
> 安装者完成 Slack 安装授权，也不绕过工作区要求的管理员审批。

### App 供给凭据

Mohist 要为工作区创建并持续维护 Manager 与各 Agent App，需要一枚**工作区级 App 供给
凭据**（Slack Configuration Token）。它把整个接入过程里需要用户手工处理的凭据压缩到
最少：

- **只提供一次**：首次连接工作区时提供。`mo slack` 引导用户打开 Slack 的 App 管理页
  生成 Configuration Token，并以受保护输入粘贴回来（不回显、可随时撤销重供）。这是
  用户在整个接入过程中唯一需要手工提供的凭据。
- **加密保存、可轮换、可撤销**：供给凭据只由 Mohist Server 保存，不出现在消息、日志、
  CLI 回显或 Agent 可见的任何文本中。失效或被撤销时，该工作区的 App 维护动作进入
  降级并给出唯一下一步（重新供给）；已安装 Bot 的收发不受影响，但新建与续装会阻塞。
- **之后只剩授权**：有了供给凭据，Manager 与 Agent App 的创建、配置更新都由 Mohist
  自动完成；用户面对的只剩 Slack 安装授权点击（以及本机路径每个 Agent 一次性的
  App-level token）。供给凭据只用于创建和维护 App 本身，不能替代安装授权，也不授予
  Mohist 读取工作区消息的能力。

设计原则：**用户的主交互是授权，不是配置凭据**。任何要求用户再粘贴一枚 token 的流程，
都必须先证明 Slack 平台没有自动化手段——目前唯一的残留是本机路径的 App-level token。
引导必须自包含：不假设用户环境里有任何 Mohist 之外的工具，每一步的入口、要做什么、
为什么需要，都由 `mo slack` 直接给出。

## 与 Mohist App 对话

Manager 不仅完成一次性安装，它是 Slack 里的常驻管理入口。用户在 Mohist App 的私聊中
用自然语言完成全部日常管理动作：

- **挂载 Agent**：「把 review-bot 挂到 #backend」。Manager 建立可恢复的 Connection
  记录、创建 Agent App、发回安装授权链接，并在授权完成后引导认领 Owner、选择访问策略，
  直到就绪。整个过程就是下文「安装流程」的同一条记录，对话只是它的驱动方式之一；
  中断后在对话、Web 或 CLI 中都能从同一步继续。
- **查看与诊断**：「现在接了哪些 Agent」「review-bot 什么状态」。Manager 逐项给出
  当前状态与唯一下一步，与 Web、CLI 看到的同一批事实。
- **调整与生命周期**：调整访问策略、停用或启用一个接入、发起 Owner 转移。永久删除
  Slack App 不进入对话——它需要二次确认与审计，只在 Web 与 CLI 操作。
- **创建 Agent**：「帮我建一个每天盯 CI 失败的 Agent」。Manager 最多追问两件事——
  名字和日常做什么——其余使用默认配置：它替用户起草 Instructions、选用默认 Runtime
  与 Model，不逐项追问技术配置。创建完成后立即引导挂载。

Mohist App 绑定一个名为 `mohist-slack` 的内置 Mohist Agent：它的能力全部来自 Mohist
已有操作，与 CLI、Web 面对的是同一套资源与语义；对话只是这些能力的自然语言界面，不为
管理动作发明第二份语义，也不代替任何 Agent 执行工作。

谁能驱动 Manager 与谁能管理对应资源是同一批人：Manager 只对有权管理目标 Connection
或 Agent 的操作者响应管理请求，普通工作区成员与它对话不会得到管理动作。Mohist App 的
私聊默认只接受安装时认领的操作者，与「能调用 Bot 就等于能使唤它背后的一切」是同一
原则。

## 第一版产品边界

- 一个 Agent 接入只绑定一个 Project 中的一个 Mohist Agent。
- 一个接入对应一个 Slack 工作区中的一个 Bot 身份；这个 Bot 由 Manager 为该 Agent 创建的
  独立 Slack App 产生。
- 同一个 Mohist Agent 可以有多个接入，例如分别加入个人与团队工作区；每个接入的权限、
  状态和历史线程映射彼此独立。
- 同一个 Agent 在一个 Slack 工作区最多有一个未删除接入；一个 Bot 可以加入多个频道，
  不用为每个频道复制接入。
- 第一版为每个接入使用独立 Bot 身份，不用一个共享的 `@mohist` Bot 在消息里临时选择
  Agent。Manager 是管理入口，不是共享执行身份。
- 第一版面向私有 Slack App。是否需要公共应用市场、多租户托管，作为独立产品阶段评估。
- Slack 由独立的 `mohist-slack` 本机服务收发消息；它只做协议翻译，不保存任何需要恢复的
  数据，Agent、会话、消息接收进度、会话归属和待发消息都在 Mohist 中。托管路径的事件
  入站由 Mohist 自己校验签名后进入同一套处理流程。
- 第一版使用普通 Slack Bot：私聊、频道提及和 thread。Slack 原生的 Agent 入口是后续
  阶段，它只改变 Slack 中的呈现，不改变 Agent 能力。
- 第一版私聊入口只支持成员与 Bot 的一对一 DM，不处理 group DM；团队讨论使用 Bot 已加入的
  channel 与 thread。

## 接入前的条件与建议

把一个工作区连接到 Manager 需要一个有权在该工作区创建和安装 App 的 Slack 成员完成一次
授权，并一次性提供 App 供给凭据（见「App 供给凭据」）。**很多工作区默认禁止成员自行安装
App**，这时 Manager 的安装本身要过一次管理员审批；
之后 Manager 为各 Agent 创建的子 App，以及这些子 App 的安装授权，仍可能各自需要审批。
先确认工作区的 App 安装策略与 plan 是否支持 Manager，再决定要把哪几个 Agent 接进来。

每个 Agent 的安装是一个**可恢复的流程**：它有稳定的身份，中断、超时、用户取消或管理员
待审批后都能回到同一个 Agent App 继续下一步，不会重新创建一个 Bot。Manager 每一步只展示
一个当前状态和唯一的下一步动作，不让用户自己拼装结论。

`mohist-slack` 服务（本机路径）可以稍后安装或恢复；公共入站地址（托管路径）也可以稍后就绪。
未就绪时 Connection 保留在对应等待状态，而不是让用户重新开始。Agent Readiness 与 Connection
安装相互独立，因此可以并行完成。

建议先从 Web UI 或 `mo agent launch` 完成一次真实测试，确认 Agent 行为符合预期，再邀请
其他人使用。Agent 为 Needs setup 时仍可把 Connection 配到 Connected，但 Slack 中的新委托
会被明确拒绝；频道只显示安全摘要，Owner 与 Web/CLI 操作者能看到具体配置缺口。Unknown
时委托会被接受并等待 Runner 验证。Runner 暂时离线或没有空闲容量不阻止创建接入，任务会
明确排队。

Slack 不会成为 Agent 配置的第二份来源：直接使用失败的 Agent，不能靠 Slack 里的隐藏
提示词变成可用。接入页会分别显示 Agent Readiness、安装进度和连接健康，不用一个含混的
“Connected”掩盖配置缺口。

## 安装流程

安装始终从 Manager 开始，并在 Mohist 与 Slack 两边都留下可恢复的进度。安装者通常不需要
逐步操作：与 Mohist App 对话即可驱动同一流程，每一步的当前状态与唯一下一步都在对话中
给出。下面的步骤对两条路径都成立，是这套流程的完整事实；Web、CLI 与对话操作的是同一条
安装记录，只有最后取得运行凭据的方式不同。

### 选择 Agent 并准备安装

1. 在 Manager 中，安装者看到自己**有权管理 Connection** 的 active Agent 列表，每项给出当前
   Slack 状态：未安装 / 待操作 / 待审批 / 安装中 / 就绪 / 降级 / 已停用 / 已移除。Slack 身份
   不能让安装者看到或安装其无权管理的 Agent。
2. 安装者选择一个 Agent，确认将出现在 Slack 的 Bot 名称、头像与说明、该 Agent 需要的 Slack
   权限与理由、默认调用策略，以及谁将成为这个 Connection 的 Owner。名称是建议值，不是身份
   键；出现重名时 Manager 先给出一个带稳定后缀的备选，再创建。
3. Manager 立即建立一个**可恢复的 Connection 与安装记录**：固定目标 Agent 与目标工作区，
   但此刻还没有 Slack App 或 Bot。之后所有进度都落到这条记录上，中断后回到它继续。

### 创建 Agent App 并完成安装授权

4. Manager 用已供给的 App 供给凭据，为这个 Agent 生成一份版本化的 App 配置，并创建一个
   独立 Slack App。创建可能因超时或网络中断而结果未知；此时 Manager 进入**结果未知**状态，
   **不会自动再创建一个 App**，必须先与 Slack 核对或由人工裁决，确认同一个 Agent 仍只对应
   一个 App。
5. Manager 引导安装者完成这个 Agent App 的 Slack 安装授权。工作区开启审批时，等待管理员
   批准——这一步不能绕过。用户取消授权、授权过期或待审批时，Manager 都保留同一个 App，
   下一次继续，不新建 Bot。
6. 安装授权完成后，Manager 校验返回的工作区、App 与 Bot 身份确实和预期一致；任何不匹配都
   不保存凭据、不绑定 Connection，并给出明确的下一步。

### 取得运行凭据（两条路径在此分叉）

7. **托管路径**：Manager 直接取得并保存这个 Agent App 运行所需的全部凭据，用户无需复制
   任何子 App 运行凭据。
   **本机路径**：Manager 取得 Bot 凭据，但 App-level token 需要安装者在该 Agent App 的
   设置页 **Basic Information → App-Level Tokens** 生成一次（权限只选 `connections:write`）
   并粘贴回 Mohist。在它就绪前，Connection 不进入就绪状态。凭据不出现在 Agent Instructions、
   消息、日志或 Session transcript 中。

### 绑定 Owner 并验证

8. 身份与凭据验证完成后，进入 **Claim owner**。安装者选择 **Generate owner code**，Mohist
   显示一个短时有效、只能使用一次的认领码；离开页面后不再回显，丢失时重新生成会立即使
   旧码失效。安装者在与该 Bot 的私聊中发送该码；只有当前工作区中仍有效的正式成员才能成为
   Owner，外部协作成员、Bot 与已停用成员不能认领。一次成功的私聊认领也证明当前 App 能
   收取私聊并回复。
9. 选择频道访问策略。默认是 **Owner only**；Allowlist 通过姓名和头像搜索工作区成员，
   不要求用户查找 Slack member ID。Owner 也可以在 Bot 的管理操作中使用 Slack 原生成员
   选择器完成同一设置。Allowlist 和 Anyone 都始终包含 Owner。
   为避免被拉入私聊后形成意外授权，私聊在所有策略下都只接受 Owner。认领完成且连接健康
   后，状态变为 **就绪**。
10. 把 Bot 邀请进目标频道，在私聊中发送测试任务，或在频道根消息中 `@Bot`。测试
    结果同时可从 Agent 详情页的 Jobs 与 Sessions 中核对；Agent 尚未 Ready 时，Slack 明确
    显示安全的不可用摘要。具体 Runtime 或凭据缺口只向 Owner 以及 Web/CLI 操作者展示，
    Connection 本身仍保持就绪。

安装进度由已确认事实决定，并每次只突出一个当前状态和唯一下一步：未安装 / 待操作（例如
粘贴 App-level token、重试创建、继续授权）/ 待审批 / 安装中 / 就绪 / 降级 / 已停用。
Manager 无法知道用户是否在外部 Slack 页面完成了某一步，因此不伪造一个没有凭据或授权
证据的「就绪」状态。已完成步骤可以返回检查，页面和 `slack view` 不能只显示
Setup required。

页面允许分别查看 Agent Readiness、安装进度、连接健康和身份同步，但汇总区每次只突出一个
当前状态和唯一下一步，不让用户自己拼装结论。

### CLI

`mo slack` 覆盖 Slack 接入的全部操作：

- `mo slack setup` 把一个工作区装上 Mohist App，`mo slack status` 查看该工作区各接入的
  整体状态与唯一下一步。完整的路径选择、安装授权和操作者认领向导仍属于未交付目标，当前
  CLI 需要明确提供安装所需的工作区、App、Bot 和凭据引用。
- `mo slack configure-manager --workspace-team <team> [--credentials-file <path>]` 为已登记的
  workspace Manager 提供或轮换 Bot 凭据。凭据只能来自交互式隐藏输入，或来自用户专属、受保护且
  非符号链接的凭据文件；命令行不接受 token 字面量。`setup` 保存的只是 Manager 凭据引用，
  `status` 会分别显示引用是否已配置和凭据是否已提供，只有后者也成立时才会显示凭据就绪。
- 接入资源的管理：

```text
mo slack list <agent>
mo slack view <connection-id>
mo slack edit <connection-id> --access-policy allowlist --allow-member <slack-member-id>
mo slack disable <connection-id>
mo slack enable <connection-id>
mo slack transfer-owner <connection-id>
```

CLI 与 Web 配置的是同一个接入，不建立两份本机配置；与 Mohist App 对话驱动的安装流程
产出的记录同样可由 CLI 查询和继续。投递诊断与受管子 App 运维（deliveries、
resend-delivery、clear-gap、create-child-app、reconcile-create、reconcile-delete、remove-binding、
permanent-delete）的完整命令清单见 [CLI 参考](cli-reference.md)。

`--allow-member` 可以重复；选择 Allowlist 时，它替换除 Owner 外的完整成员列表。Owner 不用
重复填写，也不能从 Allowlist 中移除。选择 Owner only 或 Anyone 时不能同时传
`--allow-member`，错误会在修改前返回。member ID 是 CLI 自动化入口；Web 与 Slack 界面都
使用成员搜索和头像，不把 ID 当作主要交互。

> Manager Bot 凭据目前只能通过 `mo slack configure-manager` 的隐藏输入或受保护凭据文件提供；Web
> 当前不提供这个 provisioning 操作。本机路径需要补一次 App-level token 时，仍使用对应的受保护
> 输入；凭据不进入命令参数、Agent Instructions、消息、日志或 Session transcript。

## 接入配置

| 配置 | 含义 |
|---|---|
| Agent | 固定绑定的 Mohist Agent；创建后不能改绑，换 Agent 应新建接入 |
| Slack workspace | Bot 所在工作区；由 Manager 安装结果确认，不靠用户手填名称判断 |
| Bot identity | 创建时以 Agent 名称与头像初始化、之后由 Slack 管理并由 Mohist 核对的外部身份 |
| Slack 说明 | 从 Agent Description 生成的 App 短说明；空描述时 Mohist 生成非空通用说明 |
| 安装路径 | 托管（HTTPS，无需复制子 App 运行凭据）或本机（Socket Mode，每 Agent 一次手工 App-level token） |
| Owner | 首次认领或后续转移时验证的 Slack 成员；默认唯一调用者，也是 Allowlist 的固定成员 |
| Access policy | 谁可以在频道中发起工作或继续会话；默认 Owner only |
| Allowed members | Allowlist 模式下通过成员选择器添加、可在频道调用的 Slack 成员；私聊仍只有 Owner |
| 安装进度 | 未安装 / 待操作 / 待审批 / 安装中 / 就绪 / 降级 / 已停用 |
| Status | Setup required / 就绪 / Degraded / Disabled；Degraded 必须带一条可行动的原因 |
| Identity sync | Bot 名称和头像是否仍与当前 Agent 一致 |

Agent 的 Instructions、Runtime、Model、Variant、Skills 和并发限制不属于接入配置。执行
定义必须编辑 Mohist Agent，并从下一次新工作按快照生效；并发限制也由 Agent 编辑，但作为
实时调度策略作用于之后开始的 launch 与 follow-up。

Bot identity 也不是第二份 Agent 配置。Agent 名称或头像改变后，Mohist 会显示接入身份已经
不同步，并提供 Slack App 设置入口与正确的名称、头像；完成后重新验证。第一版不额外索取
Slack 管理员权限来替用户自动修改 App profile，身份不同步也不会伪装成连接中断。

Agent Description 只用于初始化 Slack App 的短说明，不会变成 Instructions 或隐藏提示词。
Description 改变后 Mohist 显示新的期望文案和手动更新入口；由于 Bot token 无法读取实际 App
配置，Mohist 只说明这一项无法自动核对，不声称已经同步。

## Slack 消息权限

为了让用户在一个已经绑定的 thread 中自然追问而不必每条消息都重新 `@Bot`，Slack App
需要接收它已加入频道中的消息事件。Mohist 只处理以下消息：Bot 私聊、明确提及，以及已经
绑定 AgentSession 的 thread 回复；其它普通频道消息在交给 Mohist Agent 前即被丢弃，正文
不进入 Mohist 的持久记录或日志。

配置页必须在安装前列出请求的 Slack 权限，并解释每项权限对应的功能。用户应只把 Bot 邀请
进确实需要使用的频道。第一版不提供零散权限开关，避免出现界面显示能追问或读文件、实际
App 却没有完整能力的半配置状态。

接入读取工作区的基础成员目录，用于 Owner/Allowlist 选人、显示消息发起者，并排除 Bot、
停用成员和外部工作区身份。Mohist 不读取成员邮箱，也不把成员目录交给 Agent；只保存显示
所需的成员 ID、名称和头像，并在 Connection 删除后清理。

Mohist 能核对 Bot 身份和已经授予的权限，也能通过真实收发确认消息路径可用；但在不索取
Slack 管理权限的前提下，不能读取 App 的全部配置。因此某项能力真正失败时才报出具体缺口，
不会仅凭 token 可用就声称所有能力正常。

本机路径（Socket Mode）不要求 Mohist 暴露公共入站地址，但 Slack 对未确认消息只保留有限
的重试窗口。配置页应建议在工作区允许时启用 **Delayed Events**；即使启用，Mohist 也不承诺
无限期补收。接入服务长时间离线后，状态页必须提示可能遗漏消息，并让用户重新发送关键委托。
托管路径（HTTPS）的入站请求由 Mohist 用该 App 的签名密钥校验时间和正文完整性后再进入处理，
未知 App 或工作区只返回未绑定，不按名称路由。

## Slack 中怎么使用

### 发起新工作

下面三种情况会发起一项新工作：

- 在与 Bot 的私聊中尚无 current Session 时发送第一条任务消息；
- 在私聊中使用 **New task**，填写并提交一项新任务；
- 在频道中发送一条新的根消息并 `@` Bot。

当前消息必须包含去掉 Bot mention 后的任务文本或至少一个可用附件。只发送 `@Bot` 不创建
AgentJob，Bot 会请用户补充任务；只发送附件可以作为明确输入，Mohist 不为它暗中编造提示词。

Mohist 接受后创建一个 AgentJob、一个 AgentSession、首条 SessionInput 与首个 AgentTurn。
Bot 先确认已经接受，显示当前是执行中还是排队中；排队时可取消，真正开始执行后可停止当前
执行。Agent 回复、失败或需要人工处理的结论都回到同一 Slack 对话。AgentJob 完成不关闭会话；
如果回复是在提问，用户直接继续即可。

### 继续同一会话

**频道**中，对 Bot 回复所在的 thread 继续发消息，会向已经绑定的 AgentSession 发送
follow-up。

**私聊**里没有 thread 的使用习惯，所以每个 DM conversation 有一个 current Session。普通消息
始终继续它，即使上一轮已经结束；AgentJob 完成也不会自动清空这个归属。要开始一件互不相关的
工作，使用 Bot 提供的 **New task** 打开任务输入，提交后同时创建新的 AgentJob、AgentSession、
SessionInput 和 AgentTurn，并把新 Session 设为 current。New task 不取消仍在执行的旧工作；旧工作
之后返回结果时，消息必须标出对应任务和稳定 Job / Session 身份，不能混成新会话的回复。

follow-up 的行为在两种场景下一致：

- 不创建新的 AgentJob；
- 保留该 Session 已有上下文；
- 每条消息创建一条有稳定身份的 SessionInput；
- 当前 Turn 尚未开始时，连续消息按 Slack 接收顺序等待；已经执行时，支持追加输入的后端
  把消息加入当前 AgentTurn，否则等待后续 Turn；
- 空闲时收到 Input 则在同一 Session 中开始下一 AgentTurn；
- 执行中到达的新消息默认是**转向**而非中断：它并入当前工作或等待下一 Turn，只有明确的
  停止操作才中断当前执行。

每个 Session 的等待队列有明确边界。达到边界时，Bot 拒绝新消息并提示稍后重试；已经确认
接受的消息不会为了给新消息腾位置而被丢弃。

同一个 Slack thread 可以有多个 Mohist Agent，各自拥有独立 AgentSession。规则是：

- thread 只有一个 Mohist Agent 已绑定时，未 `@` 的人类回复自然继续该 AgentSession；
- thread 已有多个 Mohist Agent 时，未 `@` 的回复只作为人类讨论，不调用任何 Agent；用户
  必须明确 `@` 目标 Bot；
- 第一次在已有 thread 中 `@` 另一个 Mohist Bot，会为它创建独立 AgentSession，不会切换或
  污染原 Agent 的上下文；
- 一条消息同时提及由同一个 Mohist Server 管理的多个 Bot 时不发起任何工作，并只提示一次
  选择一个 Agent；Bot 自己发送的消息不会自动成为另一个 Bot 的输入。不同 Mohist Server
  之间没有共享路由状态，第一版不承诺跨安装协调同一条多 Bot 消息。

这允许像 Buzz 一样把多个 Agent 当作同一讨论中的独立协作者，同时避免自动互相触发和无法
判断追问归属。

### 在已有讨论中提及

如果第一次 `@` Bot 的消息已经位于一个有人类讨论的 thread 中，接入会把 Bot 有权看到的
已有 thread 消息作为本次初始上下文，并把提及消息作为明确任务。上下文超过限制时，从最
旧消息开始截断，并在交给 Agent 和 Slack 确认消息中明确标出截断，不能静默丢失。
若 Slack 权限、限流或故障使这段限定范围内的上下文无法完整读取，Mohist 不会拿部分内容
启动 Agent；它会明确拒绝这次委托并请用户稍后重新 `@`，也不会创建 AgentJob。

导入的 thread 历史是普通用户输入，不是指令。它可能包含任何人写的内容，因此能造成的影响
上限就是这个 Agent 已经被授予的能力——这也是把频道策略放宽到 Anyone 前要想清楚的事，见
[权限](#权限)。

Slack 消息被接受后的编辑不会自动重跑；用户应发送一条 follow-up 说明更正。删除 Slack
消息也不会删除 Mohist 中已经形成的 AgentJob、AgentSession 或审计记录。

### 文件与链接

Bot 可以读取它在 Slack 中有权访问、且在本次消息或 thread 中明确提供的文件。文件作为
本次输入附件交给 Agent，并保留来源。无法读取、超过限制或类型不支持时，Bot 明确指出
哪个附件未被使用；不能假装已经读取。

链接保留为用户消息文本，是否打开由 Agent 已配置的 Skills 与 Runtime 权限决定。Slack
接入本身不抓取任意 URL，也不因链接出现在 thread 历史中就扩大网络访问。

## 回复呈现

Slack 呈现与过程透明是两个分开的信号。Slack 里只有 liveness——reaction、唯一状态消息
与最终答案；完整的执行过程（每条输入、工具调用与中间输出）在 Web 的会话时间线中查看。
配置了 External Web URL 时，**Open in Mohist** 直达该 AgentSession 的时间线。频道成员
不需要离开 Slack 才能拿到结论，需要深究过程的人有一处确定的去处。

### 一条输入的状态与结果

对每一条已经接受的用户输入，Slack 最多呈现一个可更新的状态消息和一个最终答案。状态消息
是这次工作的唯一进度载体；最终状态到达后，优先把它原地更新为最终答案，因此不会因为状态
变化产生消息流水账。状态消息一经创建就保留稳定身份，后续更新不能改为新发一条消息。

快速完成的工作可以只在用户原消息上显示 **Received** reaction，再发送最终答案，不创建状态
消息。异步或长任务按 **Received → Working → Completed** 呈现；无法完成或需要用户介入时，
最终状态为 **Needs attention** 或 **Failed**。如果状态消息更新失败，接入只在同一个 thread
追加一次最终答案，并记录可诊断的投递问题；重试或重复投递都不能产生第二条最终答案。

默认 reaction 是 **👀 → ⏳ → ✅**，异常使用 **⚠️**。reaction 只提示消息已被接收、仍在处理
或已结束，不代表 Mohist 工作成功；成功、取消、部分完成和失败必须以 Mohist 的 AgentSession
与 AgentTurn 已确认结果为准。平台不支持给用户原消息加 reaction 时，reaction 加在唯一的状态
消息上。

- Slack 只展示适合用户消费的 Agent 回复、排队/执行/失败状态和必要的操作，不转发隐藏
  推理、原始工具输出或凭据。
- Agent 回复按文本渲染，不把其中看起来像 Slack mention、按钮或消息配置的内容当作控制指令；
  它不能意外触发 `@channel` / `@here`、伪造 Stop 操作或要求 Slack 自动展开外部链接。真正的
  操作按钮只由 Mohist 根据当前 Job、Session 和 Turn 状态生成。
- Slack 回复必须包含足够完成当前决策的结论、证据摘要和下一步，不能要求用户进入 Mohist
  Web 才知道结果。长结果可以在同一 Slack 对话中分段呈现。
- 只有管理员配置了 Slack 用户可访问的 Mohist Web 地址时，消息才显示 **Open in Mohist**；
  localhost 地址绝不发送到 Slack。没有可用 Web 地址时显示稳定的 Job / Session 标识，供
  Web 或 CLI 的备用操作面继续查询。
- AgentJob 与每个 AgentTurn 的执行结果由 Mohist 决定。Slack 回复发送失败不会把已经完成
  的执行改成失败。
- 第一版可以显示结果中的文字、已存在且可访问的外部链接、artifact 名称和稳定标识，但不把
  Mohist artifact 自动复制成新的 Slack 文件。需要文件本体时使用结果提供的原始交付位置，
  或由 Web/CLI 备用平面读取；这不影响 Slack 中必须给出的结论与下一步。
- Slack 明确没有接受回复时，接入按平台要求重试并进入 Degraded。若发送结果无法确认，接入
  先核对；仍不确定时不会盲目再发一条，而是在 Connection 页、CLI 和可用的 Owner 诊断中显示
  **Delivery uncertain**。人工重发会明确警告可能出现重复回复；用户也可先查看 Mohist 中的
  执行结果。
- 待发消息有容量边界。尚未发送的排队或执行进度可以合并为最新状态；最终结果、明确失败和
  需要用户操作的消息不会被静默删除。无法继续容纳这些消息时，Connection 进入
  Degraded，并标明 **Backpressured** 原因，同时停止接受新的 Slack 输入；已经接受的执行继续
  由 Mohist 保存和裁定。
- 接入服务重启或重连后从 Mohist 记录的最后确认位置继续，不重复创建 Job、重复提交输入或
  重复发送已经确认的回复。

> **当前实装差距：** 频道根消息提及、已绑定 thread 追问、多个 Bot 的归属提示、重复投递保护和
> Owner-only、Allowlist、Anyone 的 Connection 级调用者访问策略已经可用；Anyone 还会校验
> Bot 是否能看到当前频道，私聊在所有策略下仍只接受 Owner。已有 thread 历史作为首次启动背景
> 导入也已经可用——导入是按 bot 可见的 thread 消息、按 ts 早于本次提及的整条消息删除的方式
> 来截断，超出大小时同时在 Slack 确认回复和 Agent 输入里标出。
>
> 附件可以随 Slack 消息一起提交为文件详情，包括文件名称、类型、大小和身份。只有 Mohist
> 能够读取文件内容时，才会尝试获取内容；不支持、超过大小限制或无法读取的文件会被拒绝。
> 这不保证能够取得或处理所有附件内容。
>
> Connection 访问策略已经交付，包含 Owner only、Allowlist 和 Anyone；Anyone 仍会确认 Bot
> 能看到当前频道。独立选择每个 Connection 接受哪些频道仍未交付。不同 Mohist Server 之间的
> 多 Bot 协调仍未提供。

## Agent 在 Slack 里的协作规范

接入时，Mohist 为 Agent 注入一份 Slack 协作规范——以 Skill 形式存在，可查看、随 Mohist
演进。它约束 Agent 在 Slack 这个多人场所里的行为方式，不改变 Agent 的能力：

- **不发空洞确认**。只表达「收到」「明白」「确认」的消息会打扰整个频道，还可能触发其他
  Bot；没有新内容就不发消息，沉默是正常结束，不是失败。
- **完成委派必须回调**。完成别人委派的工作时，在结果消息中 `@` 委派者——这是协作停滞的
  头号原因。接受委派、确认收到时不 `@` 人；只有需要对方注意时才 `@`，叙述中提及某人
  不用 `@`。
- **回复自包含，进展有分寸**。结论、证据摘要和下一步都在 Slack 回复里给出；里程碑式进展
  （已接手、被阻塞、完成）可以发到对话中，细粒度过程不刷屏，由 Web 会话时间线承载。
- **不猜回复位置**。每条输入该回到哪个 thread、锚到哪条消息，由 Mohist 随输入明确告知
  Agent；Agent 不凭记忆选择历史消息作为回复目标，也不把回复或委派发到别的频道。

## 权限

| 策略 | 私聊 | 频道提及和已绑定 thread |
|---|---|---|
| Owner only | 只有 Owner | 只有 Owner；默认值 |
| Allowlist | 只有 Owner | Owner 和明确列出的工作区成员 |
| Anyone | 只有 Owner | 能证明属于 App 安装工作区、且能在当前频道看到 Bot 的成员 |

**能调用这个 Bot，就等于能使唤这个 Agent 已经拿到的一切**——它配置的仓库写入权限、工具和
凭据。放宽访问策略是一次权限授予，不是一个便利开关。给 Agent 配了写权限的仓库，就不要把
它的频道策略设成 Anyone。

这沿用 Buzz 的 DM hardening：即使频道策略是 Allowlist 或 Anyone，也不会让偶然进入 Bot
私聊的成员获得调用权。频道成员关系只决定 Bot 能否收到消息，不代替 Access policy。Slack
Connect 中的外部参与者和归属无法确认的身份在第一版均不触发 Agent；Anyone 不是“任何能
看见消息的人”。Bot 被邀请进私有频道后，仍按接入策略检查发送者。无权调用的用户会收到简短
且明确的拒绝，不会创建 AgentJob 或 AgentSession。

停止或取消某个 AgentTurn 只能由 Connection Owner 或发起该 AgentSession 的 Slack 成员
执行。其他被允许的成员可以继续对话，但不能停止别人的执行；过期按钮也不能误停后来
开始的 Turn。

Access policy 只回答谁可以调用这个 Agent，不削减 Agent 本身已经配置的 Runtime、Skills、
仓库或工具能力。Slack 用户也不能通过消息临时增加或替换这些能力；Agent 权限仍只有 Mohist
中的一份配置。

每次输入都记录 Slack 工作区、频道、thread 和发送者身份用于审计。任何一次执行都能回答
“是哪个 Slack 成员发起的”。但 Slack 身份不会被当成 Mohist 管理员身份，也不能借普通消息
内容切换 Project、Agent 或访问策略。只有 Owner 使用明确的 **Manage access** 操作时，才能
修改这个 Connection 的调用范围。

访问策略修改立即作用于之后收到的每条输入，包括已有会话的 follow-up；它不撤销已经
接受的执行，也不删除历史。Owner 始终在 Allowlist 中。转移 Owner 只能由有权管理该
Connection 的 Mohist 操作者发起：系统生成新的单次认领码，新 Owner 在 Bot 私聊中认领后
原子替换，旧 Owner 在此之前保持不变。新 Owner 同样必须是当前工作区中仍有效的正式成员。

## 生命周期与异常

Connection 的生命周期有三个**互相独立、各自需要显式确认**的动作，不能用一个含混的「删除」
混在一起：

- **停用（Disable）**：只暂停这个 Connection——立即停止接受 Slack 输入和发送回复，已接受
  的执行仍在 Mohist 继续。它**不删除 Slack App**，也不动该 Agent App 的管理事实。重新启用
  后回到原状态。
- **移除绑定（Remove binding）**：解除 Mohist 与这个 Connection 的绑定，清理消息接收记录、
  会话映射和待发消息等运行记录，但**保留该 Agent App 的管理事实**，App 仍可被再次绑定或
  诊断。它不代替用户从 Slack 卸载 App。
- **永久删除 Slack App（Permanent delete）**：永久删除 Manager 为该 Agent 创建的 Slack App。
  这需要二次确认、单独权限与完整审计，且要求该 App 当前没有被任何 active Connection 绑定
  （或先显式移除绑定）。删除 Slack App 的结果可能未知；此时进入**删除结果未知**状态，不伪报
  已删除，必须先核对或人工裁决。Manager 只能删除它自己创建的 App。

| 情况 | 产品行为 |
|---|---|
| Agent 名称或头像被编辑 | Mohist 立即更新 Agent，并把 Bot identity 标为不同步；用户在 Slack App 设置中修改后重新验证 |
| Agent 描述被编辑 | 更新 Mohist 中的发现信息，并显示新的 Slack 说明期望值；不改变 Agent 行为或自动申请 Slack 管理权限 |
| Agent 执行定义被编辑 | 新 AgentJob 使用新快照；已有 Session 保持原配置 |
| Agent 并发限制被编辑 | 下一次 launch 或 follow-up 按最新限制调度；不强停已在执行的输入 |
| Agent 被归档 | 拒绝新的根委托；已有 Session 仍可查看和继续 |
| Agent 变为 Needs setup | 连接健康保持独立；新的根委托在频道中只显示安全摘要，Owner 与 Web/CLI 操作者可查看具体配置缺口；已有 Session 按自己的执行快照继续 |
| Agent Readiness 为 Unknown | 接受新的根委托并显示“等待 Runner 验证”；确定无法执行后由 AgentJob/Turn 返回明确失败 |
| 接入 Disabled | 立即停止接受 Slack 输入和发送回复；已接受执行仍在 Mohist 继续。禁用期间到达的消息被确认并丢弃，不让它们积成稍后的任务。重新 Enable 后只补齐缺失的当前/最终结果，不回放过期进度 |
| 移除绑定 | 清理运行记录与该 Connection 的绑定，保留该 Agent App 的管理事实；不卸载 Slack App |
| 永久删除 Slack App | 需二次确认、无 active 绑定、审计；删除结果未知时不伪报成功，需核对或人工裁决 |
| 创建子 App 超时或结果未知 | 进入「结果未知」，禁止自动再创建；必须先与 Slack 核对或人工裁决，确认同一 Agent 仍只对应一个 App |
| OAuth 取消、过期或待审批 | 保留同一个 Agent App，恢复后继续，不新建 Bot |
| 安装授权返回的身份与预期不符 | 不保存凭据、不绑定 Connection；给出明确下一步 |
| Slack 凭据失效 | 接入进入 Degraded 并停止接受新输入；修复时必须仍验证为原 workspace、App 与 Bot，否则按新安装处理 |
| 同一 Agent 已接入该 Slack workspace | 不覆盖已有 Connection；指向已有接入，由用户决定是否删除重复项并在 Slack 卸载多余 App |
| 入站通道暂时不可用（本机服务离线 / 公共入站不可达） | 已创建的 Connection 与安装进度保留；新消息由 Slack 在其重试窗口内重投，超出平台保留窗口的消息可能需要用户重新发送 |
| Owner 需要更换 | Mohist 操作者发起新的单次认领；成功前不移除旧 Owner，也不允许 Slack 发送者自行重置 |
| Owner 离开工作区、被停用或变为 guest | 接入进入 Degraded 并提示 Owner unavailable；不自动把身份转给同名成员。频道中的 Allowlist / Anyone 可继续按原策略使用，私聊和 Owner 管理操作不可用，Mohist 操作者需要发起 Owner 转移 |
| Slack 重复投递事件 | 返回已有 Job/Session/Input/Turn，不创建第二份输入 |
| Mohist 暂时无法确认 Input 是否已交给执行后端 | 标记为未知并核对同一个 Input，不用新请求自动重放 |
| 超过 Agent 并发限制或会话队列上限 | 明确显示排队或请稍后重试，不把容量不足伪装成执行失败，也不丢弃已接受的输入 |
| 待发结果达到容量上限 | 合并可替代的中间进度；Connection 进入 Degraded（Backpressured）并拒绝新的 Slack 输入，不丢弃最终结果、明确失败或用户操作 |
| Owner 或 Session 发起者取消 queued Turn | 立即从队列移除；首个 Turn 对应的 AgentJob 以 `cancelled` 失败结束 |
| Slack 无法发送回复 | 工作结果保持不变；明确未接受时自动重试，发送结果未知时显示 Delivery uncertain，不盲目重复发送 |

## 非目标

- Slack Bot 不运行 Agent Runtime，也不读取 Runner 或数据库。
- Slack Bot 不拥有 Agent 配置，不通过隐藏 prompt 修改 Agent 行为。
- Manager 不代替 Agent 发送回复，也不是多个 Agent 共享的执行身份。
- Mohist App 的对话不做 Workflow 看板、Issue 管理或完整诊断工作台；管理动作以 Agent
  接入的生命周期为边界。
- 不在 Slack 中提供 Agent 编辑器、Workflow 看板或完整诊断工作台。
- 不让一个共享 Bot 根据自然语言猜测用户想调用哪个 Agent。
- 不把普通频道消息全部发送给 Mohist；只有私聊、明确提及和已绑定 thread 的回复会触发。
- 第一版不做 Slack 原生 Agent 入口、Agent Home 和流式回复。
- 第一版不解决公开应用市场、多租户托管、计费、Slack Connect 外部成员调用或跨组织目录发现。
- 第一版不协调由不同 Mohist Server 管理、但位于同一 Slack 工作区中的 Bot。
- 第一版不支持 Slack group DM。
- 第一版不把 Mohist artifact 自动上传为 Slack 文件。
- 两条路径都不承诺「一句话零步骤全自动」：安装者都要完成 Slack 安装授权，工作区策略要求
  时同样要过管理员审批；本机路径在此基础上每个 Agent App 还需一次手工生成并粘贴 App-level
  token。

## 实装差距

数据面（消息收发与 Agent 调用）已经落地：频道根消息提及、已绑定 thread 追问、多个 Bot
的归属提示、重复投递保护、Owner-only/Allowlist/Anyone 访问策略与 thread 历史导入均已可用。
Anyone 访问会校验 Bot 是否能看到当前频道。状态消息可以
原位更新并呈现进行中的工作；未知投递会核对，更新失败只补发一次。每条 Slack 输入都带有
协作规则和回复位置，普通追问不会中断当前工作；只有对应执行中的工作才可通过明确的 Stop
操作请求停止。终态回复保留稳定会话标识，并在管理员配置可公开访问的 Mohist 地址时提供
安全的会话入口。

Manager 控制面已经提供 `mo slack setup`、`mo slack configure-manager`、`mo slack status` 和一次性认领。
`configure-manager` 只向已登记的活动 workspace enrollment 写入该 enrollment 的 Manager Bot 凭据；
重复执行是有意设计的安全轮换，命令输出只确认 workspace 和 provisioned 状态，不返回凭据。
认领后的 Mohist
App 对话使用内置 `mohist-slack` 管理 Agent，可查看状态、创建带默认配置的 Agent 并挂载、
调整接入权限与启停，以及发起 Owner 转移；它与 CLI 读取同一状态和下一步。解除绑定和永久
删除不在对话中提供，仍是 CLI 或 Web 中独立、明确的生命周期操作。

当前仍未交付的是完整的托管或本机安装向导、真实 Slack 子 App 创建与授权、公开应用市场、
跨 Mohist Server 的多 Bot 协调、Slack 原生 Agent 入口及完整诊断工作台。`mo slack setup`
目前是需要显式参数的受保护操作，不会引导用户完成完整安装流程；它也不会代替真实子 App
的创建、授权或审批。工作区安装仍由本机受保护的操作通道启动；这些未完成部分不应被理解
为已经由 Slack 支持。
