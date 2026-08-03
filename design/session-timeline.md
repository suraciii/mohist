# AgentSession 时间线

AgentSession 页时间线的呈现模型：把 transcript 事实派生为可扫读的活动条目。
本文只定义呈现派生，不定义第二份会话记录；transcript 契约与 Session 状态裁判不变
（见 [`agent-execution.md`](agent-execution.md)）。产品行为见
[`../docs/web-ui.md`](../docs/web-ui.md) 的 AgentSession 页一节。

## Model

**时间线条目（TimelineItem）**：从一段 transcript 事实派生的呈现单元。它是客户端
本地派生结果——不持久化、不进事件总线、不回写 Server，任何客户端可以用同一套规则
独立实现。

```text
TimelineItem
  Id            # 由来源事实决定：toolCallId、InputId、事实序号等
  RenderClass   # 呈现类
  Summary       # 句式：Verb + Object + Outcome?
  Salience      # 显著性
  GroupKey?     # 折叠分组键
  Detail?       # 展开内容：参数、完整输出、diff、原始 payload
```

呈现类一览：

| RenderClass | 来源事实 | 读法示例 |
|---|---|---|
| `input` | SessionInput | 输入内容 + 受理/投递状态 |
| `message` | text | Agent 回复 |
| `reasoning` | reasoning | 思考，默认折叠 |
| `file-read` | tool（read / grep / glob / list 等） | 「读取了 `x.ts`」 |
| `file-edit` | tool（edit / write 等） | 「编辑了 `x.ts`（+12/−3）」 |
| `shell` | tool（bash 等） | 「运行了 `npm test` → 通过」 |
| `domain-action` | 识别出的 Mohist 领域操作 | 「评论了 #42」「批准了 #42 的 Plan 阶段」 |
| `plan` | todo / plan 工具 | 计划与完成进度 |
| `tool` | 其余工具 | 诚实兜底：「执行了 X」 |
| `status` | session.activity、model、usage、provider.retry 等 | 淡色状态行 |
| `boundary` | compaction、session.context_reset | 「上下文已重置」分界 |
| `error` | turn.failed、任何失败的条目 | 醒目失败卡 |
| `suppressed` | 刻意降级的噪声事实 | 单行淡字 |

条目没有终态生命周期；进行中的条目（如 executing 的 tool call）随事实原地更新。

## Semantics

### 派生与分类

- 分类是纯函数：transcript 事实序列 → 条目序列。分类与渲染分离，呈现组件只消费
  TimelineItem。
- 分类按顺序尝试：`domain-action` 识别 → 工具类型表 → `tool` 兜底。识别失败必须
  降级，不得编造语义。
- 同一事实只归一类；失败是出口改写：任何条目带失败结果时 RenderClass 为 `error`，
  保留原 Summary 并追加失败事实。

### 领域操作识别

两条通路收敛到同一 `domain-action` 条目：

1. **Shell 通路**：解析 bash 类工具执行的 `mo` 命令——提取命令组与动词
   （`issue comment create`、`run approve`、`issue start` 等）映射为 Verb，参数中的
   Issue number、WorkflowRun id 等解析为 Object 与页面链接。
2. **工具通路**：Runtime 工具或 MCP 工具名命中 Mohist 领域操作表时直接映射。

命令的退出结果决定 Outcome；失败即 `error`。两条通路产出相同的 RenderClass 与
句式，只允许来源标记不同。命令不是已知的 `mo` 操作、或解析不出命令组时，按普通
`shell` 条目处理，不做猜测性升级。

### 句式与引用解析

- Summary 按 `Verb + Object + Outcome?` 构造；Outcome 先行，让人一眼判断成败。
- Object 必须解析为可识别名字或链接（Issue number 链到 Issue 页、Agent 名、run id
  链到 run），不显示裸内部 id。
- 构造不出完整句式时保留事实原样（如「执行了 X」），不补想象内容。

### 原地更新

- `tool_call.started / updated / completed` 按 toolCallId 更新同一条目；终态
  （completed / failed）不可逆，迟到事实不得回退。
- 流式 text / reasoning 按消息关联追加；非文本条目插入前封缄当前流，后续 chunk
  另起条目。
- 条目可先以兜底类出现，事实补全后升级为更语义化的类（如 `shell` →
  `domain-action`），Id 不变。

### 折叠分组

- 连续 ≥3 个同类低显著条目（`file-read`、成功的 `shell`、`tool`）折叠为一条汇总
  （「读取了 5 个文件」），组内可展开；GroupKey 相同的优先同组。
- `error`、`domain-action`、`input`、`message`、`status`、`boundary`、`suppressed`
  永不进组，且打断连续段——失败与关键动作必然浮出折叠。

### 显著性

从高到低：`error` → `domain-action`（写操作）→ `input` / `message` → `file-edit` /
`shell` → `file-read` / `tool` / `reasoning` → `status` / `suppressed`。

Salience 只影响呈现（醒目程度、折叠资格、当前活动摘要的选取），不回写任何领域
状态，也不参与 Session 状态推导。

### 沉默与状态呈现

- Turn queued → 输入条目与状态行表达「排队中」。
- Turn executing 且暂无新条目 → 当前活动条呈现「执行中」，取最近一个未终结的可读
  条目（跳过 `status` / `suppressed`）作为内容；无进行中条目时呈现 Turn 状态本身。
- `idle` / `unknown` → 明确的空闲 / 未知呈现；`unknown` 不得渲染成空闲。
- 以上全部来自 Server 事实（activity、AgentTurn 状态、transcript 状态事实）。客户端
  不做心跳推断，也不从条目序列推导 Session 状态——与
  [`agent-execution.md`](agent-execution.md) 的消费者规则一致。

### 原始视图

- 页面级开关：同一时间线数据切换为原始事实序视图——每条 transcript 事实一行，可
  展开 payload。
- 两种视图是同一数据的两个海拔，不是两条 feed；切换时按条目 Id 锚定滚动位置。

## Examples

1. `tool_call.completed{bash, "mo issue comment create 42 --body …", 退出 0}`
   → `domain-action`「评论了 #42」，点击跳转 Issue #42。
2. 同一命令退出非 0 → `error`「评论 #42 失败」，独立醒目，不进折叠。
3. 连续 read×3 + grep×2 → 折叠「读取了 5 个文件」；若第 3 个失败，则前 2 个折叠、
   失败条目独立醒目、后 2 个另起折叠。
4. `session.context_reset{reason: "reset"}` → `boundary`「上下文已重置」；其后的
   条目属于新 Runtime 上下文。

## Status

当前 Web 实现是对话式消息视图：turn 分组 + 工具分类卡片，无 TimelineItem 派生层与
显著性纪律；context 类工具折叠没有失败打断规则；`mo` 领域操作未经识别；SessionInput
受理与 AgentTurn 状态以独立证据区呈现，未编入时间线；无原始事件视图。

transcript 事实、持久化与实时推送链路已齐备，本模型不需要新增 transcript 事实，也
不改变 Server 职责——全部派生可在 Web 客户端本地完成。实施 issue 待从本 spec 创建。
