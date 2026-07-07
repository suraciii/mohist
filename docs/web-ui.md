# Web UI 指南

Web UI 是日常使用 Mohist 的主要入口。访问 `http://localhost:3456`。

## 页面一览

| 页面 | 用途 |
|---|---|
| **看板（Home）** | 默认页。所有 issue 按状态分列 |
| **Issue 详情** | 单个 issue 的全部信息和操作 |
| **Issue 改动文件** | 看一个 issue 改了哪些文件、diff |
| **Coder Session** | Agent 执行时的对话回放 |
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
- Health pill（active / paused / interrupted 等）
- Running indicator（脉冲蓝点，表示 Agent 正在工作）

### 筛选和排序

看板顶部 FilterBar：

- **Priority chips** — 点 P0/P1/P2/P3/P4 多选
- **Labels** — Popover 选择
- **Search** — 按 title 搜索
- **Sort** — Priority / Number / Updated

筛选条件反映在 URL query string，可以分享链接。

### Needs Attention 条

如果某些 issue 需要你介入（blocked / awaiting approval），看板顶部会出现琥珀色的 **Needs attention** 条，点击直达。

### Runner 不可用条

如果 Runner 没连上，看板顶部会有警告条。Runner 没连时启动 issue 会失败。

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

1. **WorkflowView** — 当前 workflow stage 的进度和子任务
2. **IssueWorkflowProfileEditor** — 修改这个 issue 的 workflow profile
3. **Diff 概览** — base/head 分支、ahead/behind、文件改动数
4. **BranchBar** — 分支状态、rebase 可用性
5. **Description** — issue body（markdown 渲染）
6. **WorkflowYamlDialog** — 查看本次 run 的实际 yaml
7. **Commits** — 本 issue 的 commit 列表
8. **Comments** — 评论列表 + 新评论框

### 右侧侧栏

按从上到下：

1. **Details** — Issue Stage / Workflow Stage / Project / Repository
2. **LatestArtifactsPanel** — Plan/Check 阶段产物（点开看 proposal.md、review.md 等）
3. **Base Drift Detected**（如有）— base branch 漂移信息
4. **Workflow Interrupted**（如有）— interrupted 提示和 Resume 按钮
5. **WorkflowConvergencePanel**（如有）— convergence 信息
6. **Actions** — 主要操作区（Start / Approve / Retry / Stop / 等）
7. **IssueModelSelector** — 切换 AI 模型（整体或 per-stage）
8. **Prerequisites**（如有）— 前置依赖列表 + Add Prerequisite 输入框

### 何时用什么按钮

| 状态 | 可见按钮 | 含义 |
|---|---|---|
| Backlog | Start | 启动 workflow |
| Running | Running indicator + Force Stop | Agent 正在执行，可强停 |
| Awaiting approval | Approve / Reject | 审批 |
| Blocked | Retry / Resume / Rerun / Stop | 看错误原因选 |
| Interrupted | Resume | 进程崩了，继续 |
| Done | Close / Archive | 终态处理 |

## Issue 改动文件页

URL: `/issues/<number>/files`

显示一个 issue 改动的所有文件，含 diff 视图。

## Coder Session 页

URL: `/sessions/<session-id>`

Agent 在执行 task 时的对话回放。可以：

- 看 Agent 的执行过程
- 看每个工具调用（读文件、写文件、跑命令）
- 调试为什么 Agent 做了某个奇怪的决定

调试 plan/build 诡异行为时必看。

## Epics 页

URL: `/epics` 和 `/epics/<id>`

### 列表页

列表先展示 `idle` / `running` Epic 的当前工作进度分组，再展示独立的生命周期分区：

| 分组 | 含义 | 展示方式 |
|---|---|---|
| **Running** | `idle` / `running` Epic 中有 linked issue 正在 in-progress；这是工作进度分组，不等同于所有卡片都是 `running` 生命周期状态 | 有内容时展开 |
| **Ready to start** | `idle` / `running` Epic 中有可推进的 next issue，等待启动 | 有内容时展开 |
| **Waiting / Blocked** | `idle` / `running` Epic 中有 open linked issue，但当前没有 startable issue，详情由 nextIssueReason 解释 | 有内容时展开 |
| **Idle / Empty** | `idle` / `running` Epic 中无 next issue、无 blocker、无 linked issues | 有内容时展开 |
| **Paused** | 暂停推进（当前 in-progress issue 不中断） | 有 paused Epic 时显示 |
| **Done** | 已完成 | 有 done Epic 时折叠显示 |
| **Closed** | 已关闭 | 有 closed Epic 时折叠显示 |

每张卡片显示编号、状态、优先级、进度条（X / Y completed）、以及当前活动或下一步信息。状态为 Paused 的卡片半透明。

详情页入口：点卡片跳转到 `/epics/<id>`。

### 详情页

页面顶部按钮区提供以下生命周期操作，按当前状态显示：

| 按钮 | 出现条件 | 操作 |
|---|---|---|
| **Start Epic** | Epic 为 idle 状态 | 开始自动推进 linked issues |
| **Pause** | Epic 为 running 状态 | 暂停推进（当前 in-progress issue 不中断） |
| **Resume** | Epic 为 paused 状态 | 恢复推进，重新评估 readiness |
| **Mark Done** | `readyToMarkDone` 为 true（所有 linked issues 都已进入终态，没有 open linked issues） | 标记完成 |
| **Close Epic** | 非 terminal 状态 | 关闭 Epic |

操作按钮带 pending 状态反馈（Starting... / Pausing... / Resuming... / Marking... / Closing...）。

### 详情页信息

详情页展示三个统计卡片：
- **Progress** — X / Y delivered + 进度条 + Ready to mark done 标记。delivered 只统计已完成交付的 issue；`readyToMarkDone` 依据是否仍有 open linked issues 判断。
- **Next Issue** — 下一个待推进 issue + 推进状态说明
- **Current Activity** — 当前活动的 linked issues（按 health 分组）

下方为 Linked Issues 列表，支持添加/移除 issue、单个 issue 的 Start 按钮、以及依赖图视图（Graph tab）。

详见 [用 Epic 规划](epics.md)（生命周期状态机）。

## Activity 页

URL: `/activity`

实时活动流，看板消息级别的细粒度事件：

- Issue 状态变化
- Workflow stage 推进
- Agent session 开始/结束
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
| **Coder Agent** | 配 AI 模型（default + per-stage override） |
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
