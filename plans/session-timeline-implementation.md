# AgentSession 时间线实施规划

## 范围与判定

唯一需求来源是 `docs/web-ui.md` 的「AgentSession 页 / 会话时间线」和
`design/session-timeline.md`。本计划只改 Web：不新增或持久化 transcript 事实，不改
Server、Runner、CLI、事件总线或 Session/Turn 的状态裁决。

当前统一读取已经给出三个同源输入：

- `/sessions/{id}/transcript` 的有序 `SessionTurn` / `SessionPart`（包括 tool 的原始输入和输出）；
- `/sessions/{id}` 的 `activity`、`SessionInputObservation`、`AgentTurnObservation` 与
  `recoveryHistory`；
- 已绑定同一 logical session、runtime 和 runtimeSessionId 的实时 transcript envelope。

实施时以这些响应对象和实时 detail 对象本身作为 raw detail，不能先经过
`projectSessionToDisplayTurns`、context-group 或旧工具卡片再反推事实。`TimelineFact` 和
`TimelineItem` 都是每次读取/实时更新后在客户端重新计算的值，不能写回 query cache、
持久化或发布成新事件。

**关键设计决定：** 将“事实归一”和“呈现派生”分开。事实适配器保留源对象、稳定来源 Id
和 Server 给出的顺序/时间；纯派生器只接受事实并产出条目；React 组件只消费条目。
这样 raw view 与摘要 view 共享同一事实数组，不会形成第二条 feed，也不会让 UI 推断
Session 状态。

```text
persisted transcript + live transcript detail + Session summary
  -> TimelineFact[] (source id, order, raw payload, Server state)
  -> deriveTimelineItems() -> groupTimelineItems()
  -> summary timeline | raw fact timeline
```

`SessionInput` 与 `AgentTurn` 关联使用 Server 的 `inputIds` 和 `sequence`；不能用生成时间、
页面位置或数组恰好相邻来猜关联。没有可证明关联的 Input 仍独立显示为 input 条目，并显示
其 acceptance 为未知。所有静默状态只读取 `activity`、当前 Turn 状态和 transcript 事实：
不以计时器、最后一条消息或空数组推断 idle/unknown。

链接是语义引用而非字符串路径：事实层输出 Issue/Agent/Workflow context 引用，页面层用
`useProjectPath` 解析现有路由。已能解析的 Issue 必须链接到 Issue 页；没有现有目标路由的
run id 显示为可识别的 Workflow context，不能把裸内部 id 当作对象名，也不能编造路由。

## Slice 与并行关系

| Slice | 独立交付价值 | 依赖 | 可并行 |
|---|---|---|---|
| S0 | 锁定一个纯、可重复的 TimelineItem 语言和分类规则 | 无 | 先完成接口契约 |
| S1 | 把现有 Session API/live 事实无损适配成该语言的输入 | S0 类型 | 与 S2 并行 |
| S2 | 用 mock TimelineItem/TimelineFact 呈现摘要、折叠、详情和 raw rows | S0 类型 | 与 S1 并行 |
| S3 | 把 S1/S2 接入 AgentSession 页，删除重复证据区 | S1、S2 | 汇合后实施 |
| S4 | 删除已无生产引用的旧 Turn/工具卡片展示链和过期测试 | S3 | 最后实施 |

S0 的 public types 是唯一跨 slice 契约；S1 不写 S2 文件，S2 不写 S1 文件。S3 只做
page/layout 组装，不重写分类或 JSX 行语义。每个 slice 先跑本 slice 的测试；S3 和 S4
再跑完整 Web 验证。

## S0: TimelineItem 纯派生契约

**文件**

- 新增 `packages/web/src/entities/session/model/timeline/types.ts`
- 新增 `packages/web/src/entities/session/model/timeline/derive.ts`
- 新增 `packages/web/src/entities/session/model/timeline/domain-actions.ts`
- 新增 `packages/web/src/entities/session/model/timeline/group.ts`
- 新增对应的 `*.test.ts`
- 修改 `packages/web/src/entities/session/index.ts`，只经实体 public API 导出类型与纯函数

