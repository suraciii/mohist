## Why

打开 Mohist session 页面，第一屏被重复信息占据：header 已经把 session 名、状态、阶段、模型、轮次、时间、session id 全列了一遍，紧贴在下面的 sticky title 又把 session 名 + 状态原样再贴一遍，再加上 sibling sidebar 的 prev/next 又是一份。真正要读的 transcript 只剩一小条。Cancel session 这种低频/危险操作又和这些元信息挤在同一行视觉权重最高的格子。读完一个 session 之前，用户要先花时间辨认哪些信息是重复的、哪些操作为什么不可用、那些"8h ago"到底对应几号几月。

本 issue 属于 epic #49 "Session 会话浏览体验"，与 #427（transcript 内容区）、#428（实时活动）、#429（mini timeline + 跳转高亮）共同把 session 页打磨成"像读 coding agent 会话一样读 mohist session"。本 issue 只动页面框架层（header、sticky 条、操作区、composer 提示），与 #427（transcript 内容区）范围正交，可并行启动。

## What Changes

- **header 元信息压缩为单行**：`SessionHeader` 中除 back / issueTitle / workflow context 这条上下文面包屑以外，session 名 + 状态徽章 + 阶段 chip + 模型 + 轮次 + 时间 + 耗时 + session id 全部压成一行；session id 改为点击复制完整值（取代当前只显示前 8 位的纯文本）。当前实现里 `flex flex-col ... sm:flex-row` 的多行布局、各 `hidden md:inline` 散落分隔符都收敛掉。
- **sticky 标题条改为滚动离开 header 后才出现**：当前 `StickySessionTitle` 在 scroll container 顶 `top-0` 常驻 sticky，导致 session 名 + 状态在首屏与 header 重复。改为：scroll container 顶的 sticky 元素在 outer `SessionHeader` 滚出视口前保持隐藏（不挂载或 `inert` + `visibility: hidden`），outer header 滚出后再 sticky 显示；sticky 条仍只保留 session 名 + 状态 + 轮次，不再带任何其它元信息。
- **Cancel session 视觉权重降级**：当前 Cancel 是 header 中唯一 `variant="destructive"` 的 primary CTA，与元信息并排抢视觉焦点。改为次要样式（outline / ghost / icon-only）+ 收进 header 的次要操作槽（如 kebab 菜单或尾部链接按钮），仍需保持可发现性与可访问性（aria-label、focusable、确认弹窗不变）。
- **Compact / Reset 禁用原因悬浮说明**：当前 `SessionRecoveryActions` 禁用态只用 `title="Unavailable while session is active"` 作为原生浏览器 tooltip，鼠标停留短暂、信息粗略。改为：由现有的 `Tooltip` 组件包裹禁用按钮渲染结构化悬浮说明（标题 + 详细原因）。运行中的 session 复用现有 `data-active="true"` 标记；已有 Compact / Reset mutation 在执行时复用既有 pending 状态。两者都不引入新的 `data-disabled-reason` 闭合枚举；native `title` 属性移除以避免重复 tooltip。
- **sibling 导航去重**：header 内的 `siblingNav` slot（prev / next）移除，因为 `siblingSidebar` 已经承载了完整 sibling 列表（含状态与当前标记）。窄屏（无 sidebar、`xl` 断点以下）保留 prev/next 的降级呈现以保证可达性，宽屏不再重复。
- **时间显示改为绝对时间为主**：当前 `formatRelativeTime` 在所有场景都返回 "8h ago" 类相对量；对已经结束较久的 session（completed / failed / stale）改为默认显示绝对日期时间（如 `Jun 17, 09:52`），相对时间（"8h ago"）降级为悬浮 tooltip 的内容；live / finalizing / probing 仍保留相对时间。判定阈值（例如 ≥ 1 小时或 statusKind 是 terminal）作为 spec 里要钉死的契约。
- **followup 输入框按 session 状态给明确提示**：`SessionFollowupComposer` 当前只有两种状态——可发送（占位符 "Send a followup message to the agent..."）和禁用（"Session is no longer accepting followups."）。改为三态文案：
  - 可交互：占位符 + 启用输入（保持现行）。
  - 排队中：`isSending` 或已有排队消息时，禁用输入并展示排队提示（"Queued — waiting for agent..."），与现有 `Sent` flash 视觉一致但持续到真正收到首个新 part。
  - 已结束不可追加：保持禁用态，但文案升级为"Session ended <relative time> — not accepting new followups."，把时间信息带进禁用文案，让用户不需要查 header 就知道什么时候结束的。
  - 文案必须与真实行为一致：禁用态下输入框真的不可写、可交互态下 submit 真的能发。
