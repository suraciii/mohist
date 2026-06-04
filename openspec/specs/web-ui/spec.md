# OpenSpec Capability: web-ui

### Requirement: Web UI 实时响应 agent 暂停状态

Web UI SHALL 监听 `agent_paused` SSE 事件，在收到事件后刷新 issue 详情和列表数据，使审批提示自动出现。

#### Scenario: agent 暂停后审批面板自动显示
- **WHEN** agent 完成一个带 approval 的阶段
- **AND** 用户正在 Web UI 查看该 issue
- **THEN** 不需要手动刷新，审批面板自动显示 "Approve & Continue" 按钮

#### Scenario: Issue 卡片状态实时更新
- **WHEN** agent 暂停
- **AND** 用户在看板页面
- **THEN** 对应 issue 卡片自动更新状态指示器（显示 "Needs Approval" 或类似标记）

### Requirement: 移除无功能的 Skip 按钮

Issue 详情页的审批面板 SHALL 只保留可用操作。当前 Skip 按钮无后端支持，SHALL 被移除以避免误导用户。

#### Scenario: 审批面板只显示可用操作
- **WHEN** 用户查看需要审批的 issue
- **THEN** 审批面板只显示 "Approve & Continue" 按钮
- **AND** 不显示无功能的 Skip 按钮

### Requirement: Web UI 展示 agent 提问

Web UI Issue 详情页 SHALL 展示当前 issue 的 pending 问题，并提供回复界面。

#### Scenario: 收到问题通知后显示问题面板
- **WHEN** SSE 收到 `question_asked` 事件（当前 issue）
- **THEN** Issue 详情页显示问题面板，包含问题文本和回复输入框

#### Scenario: 用户回复问题
- **WHEN** 用户在问题面板输入回复并点击提交
- **THEN** 调用 `POST /api/questions/:id/reply` 发送回复
- **AND** 问题面板更新为已回复状态
- **AND** SSE 收到 `question_answered` 事件后刷新 issue 状态

#### Scenario: 无 pending 问题时隐藏面板
- **WHEN** 当前 issue 没有 pending 状态的问题
- **THEN** 不显示问题面板

### Requirement: WebUI 支持通过对话框创建项目

WebUI SHALL 提供 `CreateProjectDialog` 组件，允许用户输入项目名称并通过 `DialogSelectDirectory` 选择工作目录路径来创建新项目。创建成功后 SHALL 自动切换到新项目并刷新项目列表。

#### Scenario: 成功创建项目
- **WHEN** 用户在 Header 下拉菜单点击 "New Project"
- **AND** 在对话框中输入名称 "my-project"
- **AND** 通过目录浏览器选择路径 "/home/user/repos/my-project"
- **AND** 点击 "Create"
- **THEN** 发送 `POST /api/projects` 请求（body: `{name, path}`）
- **AND** 项目列表自动刷新
- **AND** 当前项目自动切换到新创建的项目

#### Scenario: 创建项目名称已存在
- **WHEN** 用户输入已存在的项目名称
- **AND** 点击 "Create"
- **THEN** 后端返回 409 错误
- **AND** 对话框显示错误提示 "Project name already exists"
- **AND** 对话框保持打开状态

#### Scenario: 路径字段为空（验证失败）
- **WHEN** 用户只输入名称，未选择路径
- **AND** 点击 "Create"
- **THEN** 前端验证阻止提交
- **AND** 显示 "Path is required" 错误提示
- **AND** 不发送 API 请求

### Requirement: WebUI 提供搜索式目录浏览器

WebUI SHALL 提供 `DialogSelectDirectory` 组件，允许用户通过搜索、路径输入、Tab 补全和最近项目列表来选择目录。

#### Scenario: 模糊搜索目录
- **WHEN** 用户在搜索框中输入纯文本 "myapp"（不含 `/` 或 `~`）
- **THEN** 调用 `GET /api/fs/search?query=myapp&limit=50`
- **AND** 结果按 fuzzysort 相关度排序显示

#### Scenario: 路径输入逐段浏览
- **WHEN** 用户在搜索框中输入路径 "~/repos/my"
- **THEN** 解析为 HOME 起始的绝对路径
- **AND** 逐段调用 `GET /api/fs/list` 获取每级子目录
- **AND** 对最后一段 "my" 使用 fuzzysort 匹配

#### Scenario: Tab 键路径补全
- **WHEN** 用户输入 "~/repos/my-app" 并按 Tab
- **AND** 存在唯一匹配目录 "my-app-backend"
- **THEN** 搜索框自动补全为 "~/repos/my-app-backend/"

#### Scenario: 显示最近项目
- **WHEN** DialogSelectDirectory 打开
- **THEN** 顶部显示已创建的项目列表（最多 5 个），按最近更新时间排序

#### Scenario: 选择目录
- **WHEN** 用户点击某个目录项
- **THEN** DialogSelectDirectory 关闭
- **AND** 选中的绝对路径传递给调用方（CreateProjectDialog 的 path 字段）

### Requirement: WebUI 支持删除项目

WebUI SHALL 在 Header 项目下拉菜单中提供 "Delete Project" 操作，删除前 SHALL 弹出确认对话框。

#### Scenario: 成功删除项目
- **WHEN** 用户在 Header 下拉菜单点击 "Delete Project"
- **AND** 在确认对话框中点击 "Delete"
- **THEN** 发送 `DELETE /api/projects/:name` 请求
- **AND** 项目列表自动刷新
- **AND** 如果删除的是当前项目，切换到列表中第一个项目

