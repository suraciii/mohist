## Context

Epic 详情页的 Linked Issues 区域是桌面卡片网格的等比缩小版，在 320–430px 移动宽度下阅读与交互均不可用；依赖图在窄屏无降级。前置 issue 277 已修复详情页整体横向溢出与顶部栏/底部遮挡，本 issue 在其之上独立适配 **Linked-issue 行**与 **Graph 视图**两个呈现区域。动机见 `proposal.md`，需求契约见 `specs/epic-detail-responsive-layout/spec.md`。

当前实现的两个呈现区域：

- **`LinkedIssueRow`**（`EpicDetailPage.tsx:93-146`）：行容器 `flex items-center justify-between gap-4` 把「左侧内容（`min-w-0 flex-1`，含编号/health/status/priority badges 行 + 单行 `truncate` 标题，`EpicDetailPage.tsx:114-122`）」与「右侧动作（`flex shrink-0 flex-wrap gap-2`，含 `Start` + 内联 `Remove`，`EpicDetailPage.tsx:124-143`）」挤在同一水平行。问题：(1) 标题用 `truncate` 单行截断，长标题不可读；(2) `Remove` 是内联主行按钮、`onClick` 直接 `onRemove(issue.id)`（`EpicDetailPage.tsx:138`）—— 一点即删、无确认，且与主阅读路径争抢 320px 水平空间；(3) `LinkedIssue.startBlocker`（`types.ts:73`，类型为 `{kind:'draft'} | {kind:'waiting-for';issue:{...}}`，`issue.ts:68-70`）从未在行内展示，用户无法在手机上扫到「为何不能 Start」的阻塞原因。
- **Graph/List 区域**（`EpicDetailPage.tsx:788-896`）：tab 切换（`EpicDetailPage.tsx:791-829`）+ Graph 区（`EpicDetailPage.tsx:853-864`）+ List 区（`EpicDetailPage.tsx:866-890`）。`DependencyGraphCanvas` 在不可渲染（cyclic/empty）时 `return null`（`DependencyGraphCanvas.tsx:81-83`），graph 区变成空白——无任何用户可见说明；`showList`（`EpicDetailPage.tsx:529`）虽在 cyclic/empty 时回退到 list，但 graph 区无解释文案。窄屏下画布固定 `h-[560px] w-full`（`DependencyGraphCanvas.tsx:88`），无可读降级，且可能把页面撑出视口。

数据门控已就绪：`Start` 仅在 `canInlineStartRow(issue, hasInProgress)`（`inline-start.ts`）为真时渲染（`EpicDetailPage.tsx:109`），本设计保持不变。

测试约束（沿用 277）：`packages/web` 用 vitest + jsdom。jsdom 不做真实布局，`scrollWidth`/`clientWidth` 恒为 0，也不应用 CSS media query。按项目约定（AGENTS.md：禁止真实外部系统、测试要快），像素级溢出测量不在单测里可靠验证，改为断言**结构契约**（class、DOM 顺序、二次确认存在性、降级文案存在性），像素级判据留给真实浏览器手动核验。

利益相关方：移动端浏览 Epic 的开发者（主用户）；Web 层唯一改动方；无 API/持久化消费者。

## Goals / Non-Goals

**Goals:**
- `LinkedIssueRow` 在 320/390/430px 下成为移动优先的可扫读任务行：按编号 → 标题 → status/health → priority → start-blocker 原因的阅读优先级呈现，无横向溢出。
- 长/不可断行标题在行宽内换行，不再单行 `truncate` 截断。
- 在移动端展示「为何不能 Start」的阻塞原因（来自 `startBlocker` / 单 in-progress 规则），让用户无需进入 issue 详情即可理解。
- `Remove` 移出主阅读行（降到独立 secondary 动作），且需二次显式确认才执行 unlink；单次误触不会静默删除关联。
- Graph 视图在移动端明确降级：List 为默认且始终可达；选 Graph 时画布横向可滚，并给出「窄屏降级」提示；tab 切换在 320/390/430px 保持可点击、可理解。
- Graph 不可渲染（cyclic / empty / 其它）时，graph 区给出清楚的用户可见说明，并保留 List 兜底可继续工作。

