---
status: wip
---

# Subagent 与会话树设计

会话树是 AgentSession 之间的可选父子关系。它让一个已启动的 Mohist Agent
在运行时委托新的 Agent launch；它不创建 Agent 层级、消息类型、Session 终态或
工作流编排器。

产品行为见 [`../docs/subagents.md`](../docs/subagents.md)。AgentJob、AgentSession、
SessionInput 与 AgentTurn 的基础生命周期仍由 [`agent-execution.md`](agent-execution.md)
定义；本篇只定义它们在一次 spawn 中如何组合。

## 边界

- Agent 资源始终按 Project 平铺。Subagent 只是某个 child AgentSession 在父子关系中的角色。
- 子委托仍是普通 Agent launch：一个 `AgentJob`、一个 `AgentSession`、首个
  `SessionInput` 与首个 `AgentTurn`。不得新增第二条 launch 或 Runner dispatch 管道。
- `AgentSession.Source` 说明这段会话为何创建，且不可变。父子关系是独立的
  `SessionParentLink`，不属于 Source，detach 不得改写 Source。
- Server 解析能力、身份、工作目录和 Runner，持久化 link、操作与消息受理；Runner
  只执行已经解析且已经 pinned 的 child work。
- `SessionInput` 和 `AgentTurn` 是父子之间的唯一消息模型。终态回报、steer 与求助
  都不能另建 inbox、message aggregate 或 transcript 分支。
- 会话树提供 spawn、inspect、通知、stop 与 detach 原语。fork-join、等待策略、重试、
  任务推荐、任务拆分和验收仍由 Agent 自己的 Instructions 与 Skills 决定。

## 模型

### Capability declaration 与 launch snapshot

Agent 定义保存 `AllowedSubagentAgentIds`：同一 Project 内、按稳定 Agent ID 引用的有序
集合。它不复制目标 Agent 的 Instructions、Runtime、Skills 或并发配置。

每次 Agent launch 把这组声明解析为不可变 `AllowedSubagentSnapshot`：

```text
AllowedSubagentSnapshot
  AgentId
  NameAtLaunch
  DescriptionAtLaunch
```

该 snapshot 是 parent AgentJob 的执行定义的一部分，并写入它创建的 AgentSession 设置。
同一 Session 的 follow-up 不重新解析能力声明。Server 把每个 Session 自己的 snapshot
放进该 Session 的启动上下文；这不是环境变量，也不是客户端临时输入。

名称和状态的规则如下：

| 情况 | 规则 |
|---|---|
| 配置声明 | 只保存目标的稳定 ID；目标必须属于同一 Project。 |
| 父 launch 后目标改名 | 父 snapshot 保留原 name/description；spawn 时用当前 name 或 ID 解析到同一 Agent ID 后仍可授权。 |
| 父 launch 后目标 archive | 尚未接受的 spawn 被拒绝为 `target_agent_archived`；已被 launch coordinator 接受的 child 不因后续 archive 被撤销。 |
| 目标恢复 active | 新的 spawn 可再次使用该已声明 ID；已有 snapshot 不需要修改。 |
| self-spawn | 只有本 Agent 的稳定 ID 被显式列入声明时才允许；它和其它 target 一样走普通 launch scheduling。 |
| 跨 Project | 一律拒绝。父 Session、目标 Agent、child Job 和 child Session 必须属于同一 Project。 |

Agent archive 不自动删配置中的 ID。这样恢复 active 后配置仍有确定含义；但 archive
绝不允许新的 child launch。删除 Agent 的能力不在本设计范围内。

### SessionParentLink

child AgentSession 拥有一条可选 `SessionParentLink`。child 是 link 的唯一写入权威，
因为只有它能获得或失去自己的单一父会话；parent aggregate 不保存可修改的 child 列表。

```text
SessionParentLink
  EdgeId
  ParentSessionId
  ParentAgentId
  ChildLaunchJobId
  AttachedAt
  AttachedRevision
  State: attached | detached
  DetachedAt?
  DetachedRevision?
  TerminalReport: none | pending | delivered | suppressed
  TerminalReportDeliveredInputId?
```