- **不影响数据层 / API / 协议 / liveness gate**：所有改动都是 `SessionDetailShell` 及其子组件 + `SessionFollowupComposer` 的 presentation 层重排与文案调整；`SessionDataSourceResult`、`displayTurns`、`isRunning`/`canFollowup`、`meta.sessionId` / `meta.lastActivityAt` / `meta.completedAt` 等字段语义不变。

## Capabilities

- `session-header-meta-line`: `SessionHeader` 中除上下文面包屑以外的元信息行——session 名 + 状态 + 阶段 + 模型 + 轮次 + 时间 + 耗时 + session id 压缩为单行；session id 改为点击复制完整值。覆盖"首屏元信息层级分明、不再被 `hidden md:inline` 散落分隔符撑高"的不变量，并暴露稳定 `data-testid` / `data-*` 属性供后续 spec 锚定。
- `session-sticky-identity`: scroll container 顶的 sticky 标题条（`data-testid="session-sticky-title"`）的可见性逻辑——在 outer `SessionHeader` 仍在视口内时保持隐藏（首屏不再重复 session 名 + 状态），header 滚出视口后才 sticky 显示。覆盖"session 名与状态在首屏只出现一次"的 acceptance 条件。
- `session-action-weight`: header 内 Cancel 按钮的视觉与位置（次要 variant + 收进操作槽）+ `SessionRecoveryActions` 禁用态 Compact / Reset 的结构化悬浮说明（基于现有 running / pending 状态，父级 `Tooltip` 渲染结构化原因）。覆盖"危险操作不再以最高视觉权重呈现"和"禁用操作有明确的禁用原因说明"两条 acceptance 条件。
- `session-sibling-nav-dedup`: header 内的 prev/next sibling 导航移除；`SiblingSessionsSidebar` 仍为唯一信息源；窄屏（无 sidebar 容器）保留 prev/next 的降级呈现作为可达性兜底。覆盖"sibling 导航在页面上只存在一处"的 acceptance 条件。
- `session-time-display`: `formatRelativeTime` 在终端态（completed / failed / stale）改为默认显示绝对日期时间，相对时间作为悬浮 tooltip 内容；live / finalizing / probing 仍使用相对时间；阈值（例如 "距离 lastActivityAt 超过 1 小时且 statusKind 是 terminal"）在 spec 中钉死。覆盖"结束已久的 session 显示绝对时间"的 acceptance 条件。
- `session-followup-state-hints`: `SessionFollowupComposer` 三态文案与行为一致性——可交互（占位符 + 启用）、排队中（输入禁用 + 排队提示，与现有 `Sent` flash 视觉对齐）、已结束不可追加（禁用 + "Session ended <relative time>"文案）。覆盖"followup 输入框按 session 状态给出明确提示，文案与真实行为一致"的 acceptance 条件。

## Impact

