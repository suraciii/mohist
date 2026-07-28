# 把 Mohist Agent 接入 Slack

Agent 接入让一个已经配置好的 Mohist Agent 以独立 Bot 身份出现在 Slack。用户在 Slack
里 `@` 它或给它发私信，消息会交给这个 Mohist Agent；结果再回到同一条 Slack 对话。

Slack 不运行模型，不保存另一份 Instructions、Runtime、Model 或 Skills，也不裁定工作
状态。移除 Slack 接入后，同一个 Agent 仍然可以从 Web UI、CLI、事件路由和评论提及使用。

Agent 接入与 Hermes 通知不同：通知只把变化单向推送到聊天工具；Agent 接入允许用户发起
工作、继续会话、停止当前执行并收到结果。

## 第一版产品边界

- 一个 Agent 接入只绑定一个 Project 中的一个 Mohist Agent。
- 一个接入对应一个 Slack 工作区中的一个 Bot 身份。
- 同一个 Mohist Agent 可以有多个接入，例如分别加入个人与团队工作区；每个接入的权限、
  状态和历史线程映射彼此独立。
- 同一个 Agent 在一个 Slack 工作区最多有一个未删除接入；一个 Bot 可以加入多个频道，
  不用为每个频道复制接入。
- 第一版为每个接入使用独立 Bot 身份，不用一个共享的 `@mohist` Bot 在消息里临时选择
  Agent。这样用户看到谁，就能确定调用谁。
- 第一版面向 self-host 的私有 Slack App，不以公开应用市场和多租户托管为目标。
- Slack 由独立的 `mohist-slack` 本机服务接收和发送消息；它只通过 Mohist 使用 Agent，
  不在服务里保存另一份 Agent 配置。
- Slack 提供两种完整体验档：所有工作区都可用的 **Standard Bot**，以及工作区支持时推荐的
  **Slack Agent**。两者调用同一个 Mohist Agent；选择只改变 Slack 中的入口和呈现，不改变
  Agent 能力。第一条可交付路径先完成 Standard Bot，Slack Agent 体验随后补齐。
- 第一版私聊入口只支持成员与 Bot 的一对一 DM，不处理 group DM；团队讨论使用 Bot 已加入的
  channel 与 thread。

## 接入前的条件与建议

创建接入的硬条件只有 Agent 处于 active，以及配置者有权在目标 Slack 工作区创建和安装
App。`mohist-slack` 服务可以稍后安装或恢复；Connection 会保留在 **Waiting for Slack service**，
而不是让用户重新开始。Agent Readiness 与 Connection setup 相互独立，因此也可以并行完成。

建议先从 Web UI 或 `mo agent launch` 完成一次真实测试，确认 Agent 行为符合预期，再邀请
其他人使用。Agent 为 Needs setup 时仍可把 Connection 配到 Connected，但 Slack 中的新委托
会被明确拒绝；频道只显示安全摘要，Owner 与 Web/CLI 操作者能看到具体配置缺口。Unknown
时委托会被接受并等待 Runner 验证。Runner
暂时离线或没有空闲容量不阻止创建接入，任务会明确排队。

Slack 不会成为 Agent 配置的第二份来源：直接使用失败的 Agent，不能靠 Slack 里的隐藏
提示词变成可用。接入页会分别显示 Agent Readiness、安装进度、连接健康和已验证能力，不用
一个含混的“Connected”掩盖配置缺口。

## Slack 体验档

| 体验档 | 适合场景 | Slack 中的入口 |
|---|---|---|
| Standard Bot | 默认选择；所有允许安装私有 App 的工作区 | Bot 私聊、频道提及和 thread |
| Slack Agent | 工作区支持 Slack 原生 Agent 体验时推荐 | 原生 Messages 入口、Agent Home，以及同样的频道提及和 thread |