`ChildLaunchJobId` 是这次委托的初始 AgentJob，不是 child Session 的终态标志。
link 在 child 的初始 launch 建立，之后只允许 `attached -> detached`。不支持 attach
已有 Session、reparent 或重新接回树。

这组限制直接保证：

- child 最多一个当前 parent；
- 新建 child 在建立 link 前没有祖先，且 link 永不改指向，因此不会形成 cycle；
- detach 后 child 和它仍 attached 的 descendants 成为另一棵树，Source、workDir、
  Runtime binding、transcript 与 AgentJob 都保持不变；
- 历史 link 保留 ParentSessionId、EdgeId、DetachedAt、DetachedRevision 和已投递的 parent
  InputId 供审计；默认 tree 查询只返回 `attached` 边。

### Tree mutation fence 与 graph revision

每个 Project 有一个持久化的 `SessionTreeMutationFence`。它是 attachment、detach 和
cascade-stop snapshot 的唯一线性化点，同时维护严格递增的 `GraphRevision`。它不是第二份
树模型：边的权威仍是 child-owned `SessionParentLink`，fence 只持有未完成计划的
`LinkReservation`、pending mutation command 与一次 mutation 所需的短事务。

```text
LinkReservation
  EdgeId
  ParentSessionId
  ChildSessionId
  State: reserved | attached | rejected
  RejectionReason?
```

reserve 不产生可见树边。finalize attachment 在同一 fence 内再次确认 parent 的 authoritative
workDir、expected binding 和 stop admission。fence 自己不写 link：它先持久化带稳定 command
identity 和 assigned revision 的 pending attach/detach mutation，再调用 child Session 的幂等 link
transition。child 在自己的同一事务写 link/index acknowledgement 与 `AttachedRevision`，或
`DetachedAt`/`DetachedRevision`；fence 收到 acknowledgement 后才把 `GraphRevision` 推进到该
assigned revision 并使变更可见。crash 后先重放同一个 child mutation，直到它已确认，才执行
后续 fence command。这样 child 仍是 link 的唯一写入权威，且不会把半完成的 cross-store
mutation 暴露给 tree read。reserved/rejected reservation 永不出现在 `mo session tree`。

tree read 先以当前 Project `GraphRevision` 固定一个 topology snapshot。edge 在 revision `R`
存在，当且仅当 `AttachedRevision <= R` 且 `DetachedRevision` 为空或大于 `R`。查询从 root
递归读取该集合，再批量关联当前 Session summary；不逐节点激活 Session grain，也不扫描
Project 中无关 Session。

返回顺序固定为 breadth-first。每一层的 sibling order 是 `(AttachedRevision, EdgeId)`；递归
读取为每个节点构造该排序键的 ancestor path，最终按 `(depth, ancestor path)` 排序。首个
page 的 opaque cursor 固定 Project、root、revision 和最后一个 `(depth, ancestor path)`；后续
page 用该 cursor 重放同一 topology snapshot。因此同一 cursor 链不会重复或遗漏节点，detach
或 attachment 的并发变更只会由没有 cursor 的新查询观察到。cursor 不匹配 Project/root 或
无效时拒绝，不得悄悄改用最新 revision。page/continuation 只限制一次诊断读取，不定义或
拒绝业务树的深度、宽度或总节点数。

### Operational bounds

会话树没有业务性的深度、宽度或 attached-node admission cap。正常 Agent 的
`MaxConcurrentRuns`、launch queue capacity、Session input capacity、Runner capacity 和
storage retention policy 继续照常生效：child launch 在 target Agent 忙碌时按普通 AgentJob
排队，容量不足时遵守普通 launch 的可见背压语义。tree relation 不另建资源调度器，也不以
structural count 拒绝 spawn。

## Spawn

### 调用面

CLI 的规范命令是：

```bash
mo agent spawn <agent-ref> --project <project-id> --parent-session <session-id> --prompt "<brief>" --idempotency-key <key>
```