- **Web（`packages/web/src/pages/session/ui/SessionDetailShell.tsx`）** —— 框架层主要落点：
  - `SessionHeader` 子组件（`packages/web/src/pages/session/ui/SessionDetailShell.tsx:544`）：把 `flex flex-col ... sm:flex-row` 双行 + 散落 `hidden md:inline` 分隔符收敛为单行；session id 改为可点击复制按钮（行为可由 `data-testid` + `data-session-id` 暴露）；Cancel 按钮 variant 由 `destructive` 改为 outline / ghost 并收入操作槽。
  - `StickySessionTitle` 子组件（同文件 `:744`）：增加 scroll-engaged 状态机（IntersectionObserver 或 scroll 距离阈值），header 不可见时才 sticky；首屏不渲染或 `inert`。
  - `formatRelativeTime` 同文件 `:451`：增加 statusKind / 时间阈值分支；导出（或拆到 `shared/lib`）以便 spec 测试。
  - `SessionHeader` 顶部面包屑与 `siblingNav` slot：移除 `siblingNav` 渲染（在 `xl` 断点以下保留降级呈现需新增 viewport hook 或查询）；其它上下文面包屑不变。
  - `SessionErrorsEvidence` 不变（属于 #429 的 error-jump 范围）。
- **Web（`packages/web/src/widgets/coder-session/ui/SessionRecoveryActions.tsx`）** —— 禁用态说明：
  - 暴露 `data-disabled-reason` 闭合枚举的设想（active / prereq / unknown）已经在 D-3 的 Occam 检视中裁掉——prereq 与 unknown 分支没有对应 codebase 里的输入映射，引入会导致发明检测规则。改为直接复用现有 `data-active="true"` 与 mutation pending 状态触发结构化 `Tooltip` 文案（标题 + 详细原因），native `title` 属性移除。
- **Web（`packages/web/src/widgets/coder-session/ui/SessionFollowupComposer.tsx`）** —— 三态文案：
  - 在 `disabled` 渲染分支里把"Session is no longer accepting followups."替换为带结束时间的提示（需要传入 `endedAt` / `statusKind` props）。
  - 新增"queued"中间态：当 `isSending` 或上游 `followupIsPending` 持久化时，禁用输入 + 展示排队提示，与现有 `Sent` flash 复用视觉但持续时间延长到首个新 part 到达。
  - props 接口扩展（向后兼容：新增 props 可选，默认行为不变）。
- **Tests（`packages/web`）** —— 现有覆盖需迁移并扩展：
  - `SessionPage.sticky.test.tsx`：sticky 标题条首屏不渲染 / header 滚出后才出现的两条断言；单行 header 的层级断言（数据-testid 唯一、divider 数量）。
  - `SessionPage.cancel.test.tsx`：Cancel 视觉权重不再是 destructive primary；确认弹窗与可访问性不变。
  - `SessionRecoveryActions.test.tsx`：运行态或 mutation pending 禁用时渲染结构化 `Tooltip` 文案（标题 + 详细原因）；启用态不渲染该 tooltip；native `title` 不再出现。
  - `SessionFollowupComposer.test.tsx`：新增 queued 态断言；disabled 态文案含结束时间；行为契约（禁用态真的不可写、可交互态真的可发）。
  - 新增：sibling 导航去重断言（header 不再含 prev/next slot，宽屏 sidebar 仍含完整列表）；时间显示绝对/相对分支断言（terminal ≥ 阈值走绝对，live 走相对）；session id 点击复制断言。
- **Server / Runner / CLI / events / protocol / 数据模型 / liveness gate（#426）/ row anchor（#427）/ live activity（#428）/ jump highlight（#429）/ 状态来源**：全部不变。
- **Risk (low)**：纯 presentation 层重排与文案调整，单页面范围；多数断言在 `SessionDetailShell` 的现有 render spec 上扩展；与 #427/#428/#429 范围正交，可并行启动；唯一的边界接触面是 `SessionFollowupComposer` 的 props 扩展（向后兼容）与 `SessionRecoveryActions` 在现有 `data-active` 基础上触发结构化 `Tooltip`（native `title` 替换为结构化内容）。
