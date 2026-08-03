# Slack 集成实施规划

## 范围与现状

唯一需求来源是 docs/slack.md、design/slack.md、docs/cli-reference.md 的 Slack 一节和 design/cli.md 对应决策表。本计划只覆盖它们定义的目标，不以当前代码的便利性缩小行为。

当前基线为 spec/slack 的 fab0940e7。已经存在、不能在本轮重新设计的能力是：Server 持有 provider inbox、thread/DM mapping 与 outbox；频道根提及、已绑定 thread follow-up、多 Bot 归属、重复投递保护、Owner-only、thread 历史导入和状态投影数据面已经具备；mohist-slack 是无状态 Socket adapter；Web 已有会话时间线路由 /sessions/:sessionId；CLI 仍将所有 Slack 动作挂在 mo agent connection。

本轮必须新增：

1. 废弃 mo agent connection，将 19 个动作全部迁入根级 mo slack，并新增 setup/status，不留 alias。
2. 用名为 mohist-slack 的 Server 级内置 Agent 作为 Mohist App，在 Slack DM 管理 workspace、Connection 和 Agent。
3. 对每个 Slack 输入注入可查看、可版本化的协作 Skill，而不改写 Agent 的长期 Instructions、Runtime、Model 或 Skills。
4. Server 决定回复锚点，并随输入告知 Agent；Agent 不猜 channel、thread 或历史消息。
5. 执行中新消息默认 Steer；只有明确 Stop action 才请求 interrupt。
6. Open in Mohist 直达现有 AgentSession 时间线。

贯穿不变量：

- Server 是 AgentJob、AgentSession、AgentTurn、Manager lifecycle、inbox、mapping 和 outbox 的唯一事实源；adapter 不持久化恢复状态，不裁定执行结果。
- Manager 不代替执行 Agent 回答工作问题；Manager 与 Agent App 同样走 adapter、Server ingress 和 outbox。
- Connection 只决定调用资格，不能复制、扩大或削减 Agent 执行配置。Manager 只调用既有 application service，不再造第二个状态机。
- 对未知 provider mutation 必须先 reconcile；同一输入至多一个可替换 progress 和一个 terminal delivery。Slack 投递失败不改变执行结论。
- 测试只用 fake port、in-memory transport/store、固定 TimeProvider 或 fake timers；不触真实 Slack、网络、进程、文件或墙钟。

## 权限前置决策

当前 P0 Manager HTTP API 明确拒绝客户端声明 actor 或 ManagerExternalId，而 OperatorCredential 只证明控制面调用，不能识别 Slack member。因此不能把 Slack member ID 当 Mohist 管理员，也不能在无权威主体时实施 Manager DM。

S0 建立 ManagerActor 边界：mo slack setup 只能由已获 OperatorCredential 授权的本机操作者启动，并签发短时、一次性的 Manager claim。Mohist App 收到 claim 后将 Slack member 绑定到该认证的部署级管理主体。所有对话工具由 ManagerActorAccessDecider 对目标 Project、Agent 或 Connection 重新检查权限后，才调用现有 application service。

现有单操作者 P0 中，claim 是唯一的绑定来源；将来的多用户认证只能替换 ManagerActor 的解析，不能从 Slack 文本、HTTP header 或自由参数声明 actor。这保持 Slack 身份不是 Mohist 管理员的产品边界。

## Slice 与并行关系

| Slice | 独立交付价值 | 依赖 | 可并行边界 |
|---|---|---|---|
| S0 | workspace enrollment、可信 Manager actor、主 App 数据面入口 | 现有 Manager correctness kernel | 先完成，是安全门 |
| S1 | 唯一的 mo slack CLI 命令面 | S0 的 setup/status API contract | 与 S2、S3 并行，只改 CLI |
| S2 | 内置 Manager Agent 和受控管理工具 | S0 | 与 S1、S3 并行，只改 Server Agent/Manager 层 |
| S3 | 每个普通 Slack 输入的协作 Skill 与回复锚点 | 现有 SessionInput/dispatch contract | 与 S1、S2 并行，只改 Server/Runner contract 与 asset |
| S4 | Steer 默认和显式 Stop interaction | S0、S3 | S3 contract 定稿后，拥有 adapter interaction 路径 |
| S5 | 安全 Open in Mohist delivery | S3、S4 | S4 审查并提交后串行实施；两者共同修改 adapter 文件 |
| S6 | fake seam 的跨 slice 产品流验证 | S0-S5 | 最后汇合 |