`--parent-session` 是显式 caller identity。CLI 不得从当前工作目录、Runtime Session、
进程环境或最近一次 launch 猜 parent。`--idempotency-key` 是必填调用身份；CLI 在网络
失败后必须用同一个 key 重试。

Server 的规范调用面是：

```text
POST /api/projects/{projectRef}/agent-sessions/{parentSessionId}/spawns
Idempotency-Key: {key}

{ "targetAgentRef": "reviewer", "prompt": "..." }
```

path 中的 `parentSessionId` 与 idempotency header 共同构成一次 spawn 的 caller 和重放
边界。body 不接受 `workDir`、`workspacePath`、Runner、Runtime、Instructions、Model、
Skills 或任意 filesystem path。一个 key 在 `(ProjectId, ParentSessionId)` 内只能表达一种
canonical request；prompt、target 或 caller 不同的重放返回 conflict。

`parentSessionId` 是委托 authority 的显式身份，不是新的 bearer credential。现有调用方
认证先决定它能否操作 Project；Server 再从该 Session 持久化的 launch snapshot 验证它能否
委托 target。first release 不为每个 Session 创建或传播新的进程凭据。

`targetAgentRef` 由 Server 在首次接受时解析为稳定 Agent ID。target 名称改动后，旧名称
不再是有效 ref；Agent 应先发现当前名称或直接使用稳定 ID。响应复用普通 launch 的
`AgentJob`、`AgentSession`、首个 Input、首个 Turn 和 observation 引用，并额外返回
parentSessionId 与 edgeId。

### 接受条件

Server 在写入 coordinator plan 前必须同时确认：

1. parentSessionId 属于请求 Project，并带有不可变 Agent execution definition、capability
   snapshot 与 canonical AgentId；直接 launch 与 Agent Connection 的 Mohist Agent Session 都可
   满足这一条，Workflow inline Session 不满足；
2. parent Session 的 capability snapshot 含解析后 target 的稳定 Agent ID；
3. target 当前为 active，且其 launch definition 与 readiness 可以被正常解析；
4. 普通 Agent launch 的 readiness 与 queue acceptance 允许新 child；target 的
   `MaxConcurrentRuns` 只决定随后是否排队；
5. parent Session 有当前可用的 authoritative `WorkDir`；spawn body、Agent Connection 对话、
   调用方进程路径或其它 Session 的目录都不能提供、替换或升级它；
6. parent Session 有当前、已确认、可用的 Runtime binding，含 `RunnerId`、runtime 和
   runtimeSessionId；activity 为 `unknown`、binding 缺失、Runner 不再存在，或 binding
   与 Session workDir 不一致时都不能 spawn；
7. parent 所在 attached ancestor 没有尚未达到 terminal outcome 的 cascade stop operation。

第 5、6 条一起定义 first-release shared-workdir：child 的 workDir 来自 parent AgentSession
已持久化的 authoritative workDir，不是客户端给的路径；child admission 还必须 pin 到该 parent
binding 的 RunnerId。不能只复制 path 后让普通 AgentJob 在任意 eligible Runner 上调度，因为
该目录可能只存在于 parent Runner。

当前 Agent Connection launch 以 `WorkspacePath = null` 创建 Session，且没有可用的 authoritative
workDir 或 parent Runner binding。因此它在第 5 或第 6 条被拒绝为
`parent_workdir_unavailable` 或 `parent_runner_binding_unavailable`；不得以 Slack 对话、调用方
路径或某个其它 Session 的目录补齐。后续若 Agent Connection launch 自身取得这两项既有事实，
它即可按同一接受条件成为 parent，不另建 Connection 专用通道。

不能确认上述事实时拒绝而不是回退：