每个体验档都有一份由 Mohist 维护、带版本的完整 Slack 配置。用户不逐项勾选消息、文件或
交互权限；升级配置档时，旧版本仍按原能力工作，Mohist 明确提示何时需要重新安装。若工作区
不支持 Slack Agent，用户可以改用 Standard Bot，同一个 Mohist Agent 与已有执行历史不变。
Slack 原生 Agent 入口受工作区方案和成员类型限制；需要覆盖 guest 或不支持该入口的工作区
时，应选择 Standard Bot。Slack Agent App 不能在原 App 中切回 Standard Bot；选错体验档时
删除未完成 Connection，并为同一个 Mohist Agent 新建 Standard Bot 接入。

Slack Agent 的 Home 只承担备用控制面：展示 Bot 身份、是否可用、当前用户最近发起的工作，
以及回到对应 thread 或停止自己发起工作的操作。它不是 Workflow 看板或 Agent 编辑器，也
不会向一个成员展示其他成员的私有会话。

Slack Agent 不会把用户当前正在查看的频道、消息或页面自动加入 Agent 上下文。需要讨论某条
频道消息时，应在对应 thread 中 `@Bot`；需要其它内容时由用户在消息中明确提供。这样 Slack
入口不会因为用户只是浏览了某处就静默启动或改变 Agent 输入。

## 配置流程

### Web UI

1. 打开 Agent 详情页，在 **Connections** 中选择 **Add Slack**，选择 Standard Bot 或
   Slack Agent。Mohist 立即创建一条可恢复的 Connection，并展示当前步骤和下一步操作。
2. Mohist 展示将出现在 Slack 中的名称、头像、Agent overview 与能力，并提供 **Create in Slack**。该操作打开
   已预填完整配置的 Slack 创建页；下载配置文件是备用路径。若 Agent 名称不符合 Slack
   命名规则，Mohist 只生成并预览一个带稳定后缀的 Slack mention name，不修改 Agent 本身。
3. 在 Slack 中创建并安装 App，生成只含 `connections:write` 的 **App-level token (xapp-)**，
   取得 **Bot token (xoxb-)**，再填入 Mohist 的受保护表单。头像需要在 Slack App 设置中手动
   应用。凭据不出现在 Agent Instructions、消息、日志或 Session transcript 中。
4. 若 `mohist-slack` 尚未安装，页面进入 **Waiting for Slack service**，给出 `mo install slack`
   和 `mo service status slack`；已输入内容与进度不会丢失。服务可用后，Mohist 核对工作区、
   App、Bot 与当前可验证的能力。
5. token 无效、App 与 Bot 不属于同一安装或缺少必需权限时，页面进入 **Fix Slack setup**，
   只列出已确认的问题和重新打开 Slack 设置、替换凭据或重新验证的动作；不会让用户在
   **Claim owner** 上等待一个永远收不到的私聊。
6. 身份验证完成后，页面进入 **Claim owner**。配置者选择 **Generate owner code**，Mohist
   显示一个短时有效、只能使用一次的认领码；离开页面后不再回显，丢失时重新生成会立即使
   旧码失效。配置者在与 Bot 的私聊中发送该码；只有当前工作区中仍有效的正式成员才能成为
   Owner，外部协作成员、Bot 与已停用成员不能认领。一次成功的私聊认领也证明当前 App 能
   收取私聊并回复，但频道提及、文件和交互能力要在实际使用或测试后分别标为已验证。
7. 选择频道访问策略。默认是 **Owner only**；Allowlist 通过姓名和头像搜索工作区成员，
   不要求用户查找 Slack member ID。Owner 也可以在 Bot 的管理操作中使用 Slack 原生成员
   选择器完成同一设置。Allowlist 和 Anyone 都始终包含 Owner。
   为避免被拉入私聊后形成意外授权，私聊在所有策略下都只接受 Owner。认领完成且连接健康
   后，状态变为 **Connected**。
