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

The Web UI SHALL let users configure an issue-level default model and optional per-stage model overrides from the issue workflow UI. Per-stage controls SHALL use real executable pipeline stages: `explore`, `plan`, `build`, `check`, and `integrate`.

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

### Requirement: Changed-files page preserves reading context across navigation

The Web UI SHALL preserve enough client-side state for users to resume reading when they return to the changed-files page for the same issue. Restored context SHALL include the user's reading position or equivalent file/hunk anchor and the active diff mode when available.

#### Scenario: Return to prior reading position

- **WHEN** a user navigates away from `/issue/:number/files` and later returns to the same issue
- **THEN** the page restores the user's prior reading context for that issue
- **AND** the user does not need to manually relocate the previously active file or hunk from the top of the page

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