| 条件 | 结果 |
|---|---|
| parent 没有 workDir | `parent_workdir_unavailable`；不创建 child。 |
| parent binding 缺失、unknown、过期或没有可用 Runner | `parent_runner_binding_unavailable`；不创建 child。 |
| target 不在 snapshot 或已 archive | `subagent_not_allowed` 或 `target_agent_archived`；不创建 child。 |
| 有尚未达到 terminal outcome 的 cascade stop | `parent_tree_stop_in_progress`；不创建 child。 |

Runner 离线但 binding 仍是当前事实不构成换 Runner 的许可；它仍是
`parent_runner_binding_unavailable`。该 pre-plan rejection 已冻结原 idempotency key；等待
Runner 恢复后，调用方必须使用新 key 重新请求，而不是把 child 投到另一个 Runner。

### Coordinator、原子性与恢复

spawn 扩展现有按 idempotency key 持久化的 `AgentLaunchCoordinator`。它不新建
`SubagentLauncher` 或第二个 Job pipeline。coordinator key 以 parentSessionId 作为 scope
的一部分。它先持久化不带 child identity 的 `SpawnRequestFence`：

```text
SpawnRequestFence
  ProjectId
  ParentSessionId
  IdempotencyKey
  RequestFingerprint
  Outcome: validating | rejected | admitted
  RejectionReason?
```

这个 fence 是 `(ProjectId, ParentSessionId, IdempotencyKey)` 的 canonical request authority。
它只冻结 caller/key/fingerprint 与 rejection，不创建也不预留 Job、Session、Input、Turn、edge
或 reservation identity。相同 fingerprint 的 replay 返回已持久化的 rejection，或恢复同一个
admitted plan；不同 fingerprint 返回 idempotency conflict。包括 binding unavailable 和
`parent_tree_stop_in_progress` 在内的 transient pre-plan rejection 也不以同一 key 重试；调用方
必须使用新 key 重新请求。

只有 request fence validation 通过后，coordinator 才把它推进为带 child identities 的 launch
plan。plan 额外持久化：

- SpawnOrigin：parentSessionId、parent Agent ID、edgeId 和 caller key；
- parent workDir、pinned RunnerId 与完整 expected parent binding；
- target 的稳定 ID、展示 snapshot 和 child execution definition；
- 正常 launch 已有的 Job、Session、Input、Turn identity。

plan 写入后不重新读取 mutable target Agent 或 parent capability snapshot。它用既有的
`PrepareJob -> EnsureInitialLaunch -> SubmitJob` fences，并扩展为带 reservation、final check
和 abort 的单一 launch pipeline：

```text
persist request fence with fingerprint
  -> pre-plan validation
  -> reject request fence with no child artifacts, or persist launch plan and reserve EdgeId
       at SessionTreeMutationFence
  -> prepare child AgentJob with pinned RunnerId and inherited workDir
  -> create child AgentSession + initial SessionInput + initial AgentTurn
  -> final check reservation, parent workDir, binding and stop admission
  -> finalize the child-owned SessionParentLink through the fence mutation protocol
  -> submit the same prepared AgentJob to its pinned Runner
```

pre-plan validation 失败时，`SpawnRequestFence` 持久化同一 canonical rejection，但不持久化
launch plan、reservation 或 child identity，也不创建 Job、Session、Input、Turn 或 link。这是
一次未接受的 validation rejection，不是可恢复的 launch；同一 key 只会重放它，不能重新验证。

plan 一经持久化就保存 immutable child identities、expected workDir/binding 和
`LinkReservation`。同 key 的重放只能恢复它的相同结果，不能重新选择 parent、target、
workDir 或 Runner。每个后续命令都有稳定 command identity，重复执行必须得到
already-applied acknowledgement；coordinator reminder 从该 durable fence 恢复。

reservation 先占住 EdgeId，但不是 tree edge，也不让 child 对外可见。child Job、Session、
Input 与 Turn 在 final check 前都只是 provisional artifacts：`mo session tree`、正常 spawn
success response 与普通 Session command 不能发现或操作它们。initial Turn 可以是 queued，
Session activity 也可以因此为 active；它只表示 coordinator 正在恢复该计划，绝不表示 work
已被提交给 Runner。