#### Scenario: 删除最后一个项目
- **WHEN** 用户删除最后一个项目
- **AND** 在确认对话框中点击 "Delete"
- **THEN** 发送 `DELETE /api/projects/:name` 请求
- **AND** 项目列表为空
- **AND** 显示空状态引导页面

#### Scenario: 删除失败
- **WHEN** 后端返回错误（如项目不存在）
- **THEN** 显示错误提示信息

### Requirement: 无项目时显示空状态引导

WebUI SHALL 在没有项目时显示空状态引导页面，替代看板视图和 "Loading..." 文本。

#### Scenario: 首次访问无项目
- **WHEN** 用户打开 WebUI
- **AND** 项目列表为空
- **THEN** 显示空状态页面，包含提示文字 "No projects yet"
- **AND** 显示 "Create Project" 按钮

#### Scenario: 从空状态创建项目
- **WHEN** 用户在空状态页面点击 "Create Project"
- **THEN** 弹出 `CreateProjectDialog`
- **AND** 创建成功后自动切换到看板视图

#### Scenario: 无项目时访问 Explore 页面
- **WHEN** 用户访问 `/explore` 路由
- **AND** 项目列表为空
- **THEN** SHALL 显示空状态引导页面（与首页一致的引导）
- **AND** 不 SHALL 显示 "Loading..." 文本

### Requirement: KanbanView 不再负责 projectId 初始化

KanbanView SHALL 移除 `useEffect` 中调用 `setProjectId` 和 `setProjects` 的逻辑。projectId 和 projects 的初始化 SHALL 由 AppContent 中的 React Query hooks 负责，ProjectGuard 负责兜底处理。

#### Scenario: KanbanView 不包含 project context 写入
- **WHEN** KanbanView 源码被检查
- **THEN** 不存在调用 `setProjectId` 的代码
- **AND** 不存在调用 `setProjects` 的代码
- **AND** 首页看板在有项目时正常显示 issues

### Requirement: 前端 API client 提供 useCurrentProject 方法

`api.ts` SHALL 添加 `getCurrentProject` 方法，对应后端 `GET /api/projects/current`。

#### Scenario: getCurrentProject 调用成功
- **WHEN** 调用 `api.getCurrentProject()`
- **THEN** 发送 `GET /api/projects/current` 请求
- **AND** 成功时返回 `Project` 对象

#### Scenario: getCurrentProject 无 currentProject
- **WHEN** 调用 `api.getCurrentProject()`
- **AND** 后端返回 404
- **THEN** 返回 `null`
- **AND** 不抛出异常

### Requirement: 前端 hooks 提供 useCurrentProject

`useQueries.ts` SHALL 新增 `useCurrentProject` hook，封装 `GET /api/projects/current` 请求。

#### Scenario: 调用 useCurrentProject
- **WHEN** 组件调用 `useCurrentProject()`
- **THEN** 发起 `GET /api/projects/current` 请求
- **AND** 成功时返回 `Project` 对象
- **AND** 404 或无 currentProject 时返回 `null`
- **AND** 请求失败时不抛出异常

### Requirement: 前端 API client 补齐项目管理方法

`api.ts` SHALL 添加 `createProject`、`deleteProject`、`useProject` 方法，分别对应后端 `POST /api/projects`、`DELETE /api/projects/:name`、`POST /api/projects/:name/use`。

#### Scenario: createProject 调用
- **WHEN** 调用 `api.createProject({name: "x", path: "/y"})`
- **THEN** 发送 `POST /api/projects` 请求，body 为 `{name, path}`
- **AND** 返回创建的 `Project` 对象

#### Scenario: deleteProject 调用
- **WHEN** 调用 `api.deleteProject("x")`
- **THEN** 发送 `DELETE /api/projects/x` 请求
- **AND** 返回成功消息

#### Scenario: useProject 调用
- **WHEN** 调用 `api.useProject("x")`
- **THEN** 发送 `POST /api/projects/x/use` 请求
- **AND** 返回更新的项目对象

### Requirement: REQ-WUI-002 Pipeline UI shows repeated check attempts

The pipeline UI SHALL preserve visibility of repeated check results caused by failed check -> fix task -> re-check flows. It SHALL NOT collapse repeated checks in a way that hides the failure or the follow-up verification.

#### Scenario: Re-check is visible
- **WHEN** a check result list contains two results with the same check name around a fix task attempt
- **THEN** the UI SHALL display both check attempts or otherwise distinguish them as separate attempts
- **AND** check evidence SHALL be read from `CheckResult.output` rather than artifact paths

### Requirement: Web UI supports issue model overrides

The Web UI SHALL let users configure an issue-level default model and optional per-stage model overrides from the issue workflow UI. Per-stage controls SHALL use real executable pipeline stages: `plan`, `build`, `check`, and `integrate`.

#### Scenario: Configure issue default model

- **WHEN** a user selects a model in the Issue Detail model selector
- **THEN** the UI updates the issue `model` through the issue API
- **AND** the selector shows that the issue-level override is active

#### Scenario: Configure issue stage model override

- **WHEN** a user expands advanced stage overrides on Issue Detail and selects a model for `build`
- **THEN** the UI updates `stageModels.build` through the issue API
- **AND** the issue detail refresh shows the selected build-stage override

#### Scenario: Clear issue model overrides