8. 把 Bot 邀请进目标频道，在私聊根消息中发送测试任务，或在频道根消息中 `@Bot`。测试
   结果同时可从 Agent 详情页的 Jobs 与 Sessions 中核对；Agent 尚未 Ready 时，Slack 明确
   显示安全的不可用摘要。具体 Runtime 或凭据缺口只向 Owner 以及 Web/CLI 操作者展示，
   Connection 本身仍保持 Connected。

安装进度由已确认事实决定：**Create app & add credentials**、服务离线时的 **Waiting for
Slack service**、验证失败时的 **Fix Slack setup**、身份验证后的 **Claim owner**，以及
**Complete**。Mohist 无法知道用户是否在外部 Slack 页面完成了 App 创建，因此不伪造一个
单独的“App 已创建”状态；没有凭据时，唯一下一步同时包含创建 App 与返回 Mohist 配置凭据。
已完成步骤可以返回检查，页面和 `connection view` 不能只显示 Setup required。

### CLI

先创建可恢复的 Connection：

```bash
mo agent connection create explorer --provider slack --experience standard
```

命令立即输出 Connection ID、Slack 身份预览和预填的 **Create in Slack** 地址，不要求
`mohist-slack` 已在线。`--experience agent` 选择 Slack Agent 体验档。完成 Slack 侧创建后
再配置凭据：

```bash
mo agent connection configure <connection-id>
```

`configure` 在终端中以隐藏输入读取凭据，不提供把 token 直接写进命令参数的方式。非交互
环境通过 `--credentials-file` 读取受保护文件。服务离线时命令保存进度并返回 Waiting for
Slack service；认领不要求该命令持续运行。身份验证完成后，`connection view` 会把下面的命令列为
唯一下一步：

```bash
mo agent connection claim-owner <connection-id>
```

它生成并只显示一次 Owner 认领码、有效期和 Slack DM 步骤；再次运行会使旧码立即失效。

凭据文件是 UTF-8 JSON，格式固定为：

```json
{
  "appToken": "xapp-...",
  "botToken": "xoxb-..."
}
```

不接受其它字段。Linux/macOS 上文件必须是普通文件、不能是符号链接，并且只能由当前用户
读写，例如 `chmod 600 slack-credentials.json`。Mohist 不会自动删除这个用户提供的文件。

```text
mo agent connection list <agent>
mo agent connection view <connection-id>
mo agent connection configure <connection-id> --credentials-file <path>
mo agent connection claim-owner <connection-id>
mo agent connection edit <connection-id> --access-policy allowlist --allow-member <slack-member-id>
mo agent connection rotate-credentials <connection-id>
mo agent connection transfer-owner <connection-id>
mo agent connection disable <connection-id>
mo agent connection enable <connection-id>
mo agent connection delete <connection-id> --yes
```

Web 与 CLI 配置的是同一个 Agent 接入，不建立两份本机配置。

`--allow-member` 可以重复；选择 Allowlist 时，它替换除 Owner 外的完整成员列表。Owner 不用
重复填写，也不能从 Allowlist 中移除。选择 Owner only 或 Anyone 时不能同时传
`--allow-member`，错误会在修改前返回。member ID 是 CLI 自动化入口；Web 与 Slack 界面都
使用成员搜索和头像，不把 ID 当作主要交互。

## 接入配置