S0-S5 分开提交；S4 与 S5 不并行，必须由同一实施者串行推进；S6 不吸收业务实现，只做组合接线、文档差距收敛与测试。公共 composition root 或 DTO 只能由拥有该文件的 slice 改动，其他 slice 通过已锁定 contract 交互。

## S0: Enrollment、主体绑定与主 App 数据面

**文件**

- 修改 packages/server/src/Mohist.Server/Slack/Domain/SlackWorkspaceEnrollment.cs、SlackManagerApplicationService.cs、对应 store/EF row、MohistDbContext.cs、迁移和 snapshot。
- 新增 packages/server/src/Mohist.Server/Slack/Services/ManagerActorAccessDecider.cs、ManagerClaimService.cs、SlackManagerIngressService.cs 和 fake/test support。
- 修改 packages/server/src/Mohist.Server/Api/SlackManagerRoutes.cs；新增 SlackManagerIngressRoutes.cs，并在 API composition root 注册。
- 修改 Infrastructure/Slack/SlackOutboxModels.cs 和 SlackOutboxStore.cs，使 delivery owner 可明确表示 Connection 或 Manager，仍使用同一 claim/ack/uncertain/reconcile 语义。
- 新增按 Slack/Manager 和 Api/SlackManager 组织的 Server unit/spec tests。

**实现**

1. Enrollment 持有 Manager App 的外部身份、credential reference、transport/readiness、已认领 Slack member 和 audit facts；这些不属于 AgentConnection，且不保存 plaintext credential、OAuth code 或 claim code。
2. setup 创建或恢复同一个 enrollment 并签发一次 claim。相同 workspace 重试只恢复当前事实和唯一 next action；外部副作用未知时只能进入 unknown，不能创建第二个 Manager App。
3. Manager ingress 用稳定 Slack message identity，先 durable accept，再处理 claim/对话。它复用 provider inbox、outbox、claim/ack/reconcile，但不使用普通 Connection 的 Owner/Allowlist/Anyone。
4. 每个读取或写入经 ManagerActorAccessDecider 决定。未知、未认领、过期 claim 或无权 actor 不创建 Session、Connection 或 delivery intent，且不泄漏未授权资源名称。
5. status 返回 enrollment、Manager App 和相关 Connection 的统一状态与唯一 next action；CLI、Web 和对话读取同一个 projection。

**验证**

- 固定时间的 setup 幂等、claim 单次/过期/替换、member-to-actor 绑定、伪造 actor 拒绝、按目标资源 allow/deny、重复 Slack event 和重启恢复。
- 覆盖 Manager create/delete/authorization 的 definite failure 与 unknown，不复制 App 或 delivery；outbox 保持单一 claim owner，secret/audit/DTO 中无 plaintext。
- 运行 npm test。最终 npm test 是 Server/CLI 证据，不能以 Total: 0 的 focused run 替代。

## S1: 唯一 mo slack 命令面

**文件**

- 新增 packages/cli/Mohist.Cli/MohistCliCommands.Slack.cs，迁移现有 builders；修改 MohistCliCommands.cs 注册根级 slack，修改 MohistCliCommands.Agent.cs 移除 AgentConnectionCommands.Build(api)。
- 删除 packages/cli/Mohist.Cli/MohistCliCommands.AgentConnection.cs，而不是保留未注册的兼容实现。
- 新增或重命名 packages/cli/tests/Mohist.Cli.Tests/CliSlackCommandSpecs.cs；删除 CliAgentConnectionCommandSpecs.cs，避免旧命令面继续被认可。
- 只在输出字段确有变化时修改 CommandPresentations.cs 或 ResourceOutputCatalog.cs；不借迁移改 Server route/API client。

**19 项一一映射**