- **WHEN** a user clears the issue default model or a stage-specific override
- **THEN** the UI sends `null` or an override map without that stage as appropriate
- **AND** the issue falls back to lower-priority model configuration

#### Scenario: Stage lists match executable pipeline stages

- **WHEN** Settings or Issue Detail renders stage model override controls
- **THEN** the list includes `integrate`
- **AND** the list does not include `fix`

#### Scenario: Create issue with default model

- **WHEN** a user creates an issue from the Web UI and chooses a default model
- **THEN** the create request includes `model`
- **AND** the created issue stores that model override

### Requirement: REQ-WUI-ISSUE-MARKDOWN-001 Issue Detail renders Markdown content

Issue Detail Page SHALL render issue descriptions and comments as Markdown instead of raw pre-wrapped plain text. Markdown rendering SHALL support headings, paragraphs, line breaks, ordered and unordered lists, emphasis, strikethrough, inline code, fenced code blocks, blockquotes, horizontal rules, explicit Markdown links, and bare URL autolinks.

#### Scenario: Description Markdown is readable

- **WHEN** a user opens an issue whose description contains Markdown headings, lists, links, bare URLs, emphasis, strikethrough, inline code, fenced code blocks, blockquotes, or horizontal rules
- **THEN** the Description section renders those structures as formatted content
- **AND** raw Markdown markers such as heading prefixes, list prefixes, and code fences are not shown as the primary reading experience

#### Scenario: Comment Markdown is readable

- **WHEN** a user opens an issue with comments containing Markdown formatting or code snippets
- **THEN** each comment body renders the Markdown as formatted content
- **AND** comment timestamps and delete actions remain available

### Requirement: REQ-WUI-ISSUE-MARKDOWN-002 Issue Detail provides readable Markdown code styling

Issue Detail Page Markdown rendering SHALL visually distinguish inline code and fenced code blocks while preserving the existing compact gray page styling. Inline code SHALL use a light gray background, monospaced font, rounded corners, and compact padding; fenced code blocks SHALL use a light gray background, monospaced font, rounded corners, padding, and horizontal scrolling for long lines.

#### Scenario: Inline code is visually distinct

- **WHEN** a description or comment contains inline Markdown code
- **THEN** the rendered inline code is visually distinct from surrounding prose
- **AND** it uses compact styling consistent with the page text size

#### Scenario: Code block is readable

- **WHEN** a description or comment contains a fenced code block
- **THEN** the code block renders with a distinct background and monospaced font
- **AND** long lines can be read by horizontal scrolling without breaking the page layout

### Requirement: REQ-WUI-ISSUE-MARKDOWN-003 Issue Detail collapses long descriptions

Issue Detail Page SHALL keep long descriptions from dominating the first screen by collapsing descriptions that exceed the readability threshold around 600px. The user SHALL be able to expand the description to read the full rendered Markdown and collapse it again.

#### Scenario: Long description is collapsed by default

- **WHEN** a user opens an issue with a description longer than the collapse threshold
- **THEN** the Description section initially constrains the rendered content height to about 600px
- **AND** an expand control is available

#### Scenario: User expands and collapses description

- **WHEN** the user activates the expand control on a collapsed description
- **THEN** the full rendered Markdown description becomes visible
- **AND** a collapse control is available to restore the constrained view

#### Scenario: Existing issue actions still work

- **WHEN** a user edits the issue, submits a comment, or deletes a comment after Markdown rendering is enabled
- **THEN** the existing action still uses the existing API flow
- **AND** the issue detail data refresh behavior remains unchanged

### Requirement: REQ-WUI-001 Web UI shows simplified current session state

Web UI SHALL render only the simplified current opencode session call states for this feature: Running, Checking session, Session failed, and No active session.

#### Scenario: Running session displayed
- **WHEN** an issue has a current session with status `running`
- **THEN** Web UI SHALL display `Running`
- **AND** last response/data time MAY be displayed when available

#### Scenario: Probing session displayed
- **WHEN** an issue has a current session with status `probing`
- **THEN** Web UI SHALL display `Checking session`
- **AND** it SHALL display probe timing when available

#### Scenario: Failed session displayed
- **WHEN** an issue has a current session with status `failed`
- **THEN** Web UI SHALL display `Session failed`
- **AND** it SHALL display `failureReason` when available

#### Scenario: No active session displayed
- **WHEN** an issue has no current running, probing, or failed session call relevant to the current task
- **THEN** Web UI SHALL display `No active session` where the current session state is shown

#### Scenario: Complex health labels not shown
- **WHEN** Web UI renders session liveness state
- **THEN** it SHALL NOT show healthy, quiet, stale, hung-suspected, or recoverable as user-facing states for this feature

### Requirement: simplified check-stage display

The Web UI and CLI SHALL present the CHECK stage using the simplified public model. Users SHALL see `ai-review` as work being performed and SHALL see only `review-passed`, `merge-ready`, and approval as CHECK-stage decision points.

#### Scenario: UI shows ai-review as task

- **WHEN** a user views CHECK-stage progress for a new run
- **THEN** the UI SHALL show `ai-review` as task work or task history
- **AND** it SHALL NOT show `ai-review` as a check decision point

#### Scenario: UI shows simplified checks

- **WHEN** a user views CHECK-stage checks for a new run
- **THEN** the UI SHALL show review result and merge readiness using the `review-passed` and `merge-ready` check states
- **AND** it SHALL show the approval state separately as `user-approval`

#### Scenario: UI hides internal check names

