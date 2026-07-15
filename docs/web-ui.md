# Web UI 指南

Web UI 是日常使用 Mohist 的主要入口。访问 `http://localhost:3456`。

## 页面一览

| 页面 | 用途 |
|---|---|
| **看板（Home）** | 默认页。所有 issue 按状态分列 |
| **Issue 详情** | 单个 issue 的全部信息和操作 |
| **Issue 改动文件** | 看一个 issue 改了哪些文件、diff |
| **AgentSession** | Workflow 或 Mohist Agent 执行时的对话记录与操作 |
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

进入 issue 的主要操作界面。分两栏（桌面）/ 单栏（移动）。

### 顶部信息

- Number / Title
- Priority / Workflow Stage / Health / Running pills
- Labels
- Primary Epic（如有，可点跳转）
- 创建/更新时间

### 左侧主区

按从上到下：

1. **Workflow 进度视图** — 当前 workflow stage 的进度和子任务
2. **Workflow Profile 编辑器** — 修改这个 issue 的 workflow profile
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

## AgentSession 页

从 Issue 的 Workflow Session 列表或 Mohist Agent 的 Session 列表进入。

这里展示 Workflow 或 Mohist Agent 执行时的对话记录。可以：

- 看每个回合的消息和工具调用
- 看模型、用量、压缩记录和会话沿革
- 提交 follow-up：执行中进入当前回合，空闲时开始下一回合
- Compact：使用当前执行后端的原生能力压缩上下文
- Reset：在 Session 空闲时开始一段没有旧上下文的新对话，同时保留旧会话记录
- 调试某个回合为什么产生当前结果

Compact 不会创建一段伪装成原 Session 的新对话。Reset 才会建立新的底层 Session，
并继续显示在同一个 AgentSession 下。Session 来源与身份见
[Agent 与 AgentSession](agents.md)；OpenCode 操作语义见
[`mohist/opencode` Action](actions/opencode.md)。

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
| **Workflows** | Workflow profile 管理 |
| **Templates** | Prompt 模板编辑 |
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

需要严肃的移动端工作流，等 PWA + push notification 完工。

---

对应源码：`packages/web/`。