**实现**

1. 定义中立的 `TimelineFact`、`TimelineItem`、`TimelineDetail`、`TimelineReference` 和
   `TimelineGroup`。每个 fact 保留 source id、稳定排序键、发生时间和未改写的 `raw`；每个
   item 有 `id`、render class、summary、salience、可选 group key/detail/reference。render
   class 完整覆盖 `input`、`message`、`reasoning`、`file-read`、`file-edit`、`shell`、
   `domain-action`、`plan`、`tool`、`status`、`boundary`、`error` 和 `suppressed`。
2. 实现纯函数 `TimelineFact[] -> TimelineItem[]`，分类顺序固定为领域动作识别、工具类型表、
   `tool` 诚实兜底；一项事实只能得到一个类。失败是最后出口：保留已构造的句式并变为
   `error`、补充失败结果；不会在原条目旁边再制造第二个错误条目。
3. 由一个统一的 summary builder 生成 `Verb + Object + Outcome?`。文件编辑取已有
   changed-files/additions/deletions，shell 取命令和 exit outcome；参数、完整输出、diff 和
   raw payload 只进 `Detail`。无法证明对象或结果时输出保守兜底（如“执行了 X”），不补充
   想象出的路径、Issue 或成功结论。
4. 把 Mohist 动作表与解析器集中在 `domain-actions.ts`。shell 只接受可确定的 `mo` argv，
   以及 `bash -c`/`bash -lc` 中只有一条可确定 `mo` argv 的情形；含管道、重定向、命令替换、
   多命令或不能正确分词的 shell 文本一律退回普通 `shell`。动作表以完整命令组/动词匹配，
   覆盖当前 product language 的 issue comment、issue start 和 run approve/reject/retry/
   rerun/pause/resume/stop，并保留扩展表入口而不是散布正则。Runtime/MCP 通路仅对命中同一
   动作表的工具名和结构化参数升级。两条通路产生完全相同的 summary/reference/outcome，
   仅 source 标记不同；非 0 或失败状态必为 `error`。
5. 在同一 reducer 中实现原地更新语义：tool 的 started/updated/completed 以 toolCallId
   更新同一 item，终态不可被迟到事实回退；同一消息关联的 text/reasoning chunk 追加，非文本
   fact 到来前封缄当前流；补全工具事实可以 `shell -> domain-action`，但 Id 不变。
6. 实现显著性和折叠：连续至少 3 个同类低显著 item（file-read、成功 shell、tool）才能
   折为一个可展开 group，优先使用相同 GroupKey。`error`、`domain-action`、`input`、
   `message`、`status`、`boundary`、`suppressed` 永不入组且切断前后段；file-edit 也保持
   独立。salience 顺序严格为 spec 所列顺序，只影响样式、折叠资格和活动摘要选择。

**验证**

- 纯测试使用固定 ISO 时间和 fixture，不使用网络、系统时钟或 React。
- 覆盖全部 render class、成功/失败句式、文件增删、未知工具兜底、shell 与工具两通路的等价
  `domain-action`、不确定 shell 不升级、可链接 Issue 与不可解析 id。
- 覆盖 started/updated/completed 保持同一 Id、终态不回退、text/reasoning 被非文本封缄、
  兜底升级。
- 覆盖 3+ read/success shell/tool 折叠、GroupKey 优先、失败插入时前后两组断开，以及所有
  永不折叠类和 salience 排序。

## S1: Session 事实适配与实时合并

**文件**