finalize attachment 必须在 `SessionTreeMutationFence` 内再次比较 parent 的 expected workDir、
binding 与 stop admission。成功的 child link acknowledgement 才写 attached `SessionParentLink`，使 child 对 tree 和
success response 可见。`SubmitPreparedLaunch` 还必须携带该 plan 的 attached reservation；所有
submit/recovery 路径都检查它。没有 attached reservation、或 plan 已进入 rejection fence 时
一律 `must-not-submit`，即使 Job 仍是 pending。

final check 发现 parent reset、workDir/binding 改变、reservation 被 stop snapshot 拒绝（reason 为
`parent_tree_stop_in_progress`）或其它不可恢复冲突时，先把 plan 持久化为 `rejected`，再以稳定
abort command 收敛 provisional artifacts：reservation 记为 `rejected`，prepared Job 终结为 `cancelled` 且 reason 为
`parent_link_rejected`，initial Turn 终结为 `cancelled`，Session activity 回到 `idle` 而
AgentSession 仍非终态。已写的 initial Input 只保留作该 rejected plan 的审计，不对普通
Session 输入、tree 或 launch success 可见。没有 `SessionParentLink`，所以这个取消不是一次
已接受 delegation 的 terminal report，也绝不向 parent 追加 Input。

abort 本身可在任一 participant 调用后中断；重放同一 abort command 必须完成其余 participant
并保留相同 cancelled/rejected 结果。若 rejection 发生在某个 provisional artifact 之前，该
artifact 不创建，其余已创建 artifact 仍按上述状态收敛。replay 返回相同的 durable rejected
outcome 和 reason，不暴露未被接受 child 的常规 Job/Session 成功引用。已成功 finalize
attachment 的 plan 重放不会因为目标 rename/archive 或 parent 配置编辑而改变 child。

child Job dispatch envelope 带 `PinnedRunnerId`。AgentJob admission 只能 claim 这个 Runner；
它不可用时 Job 保持普通的 pending/retry 状态，不能被全 Project eligible-runner 选择逻辑
迁移。Runner 收到的仍是已解析的 prompt、workDir、runtime 与 binding constraint，不参与
选择 parent、解析 capability 或物化任意路径。

## Startup-known context

Server 为每个 Agent launch 创建不可变 `AgentSessionStartup`，与 execution definition 一起
持久化并在第一轮 dispatch 前以 Runtime 支持的 startup/system context 交给 Agent。它和
用户提供的 read-only `AgentStartupContext` 不同，不能借用后者的外部讨论语义。

```text
AgentSessionStartup
  ProjectId
  SessionId
  ParentSessionId?
  AllowedSubagents: [{ AgentId, NameAtLaunch, DescriptionAtLaunch }]
  SpawnCommand:
    mo agent spawn <agent-ref> --project <project-id>
      --parent-session <session-id> --prompt "<brief>" --idempotency-key <key>
```

这段 Server-originated context 必须在 task prompt 前可见，并明确说明：选择 target、生成
brief、生成唯一 key、等待、重试和验收都由 Agent 决定。child 还得到 ParentSessionId，
所以它需要求助时可用普通 `mo session followup <parent-session-id>`。

不得为此设置 `MOHIST_SESSION_ID` 或任何 per-session process environment variable。Runtime
没有系统 context 通道时，Runner 只能接收 Server 已明确标记的 startup block；它仍不得自行
读取环境、workDir 或会话文件来反推身份。首次 launch 后的 Session settings 是 restart 和
follow-up 的唯一 snapshot 来源。

## 消息与 child terminal report

### 普通跨会话输入

父向 child steer、child 向 parent 求助，均使用既有 `mo session followup` 和 canonical
`SessionInput` / `AgentTurn` 路径。tree relation 只提供可发现的 Session ID 和启动上下文，
不会为任一方向发明消息格式、独立队列或特殊 Runtime protocol。

### 终态的权威触发

