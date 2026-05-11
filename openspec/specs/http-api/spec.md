# OpenSpec Capability: http-api

### Requirement: API 提供项目管理接口

Server SHALL 提供项目管理的 RESTful API，基于 Hono 框架实现。API handler SHALL 通过 ProjectService 操作数据，不直接调用 StateManager 的 CRUD 方法。

#### Scenario: 列出项目
- **WHEN** CLI 请求 `GET /api/projects`
- **THEN** 返回所有已注册的项目列表

#### Scenario: 创建项目
- **WHEN** CLI 请求 `POST /api/projects` with `{ name, path }`
- **THEN** 通过 ProjectService 创建新项目
- **AND** 返回项目信息

#### Scenario: 删除项目
- **WHEN** CLI 请求 `DELETE /api/projects/:name`
- **THEN** 通过 ProjectService 从项目列表中移除项目

#### Scenario: 切换当前项目
- **WHEN** CLI 请求 `POST /api/projects/:name/use`
- **THEN** 通过 ProjectService 设置当前项目

### Requirement: API 提供状态查询接口

Server SHALL 提供 RESTful API 供 CLI 查询状态，基于 Hono 框架实现。

#### Scenario: 获取全局状态
- **WHEN** CLI 请求 `GET /api/status`
- **THEN** 返回当前项目的 Issue 状态

#### Scenario: 获取所有项目状态
- **WHEN** CLI 请求 `GET /api/status?all=true`
- **THEN** 返回所有项目的 Issue 状态

#### Scenario: 获取单个 Issue 详情
- **WHEN** CLI 请求 `GET /api/issues/:number`
- **THEN** 返回指定 Issue 的详细信息

### Requirement: API 提供操作接口

Server SHALL 提供 RESTful API 供 CLI 执行操作，基于 Hono 框架实现。API handler SHALL 通过 IssueService 操作 issue 数据，不直接调用 StateManager 的 CRUD 方法。

#### Scenario: 创建 Issue
- **WHEN** CLI 请求 `POST /api/issues` with `{ title, body?, labels? }`
- **THEN** 通过 IssueService 创建 Issue
- **AND** 返回 Issue 信息

#### Scenario: 更新 Issue
- **WHEN** CLI 请求 `PATCH /api/issues/:number` with `{ title?, body?, addLabels?, removeLabels? }`
- **THEN** 通过 IssueService 更新 Issue
- **AND** 返回更新后的 Issue

#### Scenario: 添加评论
- **WHEN** CLI 请求 `POST /api/issues/:number/comments` with `{ body }`
- **THEN** 通过 IssueService 创建 comment
- **AND** 返回 comment 信息

#### Scenario: 启动 Issue 处理
- **WHEN** CLI 请求 `POST /api/issues/:number/start`
- **THEN** Main Agent 被启动处理该 Issue
- **AND** 返回 Issue 信息和运行状态

### Requirement: API 提供问题管理接口

API SHALL 提供问题管理的 RESTful 端点。

#### Scenario: 列出 issue 的问题
- **WHEN** 请求 `GET /api/questions?issueId=xxx`
- **THEN** 返回指定 issue 的所有问题，按创建时间降序排列

#### Scenario: 回复问题
- **WHEN** 请求 `POST /api/questions/:id/reply` with `{ answer: string }`
- **THEN** 问题状态更新为 answered
- **AND** ask_user 工具的阻塞 Promise 被 resolve
- **AND** EventBus emit `question_answered` 事件，payload 包含从 issues 表查询的正确 projectId

### Requirement: question_answered 事件携带正确 projectId

`POST /api/questions/:id/reply` 端点在 emit `question_answered` 事件时，SHALL 通过 join issues 表查询该 question 对应 issue 的 projectId，并在事件 payload 中携带。SHALL NOT 使用空字符串作为 projectId。

#### Scenario: 回复问题时事件携带正确 projectId
- **WHEN** 用户 POST `POST /api/questions/:id/reply` with `{ answer: "JSON" }`
- **THEN** API 通过 question.issueId join issues 表查询 projectId
- **AND** `question_answered` 事件的 payload.projectId 为查询到的值（非空字符串）

### Requirement: API 提供配置接口

Server SHALL 提供配置管理的 RESTful API，基于 Hono 框架实现。

#### Scenario: 获取配置
- **WHEN** CLI 请求 `GET /api/config`
- **THEN** 返回当前配置（隐藏敏感信息）

#### Scenario: 设置配置
- **WHEN** CLI 请求 `PUT /api/config/:key` with `{ value }`
- **THEN** 更新配置值

### Requirement: API 处理错误情况

Server SHALL 返回清晰的错误响应，基于 Hono 框架实现。

#### Scenario: Server 未运行时
- **WHEN** CLI 请求任何 API
- **AND** server 未运行
- **THEN** 连接被拒绝（CLI 处理此错误）