- **WHEN** CHECK-stage progress or done evidence is rendered
- **THEN** the UI SHALL NOT require the user to understand `health:check`, `integration-health-gate-preview`, or `merge-readiness`
- **AND** any related diagnostic evidence SHALL be presented as supporting details rather than primary checks

### Requirement: pipeline UI shows the backlog-first stage model (REQ-001)

The Web UI SHALL render the same pipeline stage model used by the backend.

#### Scenario: Pipeline views do not show deprecated stages
- **WHEN** the user views issue cards, pipeline timelines, or other stage-order UI
- **THEN** the UI shows `backlog -> plan -> build -> check -> integrate -> done`
- **AND** it does not render `draft` or `explore` as pipeline stages

### Requirement: Explore remains available outside the pipeline model (REQ-002)

The Web UI SHALL preserve Explore-specific capabilities without representing Explore as a pipeline stage.

#### Scenario: Explore surfaces still work
- **WHEN** the user opens Explore pages, sessions, or related API-driven views
- **THEN** those surfaces continue to function
- **AND** they do not depend on `Stage.Explore` being a legal pipeline stage

### Requirement: issue-recovery-actions-match-user-intent

The Web UI SHALL present issue recovery actions according to the user intent model rather than raw internal status names. Closed issues use Reopen, paused/interrupted issues use Resume, and failed or needs-action issues use Retry or Rerun Stage. The UI SHALL NOT expose Restart.

#### Scenario: Closed issue shows reopen

- **WHEN** the user views a closed issue in the Web UI
- **THEN** the issue surface shows a Reopen action
- **AND** it does not show Resume as the primary recovery action

#### Scenario: Paused issue shows resume

- **WHEN** the user views a paused issue in the Web UI
- **THEN** the issue surface shows a Resume action
- **AND** it does not label the action Reopen

#### Scenario: Interrupted issue shows resume

- **WHEN** the user views an interrupted issue in the Web UI
- **THEN** the issue surface shows a Resume action
- **AND** the UI explains that the pipeline can continue from where it stopped

#### Scenario: Failed issue shows failure-oriented actions

- **WHEN** the user views an issue in a failed or needs-action state
- **THEN** the UI shows Retry and Rerun Stage actions when those actions are allowed
- **AND** the UI does not show Restart

#### Scenario: Blocked label is replaced for users

- **WHEN** the internal issue status is `blocked`
- **THEN** the user-visible label is rendered as `Needs action` or `Failed`
- **AND** diagnostic evidence such as blockedReason remains visible

### Requirement: REQ-WUI-001 Pipeline UI shows explicit fix tasks

The pipeline UI SHALL render the canonical stage task list returned by the backend stage-state API, including runtime-added repair tasks and excluding obsolete placeholder tasks that never execute.

#### Scenario: Plan shows only real tasks

- **WHEN** the Plan stage has both obsolete placeholder rows and real artifact task data available
- **THEN** the UI SHALL show only the real Plan tasks such as `proposal`, `specs`, `design`, `tasks`, and `self-review`
- **AND** it SHALL NOT show placeholder tasks such as `Read context files` or `Design solution`

#### Scenario: Runtime-added task is explained

- **WHEN** the stage task list includes a runtime-added repair or retry task
- **THEN** the UI SHALL render that task in the same task list as the original stage work
- **AND** it SHALL surface any available explanation metadata such as `Added after Review passed failed`

### Requirement: REQ-WUI-004 Issue Detail uses unified stage state

Issue Detail SHALL use one shared stage-state response as the source of truth for primary task and check progress. `PipelineView` and `TaskProgressPanel` SHALL present the same task list for the same stage, and checks SHALL remain visually separate from tasks.

#### Scenario: Task surfaces stay consistent

- **WHEN** a user views the same issue stage in `PipelineView` and `TaskProgressPanel`
- **THEN** both surfaces SHALL render the same canonical task list from stage-state
- **AND** they SHALL NOT disagree because one surface read placeholder or legacy progress data

#### Scenario: Checks are not promoted to tasks

- **WHEN** Issue Detail renders a stage with task progress, checks, and approval state
- **THEN** checks SHALL appear in a separate checks section
- **AND** approval SHALL remain separate from top-level task entries
- **AND** session activity, logs, and diagnostic evidence SHALL remain supporting detail rather than additional tasks

### Requirement: REQ-WUI-005 Integrate progress is visible in Issue Detail

Issue Detail progress surfaces SHALL render Integrate from persisted WorkflowRun task and check state so users can see which integration step is running, which steps completed, whether final verification passed or failed, and whether merge delivery has already happened.

#### Scenario: Integrate tasks are visible while running

- **WHEN** the active stage is Integrate and task state is available
- **THEN** Issue Detail SHALL display `integrate:spec-sync`, `integrate:archive-change`, and `integrate:merge` as separate tasks in order
- **AND** it SHALL show current running, completed, or failed status for each task

#### Scenario: Delivery metadata is visible after merge

- **WHEN** `integrate:merge` has completed
- **THEN** Issue Detail SHALL show delivery metadata including landed sha when available
- **AND** it SHALL not require users to inspect logs to know that merge occurred

#### Scenario: Final health is shown as a check, not a task

- **WHEN** Integrate check state includes `health:integrate`
- **THEN** Issue Detail SHALL render that item in the checks section rather than the task list
- **AND** it SHALL show pass/fail state and diagnostic evidence separately from Integrate task progress