| 配置 | 含义 |
|---|---|
| Agent | 固定绑定的 Mohist Agent；创建后不能改绑，换 Agent 应新建接入 |
| Slack workspace | Bot 所在工作区；由安装结果确认，不靠用户手填名称判断 |
| Bot identity | 创建时以 Agent 名称与头像初始化、之后由 Slack 管理并由 Mohist 核对的外部身份 |
| Slack overview | 从 Agent Description 生成的安装快照；Slack Agent 体验必填，空描述时 Mohist 生成非空通用说明 |
| Experience | Standard Bot 或 Slack Agent；决定 Slack 入口，不改变 Mohist Agent |
| Owner | 首次认领或后续转移时验证的 Slack 成员；默认唯一调用者，也是 Allowlist 的固定成员 |
| Access policy | 谁可以在频道中发起工作或继续会话；默认 Owner only |
| Allowed members | Allowlist 模式下通过成员选择器添加、可在频道调用的 Slack 成员；私聊仍只有 Owner |
| Setup progress | Create app & add credentials / Waiting for Slack service / Fix Slack setup / Claim owner / Complete |
| Status | Setup required / Connected / Degraded / Disabled；表示期望状态与连接健康，队列拥塞或 Owner 不可用会给出单独原因 |
| Capabilities | Identity、private message、mention、thread、file、actions 等分别为 Unverified / Granted / Observed / Missing / Failed |
| Identity sync | Bot 名称和头像是否仍与当前 Agent 一致；overview 因无法读取 App 配置而单独显示 Expected / Unverified |

Agent 的 Instructions、Runtime、Model、Variant、Skills 和并发限制不属于接入配置。执行
定义必须编辑 Mohist Agent，并从下一次新工作按快照生效；并发限制也由 Agent 编辑，但作为
实时调度策略作用于之后开始的 launch 与 follow-up。

Bot identity 也不是第二份 Agent 配置。Agent 名称或头像改变后，Mohist 会显示接入身份已经
不同步，并提供 Slack App 设置入口与正确的名称、头像；完成后重新验证。第一版不额外索取
Slack 管理员权限来替用户自动修改 App profile，身份不同步也不会伪装成连接中断。

Agent Description 只用于初始化 Slack App 的短说明和 Slack Agent 必需的 overview，不会变成
Instructions 或隐藏提示词。Description 改变后 Mohist 显示新的期望文案和手动更新入口；由于
Bot token 无法读取实际 App 配置，该项保持 Unverified，不能声称已经同步。

## Slack 消息权限

为了让用户在一个已经绑定的 thread 中自然追问而不必每条消息都重新 `@Bot`，Slack App
需要接收它已加入频道中的消息事件。Mohist 只处理以下消息：Bot 私聊、明确提及，以及已经
绑定 AgentSession 的 thread 回复；其它普通频道消息在交给 Mohist Agent 前即被丢弃，正文
不进入 Mohist 的持久记录或日志。

配置页必须在安装前列出所选体验档请求的 Slack 权限，并解释每项权限对应的功能。用户应只
把 Bot 邀请进确实需要使用的频道。第一版不提供零散权限开关，避免出现界面显示能追问或读
文件、实际 App 却没有完整能力的半配置状态。

两种体验档都读取工作区的基础成员目录，用于 Owner/Allowlist 选人、显示消息发起者，并排除
Bot、停用成员和外部工作区身份。Mohist 不读取成员邮箱，也不把成员目录交给 Agent；只保存
显示所需的成员 ID、名称和头像，并在 Connection 删除后清理。

Mohist 能核对 Bot 身份和已经授予的权限，也能通过真实收发确认某项能力；但在不索取 Slack
管理权限的前提下，不能读取 App 的全部配置。因此每项能力独立显示 **Unverified**、
**Granted**、**Observed**、**Missing** 或 **Failed**，不能仅凭 token 可用就声称所有能力
正常。

Socket 接入不要求 Mohist 暴露公网地址，但 Slack 对未确认消息只保留有限的重试窗口。配置页
应建议在工作区允许时启用 **Delayed Events**；即使启用，Mohist 也不承诺无限期补收。接入
服务长时间离线后，状态页必须提示可能遗漏消息，并让用户重新发送关键委托。

## Slack 中怎么使用

### 发起新工作

下面两种消息会发起一项新工作：

- 在与 Bot 的私聊中发送一条新的根消息；
- 在频道中发送一条新的根消息并 `@` Bot。

当前消息必须包含去掉 Bot mention 后的任务文本或至少一个可用附件。只发送 `@Bot` 不创建
AgentJob，Bot 会请用户补充任务；只发送附件可以作为明确输入，Mohist 不为它暗中编造提示词。