#### Scenario: Issue 不存在
- **WHEN** 请求的 Issue ID 不存在
- **THEN** 返回 404 错误
- **AND** 包含错误信息 "Issue not found"

#### Scenario: Server 内部错误
- **WHEN** server 发生内部错误
- **THEN** 返回 500 错误
- **AND** 记录错误日志

### Requirement: Status API reflects M1 stage model

The status API SHALL only report stages used in M1: draft, designing, implementing, done. The response SHALL NOT include task-related fields (runningTasks, queuedTasks, activeWorkers) or waiting-stage counts (waitingDesignReview, waitingReview). The `ServerState` interface SHALL NOT contain `activeTasks` or `queuedTasks` fields.

#### Scenario: Get current project status
- **WHEN** CLI 请求 `GET /api/status`
- **THEN** the response SHALL include `issuesByStage` with only `draft`, `designing`, `implementing`, `done` counts
- **AND** the response SHALL NOT include `runningTasks`, `queuedTasks`, or `activeWorkers`

#### Scenario: ServerState has no task fields
- **WHEN** the ServerState interface is inspected
- **THEN** it SHALL NOT contain `activeTasks` or `queuedTasks`

#### Scenario: Issue show endpoint omits stale fields
- **WHEN** CLI 请求 `GET /api/issues/:number`
- **THEN** the response SHALL NOT include `progress` or `stageInfo` fields
- **AND** the issue's current stage SHALL still be available in `issue.stage`

### Requirement: Removed endpoints return 404

Endpoints that are removed (approve, resume, pause) SHALL return HTTP 404 instead of their previous behavior.

#### Scenario: Removed endpoint accessed
- **WHEN** a request is made to `POST /api/issues/:number/approve`, `POST /api/issues/:number/resume`, or `POST /api/issues/:number/pause`
- **THEN** the response SHALL have status code 404

### Requirement: Start handler 校验 issue status

`POST /api/issues/:number/start` SHALL 在执行前校验 issue status，blocked 的 issue 不允许 start。

#### Scenario: Start blocked issue
- **WHEN** CLI 请求 `POST /api/issues/:number/start`
- **AND** issue status 为 `blocked`
- **THEN** server 返回 400 错误
- **AND** 错误信息包含 "blocked"

#### Scenario: Start active issue in draft stage
- **WHEN** CLI 请求 `POST /api/issues/:number/start`
- **AND** issue status 为 `active` 且 stage 为 `draft`
- **THEN** 正常启动 agent

### Requirement: 无 project 上下文时拒绝创建 issue

`POST /api/issues` SHALL 在无当前 project 时返回错误。

#### Scenario: 无 project 上下文创建 issue
- **WHEN** CLI 请求 `POST /api/issues`
- **AND** server 无当前 project 上下文（`getCurrentProjectId()` 返回 null）
- **THEN** server 返回 400 错误
- **AND** 错误信息包含 "no active project"

#### Scenario: 有 project 上下文创建 issue
- **WHEN** CLI 请求 `POST /api/issues`
- **AND** server 有当前 project 上下文
- **THEN** 正常创建 issue

### Requirement: API 路由接收 Service 实例

API 路由工厂函数 SHALL 接收 Service 实例而非 StateManager。`createIssueRoutes` 接收 IssueService 和 ProjectService，`createProjectRoutes` 接收 ProjectService。

#### Scenario: Issue 路由使用 Service
- **WHEN** server 启动并注册 issue 路由
- **THEN** `createIssueRoutes` 接收 `issueService` 和 `projectService` 参数
- **AND** handler 中不出现 `stateManager.createIssue`、`stateManager.updateIssueStage` 等直接 CRUD 调用

#### Scenario: Project 路由使用 Service
- **WHEN** server 启动并注册 project 路由
- **THEN** `createProjectRoutes` 接收 `projectService` 参数
- **AND** handler 中不出现 `stateManager.saveProject`、`stateManager.loadProjects` 等直接 CRUD 调用

### Requirement: StateManager 仅作为 repo 工厂

StateManager SHALL 仅提供 repo 实例的 getter 和数据库初始化，不暴露与 Service 重叠的 CRUD 方法。不保留 `createIssue`、`getIssueByNumber`、`updateIssueStage`、`updateIssueStatus`、`loadProjects`、`loadIssues`、`getProjectById`、`getProjectByName`、`saveProject`、`deleteProject`、`createComment`、`getCommentsByIssue`、`getLabels`、`getCurrentProjectId`、`setCurrentProjectId` 方法。

#### Scenario: StateManager 只暴露 repo getter
- **WHEN** 检查 StateManager 的公共方法
- **THEN** 仅包含 `getProjectRepo()`、`getIssueRepo()`、`getCommentRepo()`、`getConfigRepo()`、`getLabelRepo()` 以及 `isInitialized()`
- **AND** 不包含任何直接 CRUD 方法（createIssue、loadProjects 等）