**Non-Goals:**
- 不改 issue prerequisite 规则 / 单 in-progress 规则 / `canInlineStartRow` 门控逻辑。
- 不新增 graph layout 算法（沿用 `model/layout.ts`、`model/graph.ts`）。
- 不改 Epic lifecycle API / 状态机 / 后端 / 持久化。
- 不引入真实浏览器（Playwright）像素级溢出测试（见 Risks）。
- 不重排详情页整体信息架构（277 已处理）。

## Decisions

### D1. LinkedIssueRow 改为移动优先的纵向任务行，主阅读行只放编号+标题

行容器由 `flex items-center justify-between`（左右二栏）改为**纵向**结构：

1. **阅读行**（`min-w-0`）：`#number` 链接 + 标题，标题 `flex-1 min-w-0` 独占剩余宽度，保证 320px 下也有非零换行宽度。编号在前、标题紧随，符合 spec「number → title」阅读优先级。
2. **元数据行**：health tone + status badge + priority badge，`flex flex-wrap`，排在标题之下（spec 优先级 status/health → priority）。
3. **阻塞原因行**（见 D3）：仅当不可启动时出现。
4. **动作行**（见 D4）：`Start`（主，仅 startable）+ `Remove`（secondary）单独成行，**不**与阅读行共享水平空间。

桌面端不单独优化：纵向任务行在桌面同样可读，且与「移动优先」目标一致；避免引入 `md:` 双布局分支带来的回归面与维护成本。

- 备选 A：保留左右二栏，仅给右侧动作加 `flex-wrap`（277 的兜底方案）—— 拒绝：spec 明确要求 `Remove` 不与编号/标题/status 共享主阅读行；二栏布局下右侧动作始终与阅读行同行。
- 备选 B：桌面 `md:` 恢复水平二栏 —— 拒绝：双布局分支增加回归风险与测试矩阵，且纵向行在桌面体验不打折。

### D2. 标题换行而非截断

标题由 `truncate`（`EpicDetailPage.tsx:122`）改为 `break-words [overflow-wrap:anywhere]`，长中文逐词换行、长不可断行英文 token 在任意字符处断行，不撑出视口。与 277 处理详情页标题（`EpicDetailPage.tsx:598` `[overflow-wrap:anywhere]`）一致的既定模式。

### D3. 新增 start-blocker 原因呈现（行内、纯派生）

`LinkedIssue` 已携带 `startBlocker`（`types.ts:73`）与 `canStart`，但当前行内未展示。新增一个**纯派生**的阻塞原因文案，仅当「该行不可内联启动」时渲染：

- `hasInProgress`（单 in-progress 规则命中，非本行）→ `Another issue is in progress`。
- `startBlocker.kind === 'waiting-for'` → `Waiting for #{number}`。
- `startBlocker.kind === 'draft'` → `Still a draft`。
- `health === blocked` → `Blocked`。
- 其余 `canStart === false` 兜底 → `Not startable`。

派生函数放在 `pages/epic-detail/model/`（与 `primaryLifecycleAction`、`advancement` 同层），纯函数、可单测、不引入新数据/API。不复制 `canInlineStartRow` 的判定，而是复用它（`showStart === false` 时才计算并展示原因）。

- 备选 A：复用 `getCandidateUnavailableReason`（`EpicDetailPage.tsx:164`）—— 拒绝：它作用于 `Issue`（候选选择器），类型与字段（`Issue.blocker` vs `LinkedIssue.startBlocker`）不同，混用会造成类型耦合。

### D4. Remove 降级为 secondary 动作 + 二次确认 Dialog

`Remove` 从「内联主行按钮、一点即删」改为：放在独立的**动作行**（与 `Start` 同行但语义为 secondary，视觉降级，`variant="ghost"` / 弱化样式），点击后打开一个**确认 Dialog**（复用既有 `Dialog` 组件，模式与 Close/Pause 确认一致，`EpicDetailPage.tsx:905-985`）：

- Dialog 文案：`Remove #{number} from this Epic?` + 说明 unlink 不改 issue 工作流状态。
- 仅 `Confirm`（`variant="destructive"`）才触发 `removeEpicIssue.mutate`；`Cancel` / 关闭保持关联完整。
- 单次点击 `Remove` affordance **不**触发 mutate —— 满足 spec「单次误触不静默删除」。

