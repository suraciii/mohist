# Web UI 指南

Web UI 是 Mohist 的备用操作和可视化平面，不是用户日常协作的工作站点。用户通常在
Slack、IDE 或其他场所直接使用接入的 Mohist Agent，或通过外部 Agent 使用 Mohist；需要
查看全局与复杂状态、核对执行证据、调整配置，或在外部入口不可用时人工接管，才进入
Web UI。

备用不等于功能残缺。启动、审批、拒绝、恢复、停止和配置等关键操作必须可以在 Web UI
完成；Mohist Agent 也必须能在 Web 中直接配置、启动和继续对话。同一操作的结果仍由
Mohist 裁决，Web UI 不建立另一套状态或规则。

打开 `http://localhost:3456`。进入任一页面时，应当能回答：发生了什么、为什么、是否需要
人工处理，以及当前可以安全执行什么操作。

## 页面一览

| 页面 | 用途 |
|---|---|
| **看板（Home）** | 默认页。查看全局推进情况和需要处理的 Issue |
| **Issue 详情** | 查看单个 Issue 的执行状态、证据和人工操作 |
| **Issue 改动文件** | 看一个 issue 改了哪些文件、diff |
| **Agents** | 配置、测试和启动 Mohist Agent，查看 Jobs、Sessions 与外部接入 |
| **AgentSession** | 理解执行会话的归属、状态、结果、诊断证据和恢复操作 |
| **Epics** | Epic 列表和详情 |
| **Activity** | 实时活动流 |
| **Logs** | 系统日志 |
| **Settings** | 项目级配置 |
| **Archived** | 归档的 issue |

顶部导航栏切换。

## 看板（Home）

### 看板列

按 issue status 分组：

- **Backlog** — 没启动的
- **In Progress** — 正在 workflow 里跑的
- **Done** — 完成的
- **Cancelled** — 取消的（默认隐藏，可点 Show cancelled 展开）

### 卡片上的信息

每张卡片显示：

- Issue number 和 title
- Priority chip（P0-P4）
- Status pill（blocked / approval / running / waiting / drift）
- Workflow stage pill（Plan / Build / Check / Integrate）
- Health pill（active / paused / blocked 等）
- Running indicator（脉冲蓝点，表示 Inline Agent 正在工作）

### 筛选和排序

看板顶部筛选栏：

- **Priority chips** — 点 P0/P1/P2/P3/P4 多选
- **Labels** — 弹出列表选择
- **Search** — 按 title 搜索
- **Sort** — Priority / Number / Updated

筛选条件包含在页面链接里，可以直接分享。

### Needs Attention 条

如果某些 issue 需要你介入（blocked / awaiting approval），看板顶部会出现琥珀色的 **Needs attention** 条，点击直达。

### Runner 不可用条

如果 Runner 没连上并影响 Workflow 推进，看板顶部会有警告条。Issue 仍可启动并等待可用 Runner。

## Issue 详情页

查看和人工操作单个 Issue 的页面。分两栏（桌面）/ 单栏（移动）。

### 顶部信息

- Number / Title
- Priority / Workflow Stage / Health / Running pills
- Labels
- Primary Epic（如有，可点跳转）
- 创建/更新时间

### 左侧主区

按从上到下：

1. **Workflow 进度视图** — 当前 workflow stage 的进度和子任务
2. **Workflow Profile 选择器** — 为这个 issue 选择或更换 Project 中的 profile
3. **Diff 概览** — base/head 分支、ahead/behind、文件改动数
4. **分支状态栏** — 分支状态、rebase 可用性
5. **Description** — issue body（markdown 渲染）
6. **Workflow 定义查看** — 查看本次 run 实际生效的 workflow 定义
7. **Commits** — 本 issue 的 commit 列表
8. **Comments** — 评论列表 + 新评论框

### 右侧侧栏

按从上到下：

1. **Details** — Issue Stage / Workflow Stage / Project / Repository
2. **最新产物面板** — Plan/Check 阶段产物（点开看 proposal.md、review.md 等）
3. **Base Drift Detected**（如有）— base branch 漂移信息
4. **Workflow Blocked**（如有）— blocked 原因和推荐恢复操作
5. **收敛面板**（如有）— convergence 信息
6. **Actions** — 主要操作区（Start / Approve / Retry / Stop / 等）
7. **模型选择器** — 切换 AI 模型（整体或 per-stage）
8. **Prerequisites**（如有）— 前置依赖列表 + Add Prerequisite 输入框

### 何时用什么按钮