#### Scenario: config 管理由 ProjectService 承担
- **WHEN** API 需要获取或设置当前项目
- **THEN** 通过 ProjectService 的 `getCurrent()` 和 `setCurrent()` 方法
- **AND** 不通过 StateManager 的 `getCurrentProjectId()` / `setCurrentProjectId()`

#### Scenario: ProjectService 提供 getCurrentId
- **WHEN** API 需要获取当前项目 ID（字符串）
- **THEN** 通过 ProjectService 的 `getCurrentId()` 方法
- **AND** 不通过 `projectService.getCurrent()?.id`

### Requirement: Status 和 Labels 路由使用 Service

`createStatusRoutes` SHALL 接收 ProjectService 和 IssueService。`createLabelRoutes` SHALL 接收 ProjectService。

#### Scenario: Status 路由使用 Service
- **WHEN** server 启动并注册 status 路由
- **THEN** `createStatusRoutes` 接收 `projectService` 和 `issueService` 参数
- **AND** handler 中不出现 `stateManager.loadProjects`、`stateManager.loadIssues`、`stateManager.getProjectById`、`stateManager.getCurrentProjectId`

#### Scenario: Labels 路由使用 Service
- **WHEN** server 启动并注册 labels 路由
- **THEN** `createLabelRoutes` 接收 `projectService` 参数
- **AND** handler 中不出现 `stateManager.getCurrentProjectId`、`stateManager.getLabels`

### Requirement: Agent status API 返回可恢复 issues

`GET /api/agent/status` 返回值 SHALL 包含 `recoverableIssues` 数组，列出所有 `status = 'active'` 但无对应 agent session 的 issue（即上次 server 运行时未完成的 issue）。每个条目包含 `{ issueNumber, stage }`。

#### Scenario: Server 重启后检测可恢复 issues
- **WHEN** server 重启
- **AND** 数据库中存在 `status = 'active'` 且 `stage` 不是 `draft` 的 issues
- **THEN** `GET /api/agent/status` 返回的 `recoverableIssues` 数组包含这些 issue 的 number 和 stage

#### Scenario: 所有 issue 正常完成时无可恢复项
- **WHEN** 所有 issue 的 status 都不是 `active`
- **THEN** `GET /api/agent/status` 返回的 `recoverableIssues` 为空数组

### Requirement: Agent status API 暴露 ask_user 等待状态

`GET /api/agent/status` 返回值 SHALL 包含 `waitingQuestions` 数组，包含当前所有在 ask_user 中等待回答的 agent 信息：`{ issueId, issueNumber, projectId, questionId, question }`。

#### Scenario: agent 在 ask_user 中等待
- **WHEN** Main Agent 调用 ask_user 工具并阻塞等待回答
- **THEN** `GET /api/agent/status` 返回的 `waitingQuestions` 数组包含该 issue 的条目
- **AND** 条目包含 issueId、issueNumber、projectId、questionId 和 question 内容

#### Scenario: 用户回答后等待状态清除
- **WHEN** 用户通过 `POST /api/questions/:id/reply` 回答了一个问题
- **THEN** `waitingQuestions` 数组中对应条目被移除
- **AND** `GET /api/agent/status` 不再包含该 issue

### Requirement: API 支持自由文本消息注入

Server SHALL 提供 `POST /api/issues/:number/messages` 端点，允许用户在 agent 暂停时注入自由文本消息到 agent session。

#### Scenario: 注入消息并恢复 agent
- **WHEN** agent 已暂停（gate 审批点，session status 为 paused）
- **AND** 用户 POST `POST /api/issues/:number/messages` with `{ message: "改用 PostgreSQL" }`
- **THEN** 消息被追加到 agent session
- **AND** agent 自动 resume，开始新的 LLM loop
- **AND** 返回 200

#### Scenario: agent 未暂停时拒绝注入
- **WHEN** agent 正在运行（包括 ask_user 阻塞状态，session status 为 active）
- **AND** 用户 POST `POST /api/issues/:number/messages`
- **THEN** 返回 409 Conflict
- **AND** 错误信息 "Agent is not paused for issue #N"

### Requirement: Coder session detail normalized transcript

The coder session detail API SHALL return a canonical normalized transcript that the session page can render without re-projecting raw stream logs. The response SHALL preserve existing session metadata while exposing normalized turns, merged tool parts, transcript warnings, changed-file summaries, and raw-debug access where available.

#### Scenario: Detail endpoint returns normalized transcript

- **WHEN** the client requests `GET /api/issues/:number/coder-sessions/:sessionId`
- **THEN** the response includes normalized Mohist/Coder turns with merged logical tool parts
- **AND** the response includes metadata for status, last activity, event count, tool count, turn count, changed files, warnings, and unknown-tool presence when available