| 旧命令 | 新的唯一命令 |
|---|---|
| mo agent connection create | mo slack create |
| mo agent connection configure | mo slack configure |
| mo agent connection rotate-credentials | mo slack rotate-credentials |
| mo agent connection claim-owner | mo slack claim-owner |
| mo agent connection transfer-owner | mo slack transfer-owner |
| mo agent connection disable | mo slack disable |
| mo agent connection enable | mo slack enable |
| mo agent connection view | mo slack view |
| mo agent connection list | mo slack list |
| mo agent connection create-child-app | mo slack create-child-app |
| mo agent connection reconcile-create | mo slack reconcile-create |
| mo agent connection reconcile-delete | mo slack reconcile-delete |
| mo agent connection remove-binding | mo slack remove-binding |
| mo agent connection permanent-delete | mo slack permanent-delete |
| mo agent connection deliveries | mo slack deliveries |
| mo agent connection resend-delivery | mo slack resend-delivery |
| mo agent connection clear-gap | mo slack clear-gap |
| mo agent connection edit | mo slack edit |
| mo agent connection delete | mo slack delete |

新增 mo slack setup 调 S0 enrollment/bootstrap，mo slack status 读 S0 workspace projection。create 仍只建立可恢复 Connection，不能暗中替 setup 创建或认证 Manager App。configure 和 rotate-credentials 继续只收受保护输入或 credentials-file，不能新增 token literal flag。permanent-delete 与 resend-delivery 保留现有显式确认和重复警告。

**验证**

- root/group/leaf help 只出现 slack；agent connection、connection alias 和旧帮助文本均解析失败且没有 HTTP request。
- 逐项验证表中 19 个 action 的参数、route、body、错误码和既有危险动作确认；验证 setup/status 的 noninteractive 缺参、JSON 字段发现和 project 解析。
- 运行 npm test、git diff --check；文档中每个 mo slack 示例均可由真实 command tree 解析，旧命令没有公开路径。

## S2: 内置 Manager Agent 与受控管理工具

**文件**

- 新增 packages/server/src/Mohist.Server/Agent/Services/BuiltInAgentCatalog.cs、BuiltInAgentResolver.cs、SlackManagerAgentTools.cs 及 tests；修改 Agent definition resolver、launch/follow-up service 及其 contract tests，使 builtin definition 走现有 Agent/Session/Turn 路径。
- 新增 packages/server/src/Mohist.Server/Slack/Services/SlackManagerConversationService.cs 和 SlackManagerToolAuthorization.cs；只修改 S0 暴露的 manager ingress service，不改普通 Connection ingress。
- 新增 mohist-slack 内置 Instructions/工具说明嵌入资源及资产测试。不要加入 packages/cli/Mohist.Cli/presets：mo agent install supervisor 产出普通 Project Agent 和 RoutingRule，不能保留名称、不能跨 Project、也不能代表 Manager。

**实现**

1. BuiltInAgentCatalog 将 mohist-slack 定义为 Server-level reserved definition。用户 Project 的 list/create/edit/archive/delete 不能占用、替换或移除该名字；它不出现在普通 Project Agent name space，也不因 mo agent install 产生副本。Resolver 仍输出完整 Agent definition，沿用 launch -> SessionInput -> AgentTurn -> Runner，不造 Manager runtime。
2. Manager DM 每轮由 SlackManagerConversationService 创建或继续 Manager AgentSession，并附加 S0 的 authenticated actor/workspace context。tool catalog 只含查询、挂载/继续安装、access-policy edit、enable/disable、owner transfer、diagnostics 与 create Agent；remove-binding/permanent-delete 不提供。
3. 每次 tool 调用先由 SlackManagerToolAuthorization 按 ManagerActorAccessDecider 检查目标资源，再委托现有 SlackManagerApplicationService、AgentConnectionStore 或 Agent create service。不得重写 Connection transition 或直接改表。
4. 创建 Agent 最多追问 name 和日常职责。服务使用 Server typed default profile/runtime/model resolver 创建真实、可审计 Agent，manager 只起草受限 Instructions；不接收未经校验的 runtime/model JSON。成功后引导同一 create/Manager install flow。
5. 对话只由 Manager App 投递；执行 Agent 的工作结果仍由其独立 Bot 投递。状态和 next action 只来自 S0/S2 projection，模型文本不能宣布 ready。