确认态为 `LinkedIssueRow` 内部局部 state（`removeConfirmOpen`），不上提到页面：每行独立、互不干扰，且 `removeEpicIssue` 的 `disabled`（pending）仍由页面传入控制 Confirm 按钮。

- 备选 A：`⋯` 溢出菜单（DropdownMenu）容纳 Remove —— 拒绝：项目无 `DropdownMenu` 组件，新增会引入组件 + a11y + 交互状态成本；当前每行最多 2 个动作（Start/Remove），独立动作行 + Dialog 已满足「secondary affordance + 二次确认」，且 spec 明确允许「secondary **or** overflow」。
- 备选 B：原生 `window.confirm` —— 拒绝：与既有 Close/Pause 确认 Dialog 体验不一致，且不可测、不可样式化（AGENTS.md：禁止真实外部系统）。

### D5. Graph 移动端降级：横向滚动包装 + 窄屏提示，List 始终默认且可达

`linkedIssuesView` 初值已是 `'list'`（`EpicDetailPage.tsx:449`）—— List 即默认，保持不变。当用户在移动端选 Graph 时：

- 在 graph 区外层包一个 `overflow-x-auto` 滚动容器，内层画布给定 `min-w-[640px]`（或类似下限），让画布在窄屏**横向滚动**而非把页面撑出视口（消除页面级横向溢出，满足 spec 行无溢出判据）。
- 在画布上方加一条**仅移动端可见**（`md:hidden`，纯 CSS、无 JS 媒体查询）的提示：`Graph works best on wider screens — swipe to explore.`，桌面端隐藏。
- tab 切换（`EpicDetailPage.tsx:798-828`）已是两个紧凑按钮 + `inline-flex rounded-md`，320px 下宽度足够；保留现有 class 与 `data-testid`，不改交互。

不引入 `useMediaQuery`/`useIsMobile` JS 钩子：降级完全由 CSS 驱动（`overflow-x-auto` + `min-w` + `md:hidden`），零 JS 状态、无 SSR 不一致、jsdom 下可断言 class 契约。

- 备选 A：移动端强制隐藏 Graph tab、只允许 List —— 拒绝：spec 要求「可用或明确降级」，且 tab 必须可点击；隐藏等于不可用，违反「Graph/List tab 切换在移动端必须可点击」。
- 备选 B：JS 媒体查询在移动端默认切回 List —— 拒绝：引入 JS 断点状态、SSR/hydration 风险，且 List 已是初值；CSS 横滚 + 提示更简单可靠。

### D6. Graph 不可渲染时给出用户可见说明 + List 兜底

当前 canvas 在 cyclic/empty 时 `return null`（`DependencyGraphCanvas.tsx:81-83`），graph 区空白。改为：graph 区根据 `graphRenderable.reason`（`EpicDetailPage.tsx:450`）渲染**解释 banner**：

- `cyclic` → `Dependency graph has a cycle and can't be drawn. Use the list below.`
- `empty` → `Not enough linked issues to draw a graph. Use the list below.`
- 其它/未知（含渲染异常的兜底）→ `Graph is unavailable. Use the list below.`

`showList`（`EpicDetailPage.tsx:529`）在 cyclic/empty 时已为 true，list 同步显示作为兜底；对「其它」状态，扩展 `showList` 使其在 `graphSelected && !graphRenderable.renderable` 的**所有**情形下都为 true（不只 cyclic/empty），确保任何不可渲染状态 list 都在。`Renderability` 类型保持 `'renderable'|'cyclic'|'empty'`，未知态由 banner 兜底文案覆盖，不扩类型。

「其它」渲染异常的健壮性：用 React Error Boundary 包裹 `DependencyGraphWidget`（轻量、仅这一处），捕获后置 `graphRenderable` 为不可渲染并显示兜底 banner。Error Boundary 是纯前端隔离手段，不触碰后端。

### D7. 测试策略：结构契约（沿用 277），不引入真实浏览器测试