AgentSession 永不进入 terminal state。一次 child delegation 的终态是它由 spawn 创建的
`ChildLaunchJobId` 进入 `completed`、`failed` 或 `cancelled`。`unknown` 不是终态，
初始或后续 `AgentTurn` 的终态也不是 report trigger。

AgentJob 在这个状态转换同步持久化一个带 SpawnOrigin 的 terminal event。它携带 child
Job/Session ID、status、initial Turn ID、结果观察引用和 EdgeId；它不写 parent，也不把完整
transcript、output 或自然语言结果复制进 parent。event handler 随后在 child-owned link 上
claim report，因此 link 中的 `TerminalReport` 才是投递义务的唯一状态。

### 持久且幂等的投递

terminal report 在 Server 的 at-least-once event handler 中按下面顺序处理：

```text
AgentJob terminal event
  -> child Session atomically claims report on its attached link
  -> append a normal parent SessionInput
  -> parent Session accepts/reuses its AgentTurn according to normal rules
  -> child link records delivered parent InputId
```

claim 与 `attached -> detached` 在 child Session 的同一事务竞争：

- detach 先提交，claim 记为 `suppressed`，不再生成 report；
- claim 先提交，report 记为 `pending`，后续 detach 不撤销该已发生的 delegation report；
- 已投递的 parent Input 永不因 detach 删除。

parent Input 使用确定的 idempotency key
`subagent-terminal:{edgeId}:{childLaunchJobId}`，并带 source `subagent-terminal` 与结构化
provenance（child session/job/turn/result reference）。它的可见正文只说明 child、终态和
可查询结果位置。重复事件、activation loss 或 handler retry 因此最多产生一个逻辑
SessionInput；同 key 重放返回原 Input 与 Turn。

parent idle 时，这条 Input 按普通 follow-up 创建新的 Turn；parent active 时按普通输入
顺序加入当前或后续 Turn。parent input capacity、unknown activity 或暂时不可达时，link 保持
`pending`，Server 在 parent capacity/activity 变化和 dispatcher recovery 时以同一 key 重试。
它永不静默丢弃，也不把 child AgentJob 的终态回滚为未完成。report delivery 与 child Job
结果是两个独立的 durable facts。

## Cascade stop 与 detach

### Cascade stop

`mo session stop <session-id> --idempotency-key <key>` 是一次 `SessionTreeStopOperation`，
不是把任一 AgentSession 标记为 stopped。它的 API 以 root Session 与 idempotency key
创建可读取的 operation resource；同 key 的重试恢复同一 operation，新的 key 才表示
新的 stop 请求。

Server 在 operation 被接受时，以一个已排在所有先前 fence mutation 之后的
`SessionTreeMutationFence` command 持久化 root、tree membership snapshot、snapshot
`GraphRevision`、每个 target 的当时 current turn/binding 和稳定 child stop-operation IDs。
membership 是 root 及该 revision 的所有 attached descendants。已开始的 attachment/detach
mutation 必须先恢复到 acknowledgement，才可接受 stop snapshot；同一 fence 也处理
reservation、final attachment 和 detach，顺序定义如下：

| 先线性化的操作 | 后果 |
|---|---|
| detach 先于 stop snapshot | subtree 在 snapshot 之外；之后的 stop 不影响它。 |
| finalized attachment 先于 stop snapshot | child 在 snapshot 内；它的 current work 按普通 target 规则处理。 |
| stop snapshot 先于 detach | 当时 attached subtree 的 IDs 固定写入 snapshot；之后 detach 也不将其移出，仍按该 snapshot 停止。 |
| stop snapshot 遇到较早但尚未 finalized 的 reservation | reservation 记为 rejected，child 不进 snapshot，coordinator 只能 abort，绝不 submit。 |
| stop operation 尚未 terminal 时的新 reservation 或 final attachment | 拒绝为 `parent_tree_stop_in_progress`；operation terminal 后必须用新 key 创建新的 child，它不属于旧 snapshot。 |