| 状态 | 可见按钮 | 含义 |
|---|---|---|
| Backlog | Start | 启动 workflow |
| Running | Running indicator + Force Stop | Inline Agent 正在执行，可强停 |
| Awaiting approval | Approve / Reject | 审批 |
| Blocked | Retry / Resume / Rerun / Stop | 页面突出显示当前可用的推荐操作 |
| Done | Close / Archive | 终态处理 |

## Issue 改动文件页

URL: `/issues/<number>/files`

显示一个 issue 改动的所有文件，含 diff 视图。

## Agents 页

Agent 列表用于发现和管理 Project 内的 Mohist Agent。列表首先显示头像、名称、描述、
active / archived、Ready / Needs setup / Unknown、Runtime / Model、正在执行与排队的工作数，
以及外部接入健康，不用用户先打开 Session 才能判断 Agent 是否可用。Runner 离线或容量
不足作为单独的执行可用性显示，不能伪装成 Needs setup。

Agent 详情页包含四个连续区域：

1. **Definition**：头像、名称、描述、Instructions、Runtime、Model、Variant、Skills、
   并发限制与 active / archived 状态。配置控件由 Mohist 返回的 Runtime 能力与 Readiness
   驱动，缺口直接链接到对应字段或凭据设置。Needs setup 禁用启动；Unknown 仍允许提交并
   显示“等待 Runner 验证”；Ready 但没有空闲 Runner 时显示排队，不改成配置错误。
2. **Start session**：输入一个真实任务和可选 Issue / Epic / Repository 上下文，直接创建
   AgentJob、AgentSession、首条 SessionInput 与首个 AgentTurn。这是接入 Slack 前的规范测试
   入口。
3. **Work and conversations**：分别展示 AgentJob 结果和 AgentSession activity，不能用
   “Session 失败”代替失败的 Job。
4. **Connections**：把 Agent Readiness、Slack 安装进度、连接健康和身份同步分开
   展示。Add Slack 是可中断的步骤流，每次只突出一个下一步；Allowlist 通过姓名与头像搜索
   工作区成员。页面同时支持 Owner 转移、凭据轮换、重新验证、Enable、Disable 和 Delete，
   不让用户从一个笼统的 Connected / Failed 状态猜问题。

Agent archived 后，Start session 与 Add connection 不可用；历史 Jobs、Sessions 和
Connections 仍可读。active Agent 的 Add connection 不受 Readiness 阻塞，因为连接健康与执行
准备度是两件事。Agent 编辑只影响之后的新 Job，页面在保存前明确这个生效时机。

### 实装差距

当前 Agent 列表、编辑、直接启动和 Session 读取已经存在。AgentJob、SessionInput、AgentTurn
与并发/排队信息尚未完整汇总到 Agent 页；头像、Readiness、Connections 与 Slack setup 尚未
实装。

## AgentSession 页

从 Issue 的 Workflow Session 列表或 Mohist Agent 的 Session 列表进入。

这里展示 Workflow 或 Mohist Agent 的执行会话。Workflow 来源的 Session 主要是证据与
诊断视图；Agent launch 来源的 Session 同时是备用的直接对话入口，可以完整提交 follow-up。
它不需要成为用户的日常工作站，但不能是只读或残缺的调试页。

页面必须先解释这段会话，再展示会话内容。首屏应当回答：

- 为什么创建，以及它服务于哪个 Issue、Workflow task 或 Mohist Agent 工作
- 当前是排队、执行中、空闲还是状态未知，当前 AgentTurn 包含哪些输入、最近一次执行产生了
  什么结果
- 是否需要人工处理，以及当前有哪些安全操作

### 会话时间线

会话内容按发生顺序呈现为一条时间线。读它应当像扫视一个能干同事的进展：例行进展一眼
带过，需要介入的地方一眼看到。时间线遵守以下纪律：

- **每个条目读作一句话**：做了什么、对什么、结果如何——「编辑了 `runtime.rs`
  （+12/−3）」「运行测试 → 通过」。参数、完整输出和 diff 默认折叠，需要时才展开。
- **Agent 对 Mohist 的操作呈现为领域行为**：评论 Issue、作出审批、推进 Workflow 等
  动作呈现为对应的领域行为，可点击跳转到目标（如对应 Issue），不淹没在命令输出里。
- **失败醒目、读取安静**：失败的执行和需要人工判断的动作始终突出；读取、检索等例行
  操作低调呈现，连续出现时折叠成一条汇总。失败与关键动作从不进入折叠，也不被折叠
  挡住。