### Requirement: Issue Detail changes summary links to the dedicated reader

Issue Detail SHALL keep a lightweight changes summary and provide a `View files` entry into the dedicated changed-files reader. The summary SHALL not require the user to expand inline per-file diffs in order to reach the primary file-reading experience.

#### Scenario: View files from Issue Detail

- **WHEN** a user opens Issue Detail for an issue with available change data
- **THEN** the page shows a lightweight changes summary with base/head and diffstat context
- **AND** it provides a `View files` action that navigates to `/issue/:number/files`

#### Scenario: Issue Detail remains lightweight

- **WHEN** a user is browsing Issue Detail
- **THEN** the page keeps changes context visible
- **AND** it does not require the user to stay inside issue description, comments, tasks, or session entry surfaces to perform the primary code-reading workflow

### Requirement: REQ-WUI-198-001 Web issue dialogs support priority editing

The Web UI SHALL expose issue priority in both create and edit dialogs using the same `p0`-`p4` semantics as the CLI.

#### Scenario: Create issue with priority
- **WHEN** a user opens Create Issue
- **THEN** the dialog shows a priority selector with `p0` through `p4`
- **AND** the default selection is `p2`

#### Scenario: Edit issue priority
- **WHEN** a user opens Edit Issue for an existing issue
- **THEN** the dialog shows the issue's current priority
- **AND** saving can update that priority through the issue API

### Requirement: REQ-WUI-198-002 Kanban board supports focused filtering and sorting

The Kanban board SHALL support priority filtering, label filtering, title search, and shared sort switching across all stage columns, with the board query state persisted in the URL.

#### Scenario: Priority and label filters update board counts
- **WHEN** a user applies priority or label filters
- **THEN** each stage column updates its issue list and displayed count to match the filtered set

#### Scenario: Search filters by title
- **WHEN** a user types into the board search box
- **THEN** issues are filtered in real time by title match

#### Scenario: Shared sort mode updates all columns
- **WHEN** a user switches sort mode to `updated`
- **THEN** every stage column reorders its issues using that same mode

#### Scenario: Board state is restored from URL
- **WHEN** a user refreshes the board or reopens a bookmarked filtered URL
- **THEN** priority filters, label filters, search text, and sort mode are restored from the URL

#### Scenario: Mobile board uses the same focused view
- **WHEN** a user views the board on mobile
- **THEN** the single-column stage view reflects the same filtered and sorted issue set as desktop

### Requirement: Changed-files page preserves reading context across navigation

The Web UI SHALL preserve enough client-side state for users to resume reading when they return to the changed-files page for the same issue. Restored context SHALL include the user's reading position or equivalent file/hunk anchor and the active diff mode when available.

#### Scenario: Return to prior reading position

- **WHEN** a user navigates away from `/issue/:number/files` and later returns to the same issue
- **THEN** the page restores the user's prior reading context for that issue
- **AND** the user does not need to manually relocate the previously active file or hunk from the top of the page

### Requirement: Issue Detail shows merge summary and issue commits

Issue Detail SHALL keep a lightweight changes summary and provide a `View files` entry into the dedicated changed-files reader, but the summary SHALL now describe merge intent before diff counts. Issue Detail SHALL also show a lightweight commits section so users can understand which commits compose the issue's pending merge content.

#### Scenario: View files from Issue Detail

- **WHEN** a user opens Issue Detail for an issue with available change data
- **THEN** the page shows merge framing such as `head wants to merge into base`
- **AND** shows files changed, additions, deletions, and merge-base diff context
- **AND** provides a `View files` action that navigates to `/issue/:number/files`

#### Scenario: Issue Detail commit summary

- **WHEN** a user opens Issue Detail for an issue with available commit data
- **THEN** the page shows a commits section with commit count and a list of recent issue commits
- **AND** each commit item can navigate to commit-specific inspection in the changed-files reader

#### Scenario: Issue Detail remains lightweight

- **WHEN** a user is browsing Issue Detail
- **THEN** the page keeps merge and commit context visible
- **AND** it does not embed a full changed-files diff review experience inline

### Requirement: REQ-WUI-WORKFLOW-RUN-001 Issue Detail renders WorkflowRun-backed progress

Issue Detail SHALL render user-triggered rebase as ordinary WorkflowRun task progress in the current stage task list. Rebase-specific SSE or toast feedback MAY remain as supplementary detail, but users SHALL be able to understand rebase status from the same canonical task list used for other workflow work.

#### Scenario: Rebase becomes visible task state after click

- **WHEN** a user triggers rebase for the current issue
- **THEN** Issue Detail SHALL show `Rebase branch` in the current stage task list using canonical stage-state or WorkflowRun-backed data
- **AND** the task SHALL transition through pending, running, completed, or failed like other visible tasks

#### Scenario: Rebase visibility does not rely on bespoke SSE interpretation

- **WHEN** rebase work has been scheduled in the WorkflowRun
- **THEN** Issue Detail SHALL NOT require dedicated rebase-only SSE semantics to know that rebase is part of the workflow
- **AND** any retained rebase progress or conflict messaging SHALL be secondary to canonical task-list state

### Requirement: REQ-WUI-209-001 Homepage is a decision-first work entry

The homepage SHALL surface user-actionable work before the Kanban board by rendering a compact `Needs attention` summary above the board when actionable items exist. The summary SHALL derive from existing issue and agent data and use user-facing decision labels rather than raw internal state names.