因此并发操作没有“读取一棵正在变化的树”这个中间状态。stop retry 从持久化的 target IDs、
expected turn/binding 与 child stop-operation IDs 恢复；它不重新遍历树，也不重新决定 detach
或 attachment 是否属于该次 stop。

若 snapshot 包含一个已经 attached、但尚未 Submit 的 child，target sub-operation 以它的
initial Job/Turn identity 取消该 queued work，并把该 cancellation 写入同一 plan 的 submission
gate。之后 coordinator 或 reminder 都不得 submit。此时 link 已经 accepted，所以 Job 的
`cancelled` terminal result 仍按 normal terminal-report 规则处理；这不同于 reservation 被
rejected 时完全没有 callback 的 abort。

每个 target 只执行 Server 的现有 turn-control 语义：

| target 当时状态 | 该 operation 的结果 |
|---|---|
| 没有未终结 Turn | `already-idle`；Session 继续存在。 |
| queued Turn | `cancelled`；不联系 Runner。 |
| executing Turn | Server 向该 target 的 expected binding Runner 请求 stop，并记录 `stop-requested`。 |
| Runner 未收到请求 | `pending`；可以安全重试同一 sub-operation。 |
| Runner 可能已执行但结果无法确认 | `unknown`；绝不伪造 idle 或取消。 |
| target 已替换 binding/Turn | `rejected`；不对后来出现的工作发 stop。 |

operation summary 只从 per-target facts 推导：全部确定为 `completed`；确定成功与
`rejected` 混合为 `partial`；任一不可确认结果为 `unknown`；尚有安全投递为 `running`。
只有 `completed` 与 `partial` 是 terminal outcome。`running` 与 `unknown` 都保留 stop
admission fence；后者不是安全完成，必须以原 operation retry/reconcile 到确定结果，期间
新的 spawn 继续拒绝为 `parent_tree_stop_in_progress`。重试只重新核对或重发原 target 的
同一 persistent sub-operation ID。它不会重新遍历树、创建 child、重放输入，或停止 operation
snapshot 之外后来开始的 Turn。

Session 因此没有 terminal lifecycle：cascade stop 结束的是 snapshot 中的 current work，
不是关闭会话。完成后 parent 或 child 可以在另一次明确 follow-up 中继续；那是新的 Turn，
也不会被旧 stop operation 追杀。

### Detach

`mo session detach <child-session-id>` 要求 child 当前 attached。它在 child Session 中把
link 作为一个 fence mutation 改为 `detached`：fence 先持久化 command/revision，child 再以
幂等 transition 写入 detach revision/index acknowledgement，最后由 fence 发布该 revision。
它返回被移除的 parent 和 edge identity，不 cancel child、不会移动 workDir、不会修改 child
Source，也不会把 child 转换成新 Agent 资源。已 detached child 是新的 tree root；它的
attached descendants 保持原来的结构。

detach 是幂等的目标状态：重试已经 detached 的同一 child 返回该 historic link。它不能
reparent 到另一个 Session。终态 report 与 detach 的竞争规则以本篇“持久且幂等的投递”为
唯一权威。

## Delivery increments

| Increment | 独立用户价值 | 依赖与范围 |
|---|---|---|
| 1. Spawn and inspect | 具备 authoritative workDir 与当前 Runner binding 的已授权 Mohist Agent parent 能 spawn 一个已声明的 child，并从 `mo session tree` 看见 child、Job 与 Session。当前 Agent Connection parent 因缺少这些事实被拒绝。 | capability stable IDs and launch snapshot、explicit caller/key、parent workDir plus pinned Runner binding、existing launch coordinator extension、reservation/finalization/abort gate、link/index/revision-pinned paged tree query。没有 terminal callback、cascade 或 detach。 |
| 2. Terminal callback | parent 会收到 child launch job 的一次终态 Input，并可按引用检查结果。 | 依赖 1 的 edge 与 Job identity；实现 pending/delivered/suppressed report recovery。 |
| 3. Cascade and detach | parent 能停止当前 attached subtree 的工作；需要留下 child 时可 detach。 | 依赖 1 的 link index；实现 `SessionTreeStopOperation`、fence、partial/unknown/retry 与 detach race rules。 |
| 4. Managed worktree | child 可以请求由 Project Space 定义且由 Runner 物化的隔离工作空间。 | 依赖 1 的 link/launch model 与 Project Space/Runner materialization contract；不接受任意 Server path。 |