**Manager tool 与 mo slack 的对照**

| mo slack 动作 | Manager DM 处理方式 |
|---|---|
| setup | 不暴露；它是 OperatorCredential 启动的 bootstrap，完成后才有可对话的 Mohist App。 |
| status、list、view | 允许，只读取 S0 的同一 projection，并回答唯一 next action。 |
| create | 允许，但呈现为挂载 Agent；它驱动已有 create/child-app/authorization workflow，不把内部步骤暴露为散乱命令。 |
| claim-owner、transfer-owner | 允许；生成或继续一次性 claim，并将用户引导到目标 Agent App DM 完成认领。 |
| edit、enable、disable | 允许；先做 actor 对目标 Connection 的权限判断，再调用同一 application service。 |
| configure、rotate-credentials | 不允许；credential 输入只在 CLI/Web 的受保护通道提交，绝不进入 Slack 消息或 transcript。 |
| create-child-app、reconcile-create、reconcile-delete | 不作为原始 tool 暴露；Manager 的挂载/恢复流程可调用同一服务，但用户只看到当前状态和下一步。 |
| deliveries、resend-delivery、clear-gap | 不作为原始 tool 暴露；对话只给安全诊断摘要，可能重复投递的 resend 仍留 CLI/Web 的显式确认。 |
| remove-binding、delete、permanent-delete | 不允许；前两类解绑/删除和永久删除都保留 Web/CLI 的显式生命周期操作，permanent-delete 还要求二次确认和审计。 |

**验证**

- catalog/resolver tests 锁定 reserved-name collision、不可 archive/delete、不进入普通 list、但可启动标准 Session/Turn；使用 fake runner/agent API。
- conversation specs 覆盖 claim 后 list/view/create/edit/enable/disable/transfer、两次澄清上限、default source、无权不泄漏和直接模型请求不能绕过 tool authorization；删除动作不可发现/执行。
- tool 结果与 CLI/API 同一资源状态和同一 next action；运行 npm test。

## S3: 每输入协作 Skill 与回复锚点

**文件**

- 新增版本化 mohist-slack-collaboration Skill asset、Server embedded catalog 和 asset contract tests；通过受管 skill-data 打包，不在测试或运行时读取 checkout 路径。
- 新增 packages/server/src/Mohist.Server/Contracts/AgentSlackExecutionContextContracts.cs，修改 Session input/dispatch contracts、AgentSessionInputProvenance mapper/read model，以及 SlackConnectionRoutes.cs 中 BuildSlackInputProvenance 和 launch/follow-up 的唯一调用点。
- 修改 Server-to-Runner dispatch payload builder、packages/runner/src/runtime/agent-job-executor.ts、executor.ts、execution-envelope.ts 和对应 runner tests。扩展 known payload keys，不能将 context 当 unknown 丢弃。

**实现**

1. 成功接纳每条普通 Slack root/follow-up 后，Server 按不可变 input identity 计算 SlackReplyAnchor：workspace、conversation、thread root、触发 message、发起人、Connection、sessionId 和 dispatchRef。该值是 provenance/dispatch context，重投必须等价。
2. context 以 dispatch-only、清晰标记为系统事实的 envelope 交给 Runner；不进入 Agent 长期 config，不让非 Slack launch 有 Slack 字段，也不允许 Agent 用它改写 outbox target。出站仍只用 Server outbox target。
3. Slack input 注入版本化 Skill，其内容只有四条 spec 规则：避免空洞确认；完成委派才 @ 委派者；结论/证据/下一步自包含而不过度刷屏；回复位置服从 SlackReplyAnchor。Web/CLI/Workflow input 的 skills 和 prompt 字节保持不变。
4. Skill 是可查看的受管 asset，也是运行时 inline resolved skill；不依赖用户是否恰好安装 ~/.agents/skills，不能硬编码进 adapter，也不复制到每个 Agent definition。asset version/hash 进入 dispatch evidence，但不保存 secret。