Mohist 接受后创建一个 AgentJob、一个 AgentSession、首条 SessionInput 与首个 AgentTurn。
Bot 先在对应线程中确认已经接受，显示当前是执行中还是排队中；排队时可取消，真正开始
执行后可停止当前执行。
Agent 回复、失败或需要人工处理的结论都回到同一线程。AgentJob 完成不关闭 thread；如果
回复是在提问，用户直接在 thread 中继续即可。

在一对一 DM 中，Bot 也始终把回复放进当前消息的 thread。回复该 thread 表示继续现有
Session；回到 DM 根部发送新消息表示开始一项独立工作，避免旧任务上下文静默混入新任务。

### 继续同一会话

对 Bot 回复所在的 Slack thread 继续发消息，会向已经绑定的 AgentSession 发送 follow-up：

- 不创建新的 AgentJob；
- 保留该 Session 已有上下文；
- 每条消息创建一条有稳定身份的 SessionInput；
- 当前 Turn 尚未开始时，连续消息按 Slack 接收顺序等待；已经执行时，支持追加输入的后端
  把消息加入当前 AgentTurn，否则等待后续 Turn；
- 空闲时收到 Input 则在同一 Session 中开始下一 AgentTurn。

每个 Session 的等待队列有明确边界。达到边界时，Bot 拒绝新消息并提示稍后重试；已经确认
接受的消息不会为了给新消息腾位置而被丢弃。

要开始一次有独立启动记录的新对话，应新开私聊根消息或频道根消息。同一个 Slack thread
可以有多个 Mohist Agent，各自拥有独立 AgentSession。规则是：

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

Slack 消息被接受后的编辑不会自动重跑；用户应发送一条 follow-up 说明更正。删除 Slack
消息也不会删除 Mohist 中已经形成的 AgentJob、AgentSession 或审计记录。

### 文件与链接

Bot 可以读取它在 Slack 中有权访问、且在本次消息或 thread 中明确提供的文件。文件作为
本次输入附件交给 Agent，并保留来源。无法读取、超过限制或类型不支持时，Bot 明确指出
哪个附件未被使用；不能假装已经读取。

链接保留为用户消息文本，是否打开由 Agent 已配置的 Skills 与 Runtime 权限决定。Slack
接入本身不抓取任意 URL，也不因链接出现在 thread 历史中就扩大网络访问。

## 回复呈现

- Slack 只展示适合用户消费的 Agent 回复、排队/执行/失败状态和必要的操作，不转发隐藏
  推理、原始工具输出或凭据。
- Agent 回复按文本渲染，不把其中看起来像 Slack mention、按钮或消息配置的内容当作控制指令；
  它不能意外触发 `@channel` / `@here`、伪造 Stop 操作或要求 Slack 自动展开外部链接。真正的
  操作按钮只由 Mohist 根据当前 Job、Session 和 Turn 状态生成。
- Slack 回复必须包含足够完成当前决策的结论、证据摘要和下一步，不能要求用户进入 Mohist
  Web 才知道结果。长结果可以在同一 thread 中分段呈现。
- 只有管理员配置了 Slack 用户可访问的 Mohist Web 地址时，消息才显示 **Open in Mohist**；
  localhost 地址绝不发送到 Slack。没有可用 Web 地址时显示稳定的 Job / Session 标识，供
  Web 或 CLI 的备用操作面继续查询。
- AgentJob 与每个 AgentTurn 的执行结果由 Mohist 决定。Slack 回复发送失败不会把已经完成
  的执行改成失败。
- 第一版可以显示结果中的文字、已存在且可访问的外部链接、artifact 名称和稳定标识，但不把
  Mohist artifact 自动复制成新的 Slack 文件。需要文件本体时使用结果提供的原始交付位置，
  或由 Web/CLI 备用平面读取；这不影响 Slack 中必须给出的结论与下一步。