#### Scenario: Homepage surfaces actionable issues first
- **WHEN** the homepage contains issues awaiting approval, interrupted issues, blocked issues, integrate failures, or done issues that are not merged
- **THEN** the page shows a `Needs attention` summary above the board
- **AND** each summary item uses user-action language such as `Approval needed`, `Integration failed`, `Interrupted`, `Needs action`, or `Not merged`
- **AND** optional detail text may explain the secondary reason without replacing the primary action label

#### Scenario: Attention summary does not replace board navigation
- **WHEN** a user selects an item in the `Needs attention` summary
- **THEN** the user can open the relevant issue directly
- **AND** the Kanban board remains available below as the main browsing surface

### Requirement: REQ-WUI-209-002 Desktop and mobile board layouts preserve work visibility

The homepage SHALL render the Kanban board as horizontally visible stage columns side by side at `md+` widths while preserving the existing shared filter and sort behavior. On mobile, the page SHALL preserve the single-stage board model and keep issue content visible without forcing the user to scroll past a full control matrix first.

#### Scenario: Desktop board renders side by side columns
- **WHEN** a user views the homepage at `md+` widths
- **THEN** the stage columns render side by side in a horizontal board container
- **AND** the board does not stack all stage columns vertically
- **AND** existing board filtering and shared sort behavior still apply across the visible columns

#### Scenario: Mobile still prioritizes issue content
- **WHEN** a user views the homepage on mobile
- **THEN** the page keeps the single-stage board model
- **AND** filter controls are compact enough that issue content is visible in the first screen

#### Scenario: Done history remains available but de-emphasized
- **WHEN** the homepage renders the Done column
- **THEN** done/history work remains available on the board
- **AND** its presentation is visually de-emphasized relative to active and attention work

### Requirement: REQ-WUI-209-003 Homepage label filtering reaches all labels

The homepage SHALL preserve the #198 URL-backed search, priority, label, and sort model while making all project labels reachable from the label filter UI. The filter surface SHALL remain compact and SHALL NOT limit reachable labels to the first eight returned labels.

#### Scenario: Label beyond the first eight is selectable
- **WHEN** the project contains more than eight labels
- **AND** a user wants to filter by a label that is not in the first eight visible labels
- **THEN** the homepage provides a way to discover and select that label
- **AND** the board updates using the same label-filter semantics as other labels

#### Scenario: Board state remains URL-backed
- **WHEN** a user applies search, priority, label, or sort controls on the homepage
- **THEN** the board state continues to be reflected in and restored from the URL

### Requirement: REQ-WUI-209-004 Homepage regressions are covered by tests

The homepage SHALL include regression coverage for the decision-first summary, desktop multi-column visibility, and label reachability beyond the first eight labels.

#### Scenario: Desktop layout regression is caught
- **WHEN** the homepage component tests run
- **THEN** they fail if the desktop board no longer renders with a horizontal multi-column contract at `md+` widths

#### Scenario: Hidden label regression is caught
- **WHEN** the homepage component tests run against label data sets with more than eight labels
- **THEN** they verify a label beyond the first eight is discoverable/selectable
- **AND** they verify filtering by that label updates the board content or displayed counts

#### Scenario: Attention summary wording is covered
- **WHEN** the homepage component tests run against representative actionable issue data
- **THEN** they verify the `Needs attention` summary renders user-action wording rather than only raw internal status names

### Requirement: check-review-repair-surface

Issue Detail SHALL present Check review repair state as a user-facing decision surface when a Check review failure has repair evidence. The surface SHALL distinguish repair task outcome from review gate verdict and SHALL present checkpoint retry, review-only rerun, and fixing review findings as separate user intents.

#### Scenario: Check repair state is visible

- **WHEN** a user views an issue blocked by Check review failure
- **AND** stage-state includes `checkRepair`
- **THEN** Issue Detail SHALL show auto-fix status, attempts used and remaining, last repair status, follow-up review status, and stop reason
- **AND** it SHALL show unresolved review summary when available

#### Scenario: Completed repair followed by failed review is not contradictory

- **WHEN** the last `fix-review-findings` task completed
- **AND** the follow-up `review-passed` check failed
- **THEN** Issue Detail SHALL state that the last repair completed and the follow-up review failed
- **AND** it SHALL NOT present repair completion as review gate success

#### Scenario: Repair exhaustion explains next action

- **WHEN** `checkRepair` reports zero remaining automatic repair attempts
- **THEN** Issue Detail SHALL explain that auto-fix will not continue automatically
- **AND** it SHALL recommend a clear next action such as manual takeover or review-only rerun after code changes

#### Scenario: Recovery actions use explicit intent labels

- **WHEN** Check review repair state is shown
- **THEN** Issue Detail SHALL label actions by intent, including `Retry checkpoint`, `Rerun review only`, and `Fix review findings` when available
- **AND** ambiguous `Retry` SHALL NOT be the primary action label for review repair failures

### Requirement: check-review-repair-regressions

Check review repair behavior SHALL have regression coverage that protects both the structured API state and the user-facing display semantics.

#### Scenario: Backend repair projection is covered

- **WHEN** backend tests create Check state with completed repair tasks and failed follow-up review
- **THEN** tests SHALL verify attempts, repair availability, last repair status, follow-up review status, and stop reason in stage-state

#### Scenario: Exhausted retry does not look like repair

- **WHEN** backend tests retry an exhausted failed Check review
- **THEN** tests SHALL verify no new `fix-review-findings` task is scheduled