- **每条输入有状态**：每条用户消息显示 SessionInput 的受理/投递状态，多条输入可以
  归在同一个 AgentTurn 下；Turn 的排队、执行和结束在时间线中可见。
- **沉默也是状态**：排队中、等待执行后端响应、空闲、状态未知都明确呈现，时间线不会
  留下「不知道会话是否还活着」的空白。
- **上下文边界可见**：Compact 与 Reset 在时间线中以分界条目标注（「上下文已重置」），
  之前的内容保留，之后从空上下文开始。

调试时可以切换到原始事件视图：同一时间线数据未经加工的按序呈现，用于核对一次执行
为什么产生当前结果。

在此基础上，可以：

- 看模型、用量、压缩记录和当前活动状态
- 提交 follow-up：执行中加入当前执行，空闲时开始新的执行
- 取消 queued Turn，或请求停止 active Turn；停止结果未知时保留明确的 Unknown 状态
- Compact：使用当前执行后端的原生能力压缩上下文
- Reset：让后续输入从空 Runtime 上下文继续，同时保留已记录的会话内容

Compact / Reset 与缺失自动恢复的语义见
[Action 契约 · 共享语义](actions/README.md#agent-执行类-action-的共享语义)；两者都继续
显示在同一个 AgentSession 下，页面以「上下文已重置」标注，不展示底层 Session 历史。
Session 来源与身份见 [Agent 与 AgentSession](agents.md)。

### 实装差距

当前页面是对话式消息视图：工具调用有分类渲染，但条目尚未句式化，也没有显著性与折叠
纪律；Agent 对 Mohist 的领域操作尚未识别呈现；SessionInput 受理与 AgentTurn 状态已有
独立证据展示，但尚未编入时间线；原始事件视图不存在。缺失恢复尚未落地，页面仍不能
展示“后续从空上下文开始”这一状态。对应实施 issue 待从时间线 spec 创建。

## Epics 页

URL: `/epics` 和 `/epics/<id>`

### 列表页

所有 Epic 按当前工作情况分组：正在推进的、等待启动的、等待/受阻的、空闲的排在前面，Paused / Done / Closed 各自单独分区（Done / Closed 默认折叠）。每张卡片显示编号、状态、优先级、进度条（已完成 / 总数）、以及当前活动或下一步信息。点卡片进详情页。

### 详情页

- 顶部按钮区按 Epic 当前状态提供生命周期操作（Start Epic / Pause / Resume / Mark Done / Close Epic）
- 三个统计卡片：**进度**（已交付 / 总数，并提示是否已可标记完成）、**下一个 Issue**（下一个待推进 issue 和推进状态说明）、**当前活动**（正在进行的 linked issues，按 health 分组）
- 下方为 Linked Issues 列表，支持添加/移除 issue、单个 issue 的 Start 按钮、以及依赖图视图（Graph tab）

各操作在什么状态下可用、状态之间怎样转换，见 [用 Epic 规划](epics.md)。

## Activity 页

URL: `/activity`

实时活动流，看板消息级别的细粒度事件：

- Issue 状态变化
- Workflow stage 推进
- AgentSession 开始/结束
- Runner 连接/断开

调试"刚才发生了什么"时看。

## Logs 页

URL: `/logs`

系统日志（Server + Runner）。报错排查时看。

## Settings 页

URL: `/settings/<section>`

6 个 section：

| Section | 用途 |
|---|---|
| **OpenCode** | 查看 OpenCode 模型与配置 |
| **Runtime** | Runner 状态、并发容量 |
| **Repositories** | 项目关联的 git 仓库 |
| **Workflows** | Project 的 Workflow Profile collection 与默认 Profile |
| **Prompts** | Project Prompt 编辑 |
| **System** | 系统级配置 |

详见 [Workflow Profile](workflow-profiles.md) 和 [Runner 指南](runner.md)。

## Archived 页

URL: `/archived`

归档的 issue 列表。可以 unarchive。

## 移动端

Web UI 在移动端有基本适配（看板有移动布局），但当前不是核心场景。完整的移动端体验在 roadmap 中。

现在移动端能用的：

- 看板（切换 stage tab 查看不同列）
- Issue 详情（基本可读）

不够好的：

- 审批按钮较小，容易误触
- 长 body 在小屏阅读体验差
- Settings 在移动端不友好

需要严肃的移动端工作流，见 [移动端 PWA 与推送](mobile-pwa.md)（方案，未实装）。

---

对应源码：`packages/web/`。