#### Scenario: Historical replay uses persisted data

- **WHEN** the session has persisted `session_stream_log` rows
- **THEN** the endpoint assembles the transcript from `session_stream_log`
- **AND** it does not require in-memory SSE state to render the completed session

#### Scenario: Legacy fallback remains understandable

- **WHEN** no session stream rows exist but filtered workflow log stream events exist
- **THEN** the endpoint uses workflow log fallback events to assemble a best-effort transcript
- **AND** missing prompts or ambiguous normalization are surfaced as incomplete state or transcript warnings

#### Scenario: Running session metadata is not misleading

- **WHEN** a session is still running or finalizing
- **THEN** terminal fields such as completed timestamp and completed duration are not presented as completed-session facts
- **AND** the response still exposes last activity and current display status data for the live page

### Requirement: Issue APIs expose model metadata

Issue create, update, list, and detail APIs SHALL accept and return issue-level model metadata where applicable. Model values SHALL use `provider/model` format, and invalid model metadata SHALL be rejected before persistence.

#### Scenario: Create issue with model metadata

- **WHEN** `POST /api/issues` is called with `model` and `stageModels`
- **THEN** the issue is created with those model overrides
- **AND** the response includes `model` and `stageModels`

#### Scenario: Update issue stage model overrides

- **WHEN** `PATCH /api/issues/:number` is called with `stageModels: { "plan": "anthropic/claude-opus-4-20250514" }`
- **THEN** the issue stage model overrides are replaced with the submitted map
- **AND** the response includes the updated `stageModels`

#### Scenario: Clear issue stage model overrides

- **WHEN** `PATCH /api/issues/:number` is called with `stageModels: null`
- **THEN** per-stage issue overrides are cleared
- **AND** the issue can fall back to global stage model configuration

#### Scenario: Reject invalid model metadata

- **WHEN** issue create or update receives a `model` or `stageModels` value that is not in `provider/model` format
- **THEN** the API returns HTTP 400
- **AND** the issue is not updated with the invalid model metadata

### Requirement: REQ-API-001 API exposes current session liveness data

Issue/session API responses SHALL expose current session call state and liveness fields needed by CLI and Web clients.

#### Scenario: Coder session list includes liveness fields
- **WHEN** a client requests an issue's coder sessions
- **THEN** each session item SHALL include status, `lastDataAt`, `probeSentAt`, `probeDeadlineAt`, and `failureReason`

#### Scenario: Coder session detail includes liveness metadata
- **WHEN** a client requests a coder session detail transcript
- **THEN** metadata SHALL include status, `lastDataAt`, `probeSentAt`, `probeDeadlineAt`, and `failureReason`
- **AND** `probing` SHALL be represented as the current checking-session state

#### Scenario: Agent/session status exposes active session state
- **WHEN** a client requests agent or issue status data for an issue
- **THEN** the response SHALL include enough current-session data to distinguish Running, Checking session, Session failed, and No active session

#### Scenario: API does not expose health taxonomy
- **WHEN** API responses include session liveness state
- **THEN** they SHALL NOT expose healthy, quiet, stale, hung-suspected, or recoverable as authoritative session states

### Requirement: simplified check-stage public model

The HTTP API SHALL expose the simplified CHECK-stage public model for new check-stage runs: `ai-review` as task history, and `review-passed`, `merge-ready`, and `user-approval` as visible checks or approval state. Approval endpoints SHALL validate that the current approval snapshot corresponds to passing review and merge checks for the current worktree snapshot.

#### Scenario: Issue detail exposes simplified checks

- **WHEN** a client requests `GET /api/issues/:number` for an issue in or after a new CHECK-stage run
- **THEN** the response SHALL expose CHECK-stage visible checks named `review-passed`, `merge-ready`, and `user-approval`
- **AND** it SHALL NOT require clients to interpret `health:check`, `merge-readiness`, `integration-health-gate-preview`, or `ai-review` as visible check names

#### Scenario: Check suite endpoint exposes simplified checks

- **WHEN** a client requests `GET /api/issues/:number/check-suite` for a new CHECK-stage run
- **THEN** the active check suite SHALL contain `review-passed`, `merge-ready`, and `user-approval` check state
- **AND** it SHALL NOT initialize `ai-review` as a check state key for new runs

#### Scenario: Approval validates current reviewed merge-ready snapshot

- **WHEN** a client approves CHECK-stage user approval
- **THEN** the API SHALL require `review-passed` to be passed for the approval snapshot
- **AND** it SHALL require `merge-ready` to be passed for the approval snapshot
- **AND** it SHALL reject approval if current `HEAD`, worktree cleanliness, or approval snapshot no longer matches the passed review and merge state