- 新增 `packages/web/src/widgets/session-transcript/model/timeline-facts.ts`
- 新增 `packages/web/src/widgets/session-transcript/model/timeline-facts.test.ts`
- 新增 `packages/web/src/widgets/session-transcript/model/useSessionTimeline.ts`
- 新增 `packages/web/src/widgets/session-transcript/model/useSessionTimeline.test.tsx`
- 修改 `packages/web/src/entities/agent/model/types.ts`，为 transcript live detail 声明已有的
  `type`、`sequence`、`createdAt` 和原始 `payload` header
- 修改 `packages/web/src/app/providers/model/event-envelope.ts`，保持上述 header 与 nested payload
  在标准化 detail 中无损共存，而不是只向下游暴露展示字段
- 修改 `packages/web/src/app/providers/LiveTaskProvider.transcript.dom.test.ts`，锁定 envelope 到
  AgentDetail event 的 header/payload 保留契约
- 修改 `packages/web/src/widgets/session-transcript/model/useSessionTranscript.ts`，仅在需要保留
  live source detail/稳定 correlation 时扩展返回值；不在此处分类或渲染
- 修改 `packages/web/src/widgets/session-transcript/index.ts`，导出受控的 timeline hook/types

**实现**

1. 将 persisted `SessionTurn`/parts、summary 的 inputs/turns/activity/recovery history 和已经
   identity-filtered 的 live detail 归一为一个按 Server sequence/timestamp 排序的事实数组。
   先把现有 envelope 的 `type`、`sequence`、`createdAt`、nested `payload` 公开为有类型的 live
   detail；`raw` 保持原 API/SignalR 对象，detail 展开直接读它，而不是从 display string 逆解析。
2. input fact 带 acceptance；每个 AgentTurn 带 its inputIds、queued/executing/terminal status
   和终结 result。多条 Input 以实际 `inputIds` 归到同一 turn 视觉关系；未归属 input 单列。
   queued 的输入和 status 行写“排队中”，executing turn 产生“执行中”的状态事实和活动候选，
   terminal result 以实际成功/失败/cancelled 事实表现，不能将 AgentJob 结果当 Session 终态。
3. recovery history 和 transcript 的 compaction/context-reset 都生成 `boundary` fact，按已给的
   recordedAt/part time 插入原顺序。Reset summary 为“上下文已重置”，其后事实属于新 context；
   Compact 保持对应边界和详情。历史和 live 的同一来源 Id 去重，不因 refetch 双显。
4. 由 summary `activity` 建立 status fact：queued 首先由 Turn 表达；active 且没有新可读条目
   时，Current activity 选最近未终结且非 status/suppressed 的 item，否则只呈现 Turn 状态；
   idle/unknown 明确输出对应状态，unknown 绝不折算 idle。既不复用现有两秒 streaming
   timer，也不以“最后消息多久以前”作语义状态判断。
5. `useSessionTimeline` 只组合事实并调用 S0 纯函数，输出 `facts`、未折叠 items、分组后的
   entries 和 current activity。它不保存第二份 timeline state；每次 authoritative response 或
   合格 live update 到来时用稳定 source Id 重算。

**验证**

- 使用 summary/transcript/live fixture 和 `dispatchAgentEvent` fake，验证 envelope header/payload
  无损、runtime/session identity 拒绝规则仍有效、refetch 不覆盖 live tail，重复事实不重复显示。
- 验证 Input acceptance、多个 input 对一个 turn、queued/executing/terminal turn、未关联 input，
  以及 active-no-output、idle、unknown 的三种不同状态。
- 验证 reset/compaction 在原始时间顺序中保留为 boundary，tool live update 的 raw detail 与
  stable item Id 一致。测试只传固定时间/fixture；如涉及 React effects，用 fake timer 并在
  每个 test 恢复，不等待墙钟。

## S2: Timeline 与 raw 视图组件

**文件**

- 新增 `packages/web/src/widgets/session-transcript/ui/TimelineItemList.tsx`
- 新增 `packages/web/src/widgets/session-transcript/ui/TimelineItemRow.tsx`
- 新增 `packages/web/src/widgets/session-transcript/ui/TimelineGroupRow.tsx`
- 新增 `packages/web/src/widgets/session-transcript/ui/RawTimelineView.tsx`
- 新增对应 `*.test.tsx`