- Slack 明确没有接受回复时，接入按平台要求重试并进入 Degraded。若发送结果无法确认，接入
  先在原 thread 中核对；仍不确定时不会盲目再发一条，而是在 Connection 页、CLI 和可用的
  Owner 诊断中显示 **Delivery uncertain**。人工重发会明确警告可能出现重复回复；用户也可
  先查看 Mohist 中的执行结果。
- 重连后从最后确认的位置继续，不重复创建 Job、重复提交输入或重复发送已经确认的回复。

## 权限

| 策略 | 私聊 | 频道提及和已绑定 thread |
|---|---|---|
| Owner only | 只有 Owner | 只有 Owner；默认值 |
| Allowlist | 只有 Owner | Owner 和明确列出的工作区成员 |
| Anyone | 只有 Owner | 能证明属于 App 安装工作区、且能在当前频道看到 Bot 的成员 |

这沿用 Buzz 的 DM hardening：即使频道策略是 Allowlist 或 Anyone，也不会让偶然进入 Bot
私聊的成员获得调用权。频道成员关系只决定 Bot 能否收到消息，不代替 Access policy。Slack
Connect 中的外部参与者和归属无法确认的身份在第一版均不触发 Agent；Anyone 不是“任何能
看见消息的人”。Bot 被邀请进私有频道后，仍
按接入策略检查发送者。无权调用的用户会收到简短且明确的拒绝，不会创建 AgentJob 或
AgentSession。

停止或取消某个 AgentTurn 只能由 Connection Owner 或发起该 AgentSession 的 Slack 成员
执行。其他被允许的成员可以继续 thread，但不能停止别人的执行；过期按钮也不能误停后来
开始的 Turn。

Access policy 只回答谁可以调用这个 Agent，不削减 Agent 本身已经配置的 Runtime、Skills、
仓库或工具能力。Slack 用户也不能通过消息临时增加或替换这些能力；Agent 权限仍只有 Mohist
中的一份配置。

每次输入都记录 Slack 工作区、频道、thread 和发送者身份用于审计，但 Slack 身份不会被
当成 Mohist 管理员身份，也不能借普通消息内容切换 Project、Agent 或访问策略。只有 Owner
使用明确的 **Manage access** 操作时，才能修改这个 Connection 的调用范围。

访问策略修改立即作用于之后收到的每条输入，包括已有 thread 的 follow-up；它不撤销已经
接受的执行，也不删除历史。Owner 始终在 Allowlist 中。转移 Owner 只能由有权管理该
Connection 的 Mohist 操作者发起：系统生成新的单次认领码，新 Owner 在 Bot 私聊中认领后
原子替换，旧 Owner 在此之前保持不变。新 Owner 同样必须是当前工作区中仍有效的正式成员。

## 生命周期与异常