Increment 1 不需要等待后续生命周期能力即可交付。它必须用 coordinator recovery 证明：在
reserve、prepare、initial Session creation、final link check、abort 和 submit 任一步中断后，
同一个 idempotency key 恢复同一 accepted child 或同一 durable rejection；child 永远不会在
缺 link、rejected reservation、已被 snapshot 取消或错误 Runner 上 dispatch。

## Non-goals

- 不引入 Agent 资源层级、临时无身份 Agent 或跨 Project spawn。
- 不把 Session 当成 completed、failed、stopped 或 closed 的对象。
- 不创建 fork-join、自动 retry、自动汇总、Agent 推荐、任务规划、验收或父代理巡逻机制。
- 不复制 transcript、output、tool calls 或 Runtime context 到 parent；terminal report 只传引用。
- 不允许 first release 的 `--worktree`、`--work-dir`、`workspacePath` 或任意路径输入。
- 不让 Runner 通过扫描工作目录、环境变量或 Runtime Session 猜 parent/child 身份，也不让它
  为 child 选择不同于 parent binding 的 Runner。
- 不承诺 tree relation 替代 Issue、Workflow 或 Project Space 的工作所有权和隔离模型。

## 验证

Server spec 必须以 fake Runner、fake clock 和 in-memory stores 覆盖至少以下行为：

- capability snapshot 对 rename、archive、self-spawn、cross-Project 和同 key conflict 的结果；
- SpawnOrigin 的 parent identity、缺失/unknown/stale binding 或 authoritative workDir 拒绝、
  workDir inheritance 与 exact Runner pin，且 Job admission 不会改选其它 eligible Runner；
- pre-plan validation rejection 只持久化 request fence、同 key stable replay、mismatched payload
  conflict 和新 key retry，不留任何 child artifact；post-plan reservation/final-check rejection 的
  Job cancelled、initial Turn cancelled、Session idle、无 visible link/input/callback 和 replay
  must-not-submit；
- coordinator 每个 durable fence 的 activation loss/retry，确保一个 Job、Session、Input、
  Turn、edge 和 dispatch，或同一 durable rejection；
- 单 parent、无 reparent/cycle、detach 后 subtree query 的 indexed read cost、batch、revision-
  pinned stable BFS page/continuation 及 concurrent attach/detach 只由新 query 可见；
- 当前 Agent Connection parent 与 Workflow inline parent 因各自缺失 required facts 被拒绝；具备
  authoritative workDir 与当前 Runner binding 的 Mohist Agent parent 可 spawn，target Agent
  `MaxConcurrentRuns` 满时 child 以普通 Job 语义排队而非拒绝；
- AgentJob terminal 而非 Session/Turn terminal 触发一次 parent SessionInput，含 handler
  replay、parent busy/capacity、unknown 与 detach race；
- cascade snapshot 与 reservation/final attachment/detach 的两两顺序、queued/executing/idle/
  unknown targets、pre-submit child cancellation、partial outcome、同 operation retry、unknown
  保持 spawn admission fence 及 later Turn 不被旧 operation 停止；
- startup context 在第一轮前包含 own Session ID、parent ID（若有）、snapshot 和规范 CLI
  command，且 dispatch 不依赖 per-session environment variable。

## 状态

本设计尚未实装。当前 Agent launch coordinator 已有 idempotent prepare / initial launch /
submit recovery，AgentJob 已是 launch work 的 terminal authority，AgentSession 已有
SessionInput、AgentTurn 与 turn-control 基础；它们是上述增量应扩展的既有边界。当前并无
SessionParentLink、spawn API、tree index、terminal callback、cascade operation、detach 或
managed worktree materialization。