**验证**

- fixed-time Server specs 覆盖 DM、频道 root、thread follow-up 和重复 ingress 的 anchor 精确性、replay equivalence、审计和无 token 泄漏。
- runner tests 断言 Slack dispatch 有一份正确版本 Skill/context，非 Slack dispatch byte-for-byte 不变，且 context/Skill 不被 unknown-key filtering 丢弃。
- asset contract tests 覆盖名称、版本、可读内容和四条约束；运行 npm test、npm run typecheck -w packages/runner、npm test -w packages/runner。

## S4: Steer 默认与显式 Stop

**文件**

- 新增 packages/server/src/Mohist.Server/Api/SlackInteractionRoutes.cs、packages/server/src/Mohist.Server/Slack/Services/SlackTurnControlService.cs 和 Server specs。
- 修改 packages/mohist-slack/src/types.ts、adapter.ts、transport.ts、index.ts 与三个现有 test files，支持规范化 interaction envelope 和 Server action delivery。
- 只在必要时修改 AgentSessionFollowupDispatcher 或 Session control operation adapter；不改 AgentSession 既有 queue/turn state machine。普通 text ingress 继续经 S3 所有的 SlackConnectionRoutes.cs。

**实现**

1. 正常 Slack 消息无论当前 Turn 是 queued 或 executing，都只调用既有 follow-up 接纳路径：它成为同一 Session 的 SessionInput，支持追加的 runtime 可并入当前 Turn，否则按现有 queue 等待下一 Turn。normal ingress 绝不能调用 cancel/stop 或发 abort。
2. Server 生成 Stop block action，payload 绑定 Connection、session、turn、input/dispatch、actor、单次 nonce 和固定 expiry。adapter 只转发 interaction，不能由按钮文本决定停止对象。
3. interaction route 验签/去重后，SlackTurnControlService 重读当前 Turn，确认 actor 是 Connection Owner 或 Session Slack 发起者；过期、重放、connection/turn 不符、终态或 Turn 已变更均无副作用。只有仍匹配的 executing Turn 才委托既有 stop operation；queued cancel 也只由现有显式 cancel action 触发。
4. action 显示状态来自 Server 确认的 control result，不能以 Slack click 成功推断 runtime 已停止。

**验证**

- Session/ingress specs 证明 executing follow-up 不 stop/cancel、不丢 input、按接收顺序排队，并覆盖 runtime append 与 queue 两种后端结果。
- interaction specs 覆盖 Owner/initiator allow、其他 allowlisted deny、stale nonce、replay、wrong connection/turn、terminal turn 与 successful stop；每次拒绝都没有 Agent API side effect。
- adapter tests 覆盖 interaction 规范化、Server ack、action payload 无 credential；运行 npm test、npm run typecheck -w packages/runner、npm test -w packages/mohist-slack。

## S5: Open in Mohist 的安全会话链接

**文件**

- 新增 Infrastructure/Slack/SlackWebLinkBuilder.cs 及 unit tests；修改 SlackProviderOptions.cs、SlackFinalReplyRenderer.cs、SlackTerminalDeliveryHandler.cs、SlackStatusProjection.cs 和 SlackOutboxModels.cs。
- 修改 packages/mohist-slack/src/types.ts、adapter.ts、transport.ts、status-projection.test.ts 和 adapter.test.ts，使 Server delivery payload 可带安全 Slack Block button/link。
- 不改 Web 路由：packages/web/src/app/App.tsx 的 /sessions/:sessionId 与 pages/session/UnifiedSessionPage 已是唯一目标。只扩展其 route test 防漂移。

**实现**

1. Link builder 只接受管理员配置的 ExternalWebUrl，拒绝 loopback、localhost、private-only host、空值、非 HTTPS（明确开发 allowlist 除外）及带 user-info URL。用结构化 URL API 拼接既有 project-scoped /sessions/:sessionId，不接受 agent/model 提供的 URL 片段。
2. 有 sessionId 的状态/终态投影携带 Server 生成的 Open in Mohist block；无可用 external URL 时保留稳定 Job/Session id，不放不可达链接。文本结论仍完整，链接不是必经阅读路径。
3. adapter 只发送 Server 序列化的 block/text；不把 Agent 回复内的 @channel、伪造按钮、任意 URL 或 Block JSON 提升为 Slack 控制对象。update/fallback/unknown reconcile 保持同一 client message identity，不能产生第二个 terminal answer。