#### Scenario: Frontend display semantics are covered

- **WHEN** frontend tests render completed repair plus failed follow-up review
- **THEN** tests SHALL verify the UI displays repair-attempt-completed plus review-still-failing semantics
- **AND** tests SHALL verify exhausted repair budget guidance and explicit action labels

### Requirement: Web UI shows start prerequisites on Issue Detail

Issue Detail SHALL display issue-level start prerequisites from API-provided `prerequisites` data, including whether each prerequisite issue has been delivered.

#### Scenario: Issue Detail lists prerequisite issues
- **WHEN** a user opens Issue #201
- **AND** Issue #201 has prerequisite issues #200 and #199
- **THEN** Issue Detail shows #200 and #199 as start prerequisites
- **AND** each prerequisite row indicates whether that prerequisite issue is delivered or waiting for delivery

#### Scenario: Issue Detail does not parse body text for prerequisites
- **WHEN** Issue Detail renders start prerequisite information
- **THEN** it SHALL use structured API fields such as `prerequisites` and `startEligibility`
- **AND** it SHALL NOT infer start prerequisites by parsing the Issue description

### Requirement: Web UI cards show waiting for delivery reason

Issue list/card surfaces SHALL show a concise waiting-for-delivery reason when server-provided start eligibility reports that prerequisite issues are not delivered.

#### Scenario: Card shows waiting reason
- **WHEN** an issue card renders Issue #201
- **AND** `startEligibility.waitingForDelivery` contains Issue #200
- **THEN** the card shows a concise reason equivalent to `Waiting for #200`
- **AND** the card does not present the Issue as failed solely because it is waiting for prerequisite delivery

### Requirement: Web UI Start control respects server start eligibility

The Web UI Start control SHALL use server-provided start eligibility to explain when an Issue is waiting for prerequisite delivery, and SHALL rely on the same Server API start guard when start is attempted.

#### Scenario: Start control explains waiting for delivery
- **WHEN** Issue Detail renders Issue #201
- **AND** Issue #201 is waiting for prerequisite Issue #200 to be delivered
- **THEN** the Start control is disabled or otherwise prevented from starting immediately
- **AND** the page explains that Issue #201 is waiting for #200

#### Scenario: Start attempt surfaces server rejection
- **WHEN** a user attempts to start Issue #201 from the Web UI
- **AND** the Server API rejects the request because Issue #201 is waiting for prerequisite Issue #200 to be delivered
- **THEN** the Web UI shows the actionable server message
- **AND** it does not show that an agent session or pipeline run started

### Requirement: Web UI supports minimal prerequisite declaration

The Web UI SHALL provide the minimum interaction needed to declare that one Issue has a prerequisite issue before start, without introducing a broader graph management interface.

#### Scenario: Declare prerequisite from Issue Detail
- **WHEN** a user declares from Issue #201 that prerequisite Issue #200 must be delivered before start
- **THEN** the Web UI sends a structured API request
- **AND** Issue Detail refreshes to show Issue #200 as a start prerequisite

#### Scenario: Circular declaration error is visible
- **WHEN** the API rejects a Web UI prerequisite declaration with reason `circular-prerequisite`
- **THEN** the Web UI shows a clear validation message
- **AND** it does not add the rejected prerequisite to the displayed list

### Requirement: REQ-BDA-WUI-001 Web UI surfaces drift and stale-evidence guidance

The Web UI SHALL render projected base drift and rebase opportunity state for active issues and SHALL suppress stale Check approval actions.

#### Scenario: Drifted issue is visible in issue surfaces

- **WHEN** an active issue is drifted from the current base
- **THEN** issue cards, Issue Detail, or attention summaries SHALL show user-facing drift or needs-attention wording

#### Scenario: Deferred rebase reason is shown

- **WHEN** a rebase opportunity is deferred because mutating work is running
- **THEN** Issue Detail SHALL show why rebase is deferred
- **AND** it SHALL indicate that rebase will be reconsidered at a safe window

#### Scenario: Stale Check approval is suppressed

- **WHEN** Check approval evidence is stale due to base drift
- **THEN** the Web UI SHALL hide or replace ordinary approval actions
- **AND** it SHALL guide the user to rebase or rerun checks

#### Scenario: Conflict diagnostics are visible

- **WHEN** rebase fails or conflict resolution fails
- **THEN** Issue Detail SHALL show conflict files, failure reason, and next action guidance

#### Scenario: Drift events refresh live views

- **WHEN** drift or rebase opportunity events arrive over SSE
- **THEN** the Web UI SHALL refresh affected issue and stage-state data

### Requirement: Web UI shows Check verification approval blockers

The Web UI SHALL make failed or missing Check full verification evidence visible before approval instead of presenting the issue as merely waiting for user approval.

#### Scenario: Failed verification is visible on issue detail

- **WHEN** an issue is in Check and `health:check` has failed
- **THEN** the issue detail or approval panel SHALL show the failed Check verification gate
- **AND** it SHALL show the command, summary, duration, and log excerpt when available

#### Scenario: Approval panel indicates verified candidate

- **WHEN** Check approval is available
- **THEN** the approval panel SHALL indicate that required full verification evidence passed for the approval candidate

### Requirement: REQ-WUI-RECOVERY-001 Recovery action errors are visible

The Web UI SHALL display retry mutation errors using the same issue action error display pattern used for rerun, start, close, and reopen errors. Retry API errors SHALL NOT be swallowed or hidden after the user clicks Retry.