**实现**

1. 渲染行只读 `TimelineItem`/`TimelineGroup`：高显著 error 为醒目失败卡，domain action 和
   输入/消息突出，file-read/tool/reasoning/status/suppressed 安静。reasoning、参数、完整输出、
   diff 与 raw 使用默认关闭的 details；每行主文本只呈现 S0 生成的一句话。
2. group 行用稳定 group/child source Id，显示“读取了 N 个文件”等汇总，展开后才列组内原条目。
   不在组件重新决定折叠资格；因此失败/关键条目不会被 UI 意外吞入汇总。
3. domain-action 的 semantic reference 通过页面传入的 resolver 成为链接。使用现有 `Link` 与
   项目路径，不手写 SVG；无可用引用时保守显示 summary，不显示裸内部 id。
4. raw view 接收同一个 `TimelineFact[]`，每条 fact 一行，显示 source kind、稳定 Id、发生时间，
   默认关闭的 payload details 中展示未改写 raw。它不调用分类或分组函数。
5. 为 summary/raw 都加 `data-timeline-source-id`，让父级可按来源 Id 锚定；group 的锚点覆盖
   child source Id，避免用数组索引或滚动像素作为切换依据。

**验证**

- 组件测试以 mock items/facts 验证句式、默认折叠 detail、失败突出、domain link、group 展开
  和禁止分组类。
- 验证 raw view 的行数等于 facts 数、payload 未被 summary 改写、展开不改变顺序，并验证同一
  source id 同时可在 summary/group 和 raw 视图定位。
- 所有 DOM 测试 stub `scrollIntoView`，不使用真实网络、真实时钟或固定等待。

## S3: AgentSession 页接线与视图切换

**文件**

- 修改 `packages/web/src/pages/session/data/SessionDataSource.ts`
- 修改 `packages/web/src/pages/session/data/useUnifiedSessionDataSource.tsx`
- 修改 `packages/web/src/pages/session/data/useUnifiedSessionDataSource.test.tsx`
- 修改 `packages/web/src/pages/session/ui/SessionDetailShell.tsx`
- 修改 `packages/web/src/pages/session/ui/SessionDetailShell.test.tsx`（或现有页面 shell 测试）
- 修改 `packages/web/src/pages/session/ui/UnifiedSessionPage.test.tsx`
- 修改 `packages/web/src/widgets/session-transcript/ui/SessionTranscriptLayout.tsx`
- 重写 `packages/web/src/widgets/session-transcript/ui/SessionTranscriptLayout.integration.test.tsx`

**实现**

1. data source 将 S1 的 `facts/items/entries/currentActivity` 放入 `SessionDataSourceResult`，并把
   `summary.activity`、current Turn、inputs、turns、recovery history 和 project-path reference
   resolver 一次性传入。`displayTurns` 只在仍被非时间线证据使用时临时保留；所有展示判断改由
   TimelineItem 后即可移除它。
2. `SessionTranscriptLayout` 从 TurnList/AssistantParts 改为消费 timeline entries，使用 S2
   行组件；空状态和 current activity bar 消费 S1 的 authoritative status/currentActivity，不再
   从“有无工具卡片”“thinking”或本地 timer 推导。queued、等待 backend、idle、unknown 必须在
   有历史条目时仍可见。
3. `SessionDetailShell` 增加页面级的摘要/原始分段切换（使用现有 lucide icon 与有 label 的
   可访问控件）。切换前捕获当前可见 `data-timeline-source-id`，切换后按同一 source id
   `scrollIntoView`；找不到时不猜相邻项目。两种视图用同一 data source，切换不会 refetch、
   重新分类或建立第二个 state store。