| 情况 | 产品行为 |
|---|---|
| Agent 名称或头像被编辑 | Mohist 立即更新 Agent，并把 Bot identity 标为不同步；用户在 Slack App 设置中修改后重新验证 |
| Agent 描述被编辑 | 更新 Mohist 中的发现信息，并显示新的 Slack overview 期望值；不改变 Agent 行为或自动申请 Slack 管理权限 |
| Agent 执行定义被编辑 | 新 AgentJob 使用新快照；已有 Session 保持原配置 |
| Agent 并发限制被编辑 | 下一次 launch 或 follow-up 按最新限制调度；不强停已在执行的输入 |
| Agent 被归档 | 拒绝新的根委托；已有 Session 仍可查看和继续 |
| Agent 变为 Needs setup | 连接健康保持独立；新的根委托在频道中只显示安全摘要，Owner 与 Web/CLI 操作者可查看具体配置缺口；已有 Session 按自己的执行快照继续 |
| Agent Readiness 为 Unknown | 接受新的根委托并显示“等待 Runner 验证”；确定无法执行后由 AgentJob/Turn 返回明确失败 |
| 接入 Disabled | 立即停止接受 Slack 输入和发送回复；已接受执行仍在 Mohist 继续。接入服务会确认并丢弃禁用期间到达的消息，不让它们积成稍后的任务。重新 Enable 后只补齐缺失的当前/最终结果，不回放过期进度，也不接收禁用期间被 Slack 延迟重投的旧消息 |
| 接入 Deleted | 删除 Slack 凭据、连接关系和未用于输入的临时文件；不删除 Agent、AgentJob、AgentSession 或已接受输入的附件，也不代替用户从 Slack 卸载 App |
| Slack 凭据失效 | 接入进入 Degraded 并停止接受新输入；替换凭据时必须仍验证为原 workspace、App 与 Bot，否则新建接入 |
| 同一 Agent 已接入该 Slack workspace | 不覆盖已有 Connection；清除新凭据并指向已有接入，用户删除重复 Connection，并在 Slack 卸载刚创建的重复 App |
| `mohist-slack` 暂时离线 | 已创建的 Connection 与配置进度保留；新消息由 Slack 在其重试窗口内重投，超出平台保留窗口的消息可能需要用户重新发送 |
| Owner 需要更换 | Mohist 操作者发起新的单次认领；成功前不移除旧 Owner，也不允许 Slack 发送者自行重置 |
| Owner 离开工作区、被停用或变为 guest | 接入进入 Degraded 并提示 Owner unavailable；不自动把身份转给同名成员。频道中的 Allowlist / Anyone 可继续按原策略使用，私聊和 Owner 管理操作不可用，Mohist 操作者需要发起 Owner 转移 |
| Slack 重复投递事件 | 返回已有 Job/Session/Input/Turn，不创建第二份输入 |
| Mohist 暂时无法确认 Input 是否已交给执行后端 | 标记为未知并核对同一个 Input，不用新请求自动重放 |
| 超过 Agent 并发限制 | 明确显示排队，不把容量不足伪装成执行失败 |
| Owner 或 Session 发起者取消 queued Turn | 立即从队列移除；首个 Turn 对应的 AgentJob 以 `cancelled` 失败结束 |
| Slack 无法发送回复 | 工作结果保持不变；明确未接受时自动重试，发送结果未知时显示 Delivery uncertain，不盲目重复发送 |
| 接入待处理队列达到上限 | 接入进入 Degraded 并显示 Backpressured；不丢弃已接受消息来腾空间，Slack 保留窗口外的新消息可能需要用户重新发送 |

## 非目标

- Slack Bot 不运行 Agent Runtime，也不读取 Runner 或数据库。
- Slack Bot 不拥有 Agent 配置，不通过隐藏 prompt 修改 Agent 行为。
- 不在 Slack 中提供 Agent 编辑器、Workflow 看板或完整诊断工作台。
- 不让一个共享 Bot 根据自然语言猜测用户想调用哪个 Agent。
- 不把普通频道消息全部发送给 Mohist；只有私聊、明确提及和已绑定 thread 的回复会触发。
- 第一版不解决公开应用市场、多租户托管、计费、Slack Connect 外部成员调用或跨组织目录发现。
- 第一版不协调由不同 Mohist Server 管理、但位于同一 Slack 工作区中的 Bot。
- 第一版不支持 Slack group DM。
- 第一版不把 Mohist artifact 自动上传为 Slack 文件。

## 实装差距

Agent 接入、`mohist-slack` 服务及以上 Slack 行为尚未实装。当前 Web UI 与 CLI 已有 Agent
创建、启动、读取和继续会话的基础路径，但尚未达到“Agent 独立可用”的完整产品契约；在接入
Slack 前还需要补齐 SessionInput/AgentTurn、Slack 接入服务身份验证、重复请求保护、可断线
续读的执行状态、Agent Skills 执行和并发限制。技术边界与实施顺序见
[`design/agent-api.md`](../design/agent-api.md) 和
[`design/slack-agent-connection.md`](../design/slack-agent-connection.md)。