#### Scenario: Retry error appears in action error area
- **WHEN** a user clicks Retry on Issue detail
- **AND** `POST /api/issues/:number/retry` returns a 409 or other error
- **THEN** the action error area displays the returned retry error message
- **AND** the user can still see and choose other available recovery actions

#### Scenario: Recovery actions share error display pattern
- **WHEN** Retry, Rerun Stage, Start, Close, or Reopen fails from Issue detail
- **THEN** the failure is shown through the same visible action error area

### Requirement: Epic Web Navigation and Creation

Web UI SHALL provide a separate Epic work surface outside the issue workflow Board.

#### Scenario: Epics navigation entry

- **WHEN** a user views the main navigation
- **THEN** the navigation includes `Epics`
- **AND** Epics are not shown in Board lanes

#### Scenario: Create Epic in Web UI

- **WHEN** a user opens the create Epic form
- **THEN** the form asks for title, description, and priority
- **AND** it does not require structured success criteria or decision history

### Requirement: Epic List Web UI

Web UI SHALL list Epics with enough information for users to understand progress and next action quickly.

#### Scenario: List Epics with progress

- **WHEN** a user opens the Epics page
- **THEN** each Epic shows status, title, priority, delivered/total progress, and backend-provided next issue or ready-to-mark-done state

#### Scenario: Distinguish lifecycle groups

- **WHEN** Epics have active, done, or closed statuses
- **THEN** the list groups or clearly distinguishes those statuses

### Requirement: Epic Detail Web UI

Web UI SHALL show an Epic detail page centered on goal, progress, next issue, and linked issues.

#### Scenario: View Epic detail

- **WHEN** a user opens an Epic detail page
- **THEN** the page shows description, status, priority, delivered/total progress, next issue, and linked issues with current issue states

#### Scenario: Add linked issue

- **WHEN** a user adds an existing issue from Epic detail
- **THEN** the issue is linked to the Epic
- **AND** duplicate membership errors are shown clearly

#### Scenario: Remove linked issue

- **WHEN** a user removes a linked issue from Epic detail
- **THEN** the issue disappears from that Epic's linked issue list

#### Scenario: Lifecycle actions

- **WHEN** a user marks an Epic done or closes it from the detail page
- **THEN** the page updates to show the new Epic status

### Requirement: Issue Detail Epic Backlink

Web UI SHALL display a compact backlink from Issue Detail to the issue's primary Epic when membership exists.

#### Scenario: Linked issue shows Epic backlink

- **WHEN** a user opens Issue Detail for an issue linked to an Epic
- **THEN** the page shows `Part of Epic` with Epic id and title
- **AND** clicking the link opens the Epic detail page

#### Scenario: Unlinked issue hides Epic backlink

- **WHEN** a user opens Issue Detail for an issue without Epic membership
- **THEN** no empty or misleading Epic backlink is displayed

### Requirement: Issue Detail actions derive from recovery projection

Issue Detail SHALL render primary recovery actions from the backend recovery projection rather than from `issue.status === blocked` or other issue-level heuristics alone.

#### Scenario: Blocked issue does not automatically show Retry

- **WHEN** an issue has `status = blocked`
- **AND** the backend recovery projection does not include retry as an allowed action
- **THEN** Issue Detail SHALL NOT render or enable Retry solely because the issue is blocked

#### Scenario: Failed latest attempt enables Retry

- **WHEN** the backend recovery projection reports latest attempt state `failed`
- **AND** retry is an allowed action
- **THEN** Issue Detail SHALL render Retry as an available action

#### Scenario: Interrupted latest attempt shows interrupted guidance

- **WHEN** the backend recovery projection reports latest attempt state `interrupted`
- **THEN** Issue Detail SHALL present resume, rerun stage, or inspect guidance according to allowed actions
- **AND** it SHALL preserve blocked reason and interruption diagnostics as supporting evidence
- **AND** it SHALL NOT label the interrupted attempt as failed retryable work

#### Scenario: Running latest attempt shows wait or stop guidance

- **WHEN** the backend recovery projection reports latest attempt state `running`
- **AND** live execution evidence is present
- **THEN** Issue Detail SHALL present wait or stop guidance rather than retry

### Requirement: Web UI agrees with API recovery actions

Web UI recovery controls SHALL match the action availability returned by the API for running, completed, failed, and interrupted latest attempt states.

#### Scenario: UI follows backend action list

- **WHEN** issue detail data includes a recovery projection with allowed actions
- **THEN** the Issue Detail primary action controls SHALL be enabled only for actions present in that projection
- **AND** disabled or unavailable actions SHALL not be inferred from issue status alone

### Requirement: REQ-WUI-STRUCTURED-001 issue UI explains workflow convergence generically

The issue UI SHALL render generic workflow convergence state so users can understand whether a blocked workflow is converging without reading review prose.

#### Scenario: Blocked workflow displays convergence evidence

- **WHEN** an issue stage exposes convergence state
- **THEN** the issue detail or pipeline progress UI SHALL show the current failed check, blocked reason, blocking item count, directly repaired count, reaction attempt count, resolved count, unresolved count, and visible non-blocking follow-up items
- **AND** the UI SHALL avoid exposing review-specific lifecycle concepts as Mohist core primitives

#### Scenario: Convergence state is absent

- **WHEN** no convergence state is available
- **THEN** the issue UI SHALL preserve the existing task/check progress display