4. 将现在独立的 `SessionInputTurnEvidence` 和 `SessionRecoveryHistory` 从首屏证据区移除：它们
   的内容已进入时间线 input/status/boundary 行。保留恢复 action、usage、Job/错误证据等不属于
   transcript 的区域；错误证据改从 timeline error items 读取，而不是旧 `DisplayToolPart`。

**验证**

- page/data-source 测试覆盖 workflow 与 agent-launch Session、follow-up 后 query reconciliation、
  queued cancel/executing stop/unknown 不可操作等既有行为未回归，并验证它们的 Input/Turn 事实
  进入 timeline。
- integration 测试覆盖完整顺序：输入 -> 读取组 -> 失败 -> 后续读取组 -> domain action ->
  context reset -> idle/unknown；失败和 domain action 独立可见，边界不丢失。
- 测试 summary/raw 切换保持 source-id anchor、raw 不发额外 HTTP 请求、Issue domain link 使用
  project-scoped URL、空且 active/idle/unknown 分别呈现。测试通过 injected fixtures 和 DOM
  fake 完成。

## S4: 迁移收尾

**文件**

- 删除已没有生产 import 的旧展示模型/测试：
  `model/session-transcript-display.*`、`model/timeline-nodes.*`、旧 select/locate/turn-ref/display
  helper，以及 `ui/TurnList.*`、`ui/AssistantParts.*`、`ui/MiniTimeline.*`、
  `ui/CurrentActivityBar.*`、`ui/PromptBlock.*`、`ui/CopyFullTextButton.*` 和仅服务它们的
  `ui/tool-views/` 文件。
- 相应收敛 `packages/web/src/widgets/session-transcript/ui/index.ts` 和
  `packages/web/src/widgets/session-transcript/index.ts` 的旧导出。

**实现与验证**

1. 先用 `rg` 确认没有生产 import，再删旧 Turn 分组/工具卡片链和只验证该链的测试；不删除
   仍由 S1 使用的 live transcript identity/filtering 代码。
2. 迁移后不存在同一页面的 TurnList 与 TimelineItem 两套显示或两套折叠规则。保留的测试只描述
   新时间线行为；默认不为迁移写解释性代码注释。
3. 运行 `rg` 验证旧 public exports/production imports 为零，再跑完整 Web 验证。

## 完成审计

| 需求纪律 | 落点 | 证明 |
|---|---|---|
| TimelineItem 本地派生、句式与 detail 默认折叠 | S0、S2、S3 | 纯派生和 DOM tests；无 Server/事件写入 diff |
| Mohist 领域动作，shell 与工具两通路 | S0、S2 | 等价动作/失败/链接/安全降级的 fixture tests |
| 失败醒目、读取安静、连续折叠且失败打断 | S0、S2、S3 | grouping unit tests 和完整顺序 integration test |
| Input acceptance/delivery 与 Turn 排队、执行、结束 | S1、S3 | summary+transcript fixture 和 page data-source tests |
| 沉默状态：queued、等待 backend、idle、unknown | S1、S3 | 不同 Server 状态 fixture，unknown 不渲染 idle |
| Compact/Reset 上下文边界 | S1、S3 | recovery/transcript facts 产生 ordered boundary 的 tests |
| 原始事件视图，同源切换与锚定 | S2、S3 | raw row/raw payload/无额外请求/Id anchor tests |
| 原地更新与终态不可回退 | S0、S1 | started/updated/completed 的 deterministic tests |
| FSD、fake 和时间纪律 | 全部 | public exports、无真实 I/O/墙钟的测试，以及最终命令 |

最终在干净工作树上依次执行：

```bash
npm run typecheck -w packages/web
npm run test:run -w packages/web
npm run check:fsd -w packages/web
git diff --check
git status --short
```

前两条是 Web 交付必需验证，第三条来自 `packages/web/AGENTS.md` 的层级与 public API
约束。任何失败必须区分实现回归与基础设施问题；不能以跳过测试、真实外部依赖、墙钟等待
或保留旧路径来宣称完成。
