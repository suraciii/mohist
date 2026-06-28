## Why

Session transcript 页面在 `turns.length > 0` 的主使用路径上完全不渲染 `SessionHeader`（`SessionPage.tsx:759-789`），用户看不到 session 标题、状态、stage、所属 issue 链接，顶部仅剩一条 `recoveryBar`；长 session 只能无差别滚动，没有 turn 导航、键盘快捷键、复制全文、代码高亮，移动端 header 还会换行错乱。这是 Web 前端一次聚焦的阅读体验改造，不涉及 API 或持久化。

## What Changes

- 主 transcript 视图（已有 turn）始终渲染 header/breadcrumb：session 标题、状态徽标、stage、issue 返回链接、turn 计数，与空态/等待态分支一致；`recoveryBar` 作为 header 的子区域而非独立窄条。
- 新版 transcript layout 每个 turn 显示 turn 级别时间戳。
- 提供 turn 间快速跳转：目录/锚点列表，点击跳到对应 turn。
- 代码块进行语法高亮（在现有 `react-markdown` + `remark-gfm` 基础上接入高亮管线）。
- 基础键盘快捷键：`j`/`k` = 下/上一个 turn，`g` = 顶部，`G` = 底部；快捷键不与 followup composer 输入冲突（输入框聚焦时不触发）。
- 新增「复制全文」按钮，一键复制整个 transcript 纯文本到剪贴板。
- 移动端基础适配：header 在小屏不换行错乱、prompt/assistant 卡片不变形、无横向溢出（覆盖 320–430px 视口）。

## Capabilities

### New Capabilities

- `session-transcript-navigation`: 长.session transcript 的导航与整篇操作契约——turn 级 TOC/锚点跳转、键盘快捷键（j/k/g/G，输入聚焦时让位）、「复制全文」动作的行为与可用性边界。

### Modified Capabilities

- `agent-session-ui`: 在既有「可读 transcript」契约上新增要求——主视图（turns > 0）始终渲染 header/breadcrumb（标题、状态、stage、issue 链接、turn 计数）；每个 turn 显示时间戳；代码块带语法高亮；移动端窄屏下 header 与卡片不破坏、无横向溢出。

## Impact

- **web**：
  - `packages/web/src/pages/session/ui/SessionPage.tsx`：主视图分支（约 759-789 行）改为渲染 `SessionHeader`，`recoveryBar` 并入 header；接入 TOC/快捷键/复制全文的页面级编排。
  - `packages/web/src/widgets/session-transcript/`：`SessionTranscriptLayout` / `StickySessionTitle` / `TurnList` / `AssistantParts` 增加 turn 时间戳、TOC、复制动作、键盘事件处理；代码块渲染路径接入语法高亮。
  - 移动端：header 与 transcript 卡片的响应式 className 调整，消除窄屏横向溢出。
- **依赖**：新增一个语法高亮依赖（在 `react-markdown` + `remark-gfm` 之上，候选 `rehype-highlight` 或 `shiki`），具体选型在 design 阶段定。
- **测试**：`packages/web` 扩展 SessionPage 主视图 header 渲染、turn 时间戳、TOC 跳转、快捷键、复制全文、移动端宽度下无横向溢出等用例。
- **Server / Runner / CLI / API / 持久化**：无改动。