按 jsdom 约束，新增用例断言**结构契约**而非像素：
- `LinkedIssueRow`：纵向 class（`flex-col`/无 `justify-between` 主行）、标题 `[overflow-wrap:anywhere]`（非 `truncate`）、阅读行不含 `Remove`、阻塞原因文案存在（按 blocker 类型）。
- `Remove`：单次点击**不**触发 mutate、打开确认 Dialog、Confirm 后才 mutate、Cancel 保持关联。
- Graph 降级：graph 区含 `overflow-x-auto` 容器 + `md:hidden` 提示文案；tab 在 <2 issue 时隐藏、≥2 时可点击的既有用例不回归。
- Graph 不可渲染：cyclic/empty 时 banner 文案 + list 兜底；既有 cyclic 回退用例（`EpicDetailPage.test.tsx:2581`）补充 banner 断言。

spec 的 `documentElement.scrollWidth <= clientWidth` 像素判据留给真实浏览器手动核验（见 Open Questions）。

## Risks / Trade-offs

- `[jsdom 无法测量 scrollWidth/clientWidth 或应用 media query]` -> 单测断言结构契约（纵向 class、`overflow-x-auto` 容器存在、`md:hidden` 提示、标题换行 class、banner 文案）；像素级判据留真实浏览器手动核验（见 Open Questions）。
- [`Remove` 行为变更破坏既有用例]` -> 既有用例（如 `EpicDetailPage.test.tsx:206`、`:2618-2621` 直接点击 `Remove` 断言 mutate）随行为变更更新：改为「点击 Remove → 出现确认 Dialog → 点击 Confirm → mutate」。这是 spec 要求的有意行为变化。
- [纵向任务行在桌面端不如水平二栏紧凑]` -> 取舍：移动优先目标优先；纵向行桌面仍可读且信息完整；双布局分支的回归/维护成本高于该视觉取舍。
- [`overflow-x-auto` + `min-w-[640px]` 在桌面端引入不必要的横向滚动条]` -> 桌面端容器宽度 ≥768px 通常 > 640px，画布 fit 内宽不触发滚动；若需严格避免，可用 `md:overflow-visible` 桌面关掉横滚（实现时确认）。
- [Error Boundary 捕获 graph 渲染异常属「其它」态，可能与 cyclic/empty 文案混淆]` -> banner 文案明确区分三者；boundary 走兜底文案「Graph is unavailable」，与 cyclic/empty 互斥可辨。
- [阻塞原因派生函数与 `canInlineStartRow` 判定重叠]` -> 不复制判定：仅当 `showStart === false`（`canInlineStartRow` 已返回 false）时才计算并展示原因；派生函数只负责「为什么」，门控仍由 `canInlineStartRow` 唯一负责。

## Migration Plan

纯前端呈现改动，无数据/API/持久化迁移。部署即生效，回滚即还原 `EpicDetailPage.tsx`（及新增的 `model/` 派生函数、`DependencyGraphCanvas.tsx` 容器/banner、Error Boundary）。

验证步骤：
1. `npm run typecheck -w packages/web` 通过。
2. `npm run test:run -w packages/web` 通过，含新增移动端 Linked-issue 行结构、`Remove` 二次确认、Graph 降级/不可渲染 banner 用例；既有 cyclic 回退用例按 D6 补充 banner 断言。
3. 真实浏览器（DevTools 响应式 320/390/430）肉眼核验：(a) 行无横向滚动、标题换行、阻塞原因可见；(b) Remove 二次确认、Cancel 不删；(c) Graph 横滚 + 窄屏提示、tab 可切；(d) cyclic/empty graph 区显示说明且 list 可用。

## Open Questions

- 是否需要为 spec 像素级 `scrollWidth <= clientWidth` 判据引入真实浏览器测试（Playwright）？当前判断不引入（与 277 一致，受项目测试约定与范围限制），以结构契约单测 + 手动核验覆盖。如团队希望自动化像素校验，可作为后续独立 issue。
- `Remove` 二次确认是否应为整行级别的「行内展开确认」（点击后行内出现 Confirm/Cancel）而非 Dialog？当前选 Dialog：与既有 Close/Pause 确认体验一致、a11y 成熟、实现成本低；行内展开在 320px 下会挤压阅读行，与「Remove 不争抢阅读空间」目标相悖。实现后若反馈交互过重可再评估。