**验证**

- unit tests 覆盖 allowed URL、reject localhost/private/user-info、正确 project/session URL、无配置 fallback；route test 证明目标装载 UnifiedSessionPage。
- outbox/adapter tests 覆盖 post、chat.update、unknown reconcile、update-failure fallback 都只发送同一 terminal identity，并证明 agent-controlled text 不能形成 Slack block。
- 运行 npm test、npm run typecheck -w packages/web、npm run test:run -w packages/web、npm test -w packages/mohist-slack。

## S6: 汇合验证与收尾

**文件**

- 新增按产品流命名的 Server spec，例如 packages/server/tests/Mohist.Server.SpecTests/Slack/SlackManagerConversationSpecs.cs，只使用 S0-S5 的 fake ports、in-memory store、fixed time 和 deterministic runner probe。
- 只为 composition root 的 DI/route registration 修改其拥有文件；不把 S0-S5 业务代码复制进 integration slice。
- 更新 docs/slack.md、design/slack.md、docs/cli-reference.md 和 design/cli.md 的实装差距小节；仅当会话链接的用户可见说明发生变化时更新 docs/web-ui.md 的对应差距小节。不得改动这些文档的其他正文或留下新旧命令面并存的描述。

**组合场景**

1. 本机 operator 运行 mo slack setup，在 Mohist App DM 认领；无 claim 的 member 不能列 Agent，认领 member 只能操作授权资源。
2. Manager 通过内置 Agent 创建默认配置 Agent，再挂载 workspace；mo slack status 与 Manager 对话读到同一 next action。
3. Agent App 接收频道 root 和 executing-session follow-up；每个 input 有协作 Skill/reply anchor，follow-up 不 interrupt；合法 Stop 只停指定 executing Turn。
4. terminal answer 同一 thread 只投递一次且含安全 Open in Mohist link；adapter 重启、重复 ingress、provider unknown 或 update fallback 不复制 Job/Input/terminal answer。
5. permanent delete 无法由 Manager conversation 触发；remove binding 与 permanent delete 仍是独立显式动作。
6. 文档实装差距只陈述最终已交付或仍未交付的能力，CLI 参考、设计决策表和 Slack 产品页只保留 mo slack，不再描述 agent connection 为当前命令面。

最终完整验证：

    npm test
    npm run typecheck -w packages/runner
    npm test -w packages/runner
    npm test -w packages/mohist-slack
    npm run typecheck -w packages/web
    npm run test:run -w packages/web
    git diff --check
    git status --short

验证不得隐式 npm install、改 lockfile 或留下 Git 可见生成物。focused test 显示 Total: 0 不是有效证据。

## 完成审计

| 需求 | 落点 | 证明 |
|---|---|---|
| 19 项迁移且无 alias | S1 | mapping tests、旧命令解析失败、help/example parse |
| setup/status | S0、S1 | enrollment projection、CLI noninteractive/JSON tests |
| Server 级 mohist-slack | S2 | reserved-name、normal Session/Turn specs |
| Manager DM、权限、默认创建 | S0、S2 | claim-to-actor、tool allow/deny、two-question/default tests |
| 复用数据面且 adapter 无状态 | S0、S2、S6 | shared inbox/outbox fake flow、restart no-local-state |
| 协作 Skill | S3 | asset/version 和 Slack-only dispatch tests |
| Server 回复锚点 | S3 | replay-equivalent context、Server-only target tests |
| Steer 与 Stop | S4 | normal follow-up no-stop、signed-action authorization/staleness |
| 会话时间线链接 | S5 | safe URL/outbox payload/Web route tests |
| 测试和边界纪律 | S0-S6 | 上述完整命令、fake/fixed-time assertions、git diff --check |
