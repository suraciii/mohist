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

### Requirement: resume-retry-rerun-reopen-contract

The issue recovery API SHALL expose distinct behaviors for `resume`, `retry`, `rerun`, and `reopen`. The API SHALL NOT provide a working `restart` recovery path.

#### Scenario: Reopen endpoint is closed-only

- **WHEN** a client requests `POST /api/issues/:number/reopen`
- **AND** the issue status is `closed`
- **THEN** the API reopens the issue to `active`
- **AND** the API does not auto-enqueue `resume-pipeline`

#### Scenario: Reopen endpoint rejects blocked recovery

- **WHEN** a client requests `POST /api/issues/:number/reopen`
- **AND** the issue status is `blocked`, `paused`, or `interrupted`
- **THEN** the API returns an error indicating reopen is only for closed issues

#### Scenario: Resume endpoint recovers paused work

- **WHEN** a client requests `POST /api/issues/:number/resume`
- **AND** the issue status is `paused`
- **THEN** the API restores the issue to `active`
- **AND** the API preserves the current stage and checkpoints
- **AND** the API enqueues resume-pipeline when runtime conditions allow recovery

#### Scenario: Resume endpoint recovers interrupted work

- **WHEN** a client requests `POST /api/issues/:number/resume`
- **AND** the issue status is `interrupted`
- **THEN** the API restores the issue to `active`
- **AND** the API preserves the current stage and checkpoints

#### Scenario: Retry endpoint no longer simulates restart

- **WHEN** a client requests `POST /api/issues/:number/retry`
- **AND** retry recovery has no usable checkpoint or retryable failure evidence
- **THEN** the API rejects the request
- **AND** the API does not reset the issue to backlog or draft as a fallback
- **AND** the error directs the client to rerun or rewind instead

#### Scenario: Restart endpoint is deprecated

- **WHEN** a client requests `POST /api/issues/:number/restart`
- **THEN** the API returns a deprecation error
- **AND** the response instructs the client to use retry, rerun, or rewind instead
- **AND** the API does not mutate issue stage, checkpoint, or status

### Requirement: start-handler-guidance-uses-current-verb-model

`POST /api/issues/:number/start` SHALL use the current recovery verb model in its error guidance.

#### Scenario: Start blocked issue

- **WHEN** a client requests `POST /api/issues/:number/start`
- **AND** the issue is in a failed or needs-action state
- **THEN** the API returns an error
- **AND** the message references retry, rerun, or rewind
- **AND** the message does not recommend restart

#### Scenario: Start closed issue

- **WHEN** a client requests `POST /api/issues/:number/start`
- **AND** the issue status is `closed`
- **THEN** the API returns an error
- **AND** the message recommends reopen

### Requirement: REQ-HTTP-001 Issue stage-state API exposes current progress

`GET /api/issues/:number/stage-state` SHALL expose the canonical user-visible workflow stage view rather than raw stored stage-task rows. Each returned stage SHALL contain the current task list, current check list, stage status, approval state when present, and task metadata needed to explain runtime-added work.

#### Scenario: Stage-state excludes obsolete placeholders

- **WHEN** the backend has stored obsolete placeholder rows alongside real workflow task evidence
- **THEN** `GET /api/issues/:number/stage-state` SHALL exclude the obsolete placeholders from the returned task list
- **AND** it SHALL return the real workflow tasks for that stage in user-visible order

#### Scenario: Stage-state includes reason-aware runtime tasks

- **WHEN** a runtime repair, retry, rebase, or conflict-resolution task exists for a stage
- **THEN** `GET /api/issues/:number/stage-state` SHALL include that task in the stage task list
- **AND** it MAY include explanation metadata such as `reason` or `causedBy`

#### Scenario: Stage-state keeps checks separate

- **WHEN** the API returns stage progress for Issue Detail
- **THEN** the response SHALL keep tasks and checks in separate collections
- **AND** supporting evidence such as task output, attempts, or artifact paths SHALL remain task/check detail data rather than separate top-level tasks

### Requirement: Coder session detail exposes normalized transcript metadata

`GET /api/issues/:number/coder-sessions/:sessionId` SHALL provide the normalized transcript and display metadata required to render the session page without reconstructing core ordering, lifecycle state, or file-change metadata from raw event logs.

#### Scenario: Detail response contains stable transcript structure

- **WHEN** the client requests a coder session detail
- **THEN** the response includes normalized turns, assistant parts, transcript metadata, and incomplete markers sufficient for replay rendering

#### Scenario: Tool metadata is display-ready

- **WHEN** tool activity is included in the detail response
- **THEN** each tool part exposes stable status and enough metadata for display, including normalized identity and file-change details for patch/edit/write operations when available

#### Scenario: Replay remains usable without live SSE state

- **WHEN** a session is refreshed after completion or temporary disconnect
- **THEN** the detail response remains sufficient to render the same visible transcript order and grouping as the live session

### Requirement: Review APIs expose availability and complete review data

`GET /api/issues/:number/diff`, `GET /api/issues/:number/commits`, and `GET /api/issues/:number/commits/:hash/diff` SHALL return availability-aware review payloads. For issue-level file review, `GET /api/issues/:number/diff` SHALL compare the current base branch worktree to the current issue branch worktree, rather than diffing from the historical merge-base.

#### Scenario: Diff API available

- **WHEN** `GET /api/issues/:number/diff` is called for an issue with an existing worktree and accessible base/head branches
- **THEN** the response data includes `available: true` and `reason: null`
- **AND** includes `base`, `head`, `summary.filesChanged`, `summary.additions`, and `summary.deletions`
- **AND** includes complete file entries with file path, additions, deletions, binary status, and per-file unified diff content

#### Scenario: Diff excludes merged base-branch changes

- **WHEN** an issue branch has previously merged the base branch and therefore contains commits already present on the current base branch
- **THEN** `GET /api/issues/:number/diff` compares `base` vs `head` directly
- **AND** files whose content is already the same on both branches are not reported as issue changes
- **AND** the returned `summary` and per-file patch content reflect only the remaining worktree differences between base and head

#### Scenario: Review data unavailable by lifecycle

- **WHEN** review data is unavailable because the worktree is removed, the issue has not started, a branch is missing, or git fails
- **THEN** the response data includes `available: false`
- **AND** `reason` is one of `worktree_removed`, `not_started`, `branch_missing`, or `git_error`
- **AND** `message` explains the cause for display in the UI

### Requirement: REQ-HTTP-WORKFLOW-RUN-001 API exposes active issue WorkflowRun

The HTTP API SHALL expose the active WorkflowRun for an issue, including run status, current stage, ordered StageRuns, tasks, checks, approval snapshots, failure reason, and delivery metadata. The API SHALL treat WorkflowRun as current state and SHALL NOT reconstruct the response from logs, checkpoints, `stage_executions`, or `stage_states` when WorkflowRun data exists.

#### Scenario: Query active WorkflowRun

- **WHEN** a client requests `GET /api/issues/:number/workflow-run`
- **AND** the issue has an active WorkflowRun
- **THEN** the response SHALL include `issueId`, `issueNumber`, WorkflowRun id, status, currentStage, and ordered StageRuns
- **AND** each StageRun SHALL include its tasks, checks, approval snapshot, failure reason, and delivery metadata when present

#### Scenario: No WorkflowRun exists yet

- **WHEN** a client requests `GET /api/issues/:number/workflow-run`
- **AND** the issue has not been started and has no WorkflowRun
- **THEN** the API SHALL return a clear empty-state or not-found response
- **AND** it SHALL NOT fabricate a WorkflowRun from `stage_executions`, logs, checkpoints, or projections

### Requirement: REQ-HTTP-WORKFLOW-RUN-002 Stage-state compatibility reads WorkflowRun when available

The existing issue stage-state API SHALL project current stage/task/check progress from WorkflowRun when a WorkflowRun exists. Legacy projection MAY remain available only for issues without WorkflowRun data.

#### Scenario: Stage-state response uses WorkflowRun

- **WHEN** a client requests `GET /api/issues/:number/stage-state`
- **AND** the issue has a WorkflowRun
- **THEN** the response SHALL be projected from WorkflowRun StageRuns, tasks, checks, approval snapshots, failure reasons, and delivery metadata
- **AND** it SHALL preserve one task list and one check list per stage

#### Scenario: Evidence is not promoted

- **WHEN** the compatibility response is built from WorkflowRun
- **THEN** `stage_executions`, `workflow_log`, session logs, check suites, and checkpoints SHALL NOT become additional user-visible tasks or checks

#### Scenario: Integrate delivery facts are visible

- **WHEN** Integrate merge has completed
- **THEN** API responses SHALL expose delivery facts including target branch, candidate head, landed sha, and whether final health failed after merge

### Requirement: Issue write endpoints accept case-insensitive priority values

`POST /api/issues` and `PATCH /api/issues/:number` SHALL accept priority values case-insensitively and normalize them to the stored lowercase priority contract.

#### Scenario: Create issue with uppercase priority
- **WHEN** a client sends `POST /api/issues` with `priority: "P2"`
- **THEN** the API accepts the request
- **AND** treats the priority the same as `"p2"`

#### Scenario: Update issue with uppercase priority
- **WHEN** a client sends `PATCH /api/issues/42` with `priority: "P0"`
- **THEN** the API accepts the request
- **AND** treats the priority the same as `"p0"`

#### Scenario: Reject invalid create priority
- **WHEN** a client sends `POST /api/issues` with `priority: "urgent"`
- **THEN** the API returns a 400-class validation error

#### Scenario: Reject invalid update priority
- **WHEN** a client sends `PATCH /api/issues/42` with `priority: "urgent"`
- **THEN** the API returns a 400-class validation error

### Requirement: Issue list endpoint accepts case-insensitive priority filters

`GET /api/issues` SHALL accept uppercase or lowercase priority filter values and apply the same normalized filter semantics for both.

#### Scenario: List issues with uppercase priority filter
- **WHEN** a client requests `GET /api/issues?priority=P1`
- **THEN** the API applies the same filter as `priority=p1`

#### Scenario: Reject invalid list priority filter
- **WHEN** a client requests `GET /api/issues?priority=urgent`
- **THEN** the API returns a 400-class validation error

### Requirement: Issue coder session list endpoint returns summary metadata only

`GET /api/issues/:number/coder-sessions` SHALL return only the session summary metadata needed by the issue detail surface and SHALL NOT load or embed per-session transcript or workflow log payloads.

#### Scenario: List response excludes workflow logs and transcript payloads

- **WHEN** the client requests the coder session list for an issue
- **THEN** the response includes only lightweight session metadata needed for the list surface
- **AND** the response does not include `workflowLogs`, transcript fragments, or other per-session log payloads

#### Scenario: List path does not perform per-session log loading

- **WHEN** the server handles `GET /api/issues/:number/coder-sessions`
- **THEN** it reads session summaries without issuing per-session `session_stream_log` or `workflow_log` queries

#### Scenario: Dedicated detail endpoint remains the source of full session data

- **WHEN** the client requests `GET /api/issues/:number/coder-sessions/:sessionId`
- **THEN** the response still includes the full transcript and log-backed detail needed for session inspection

#### Scenario: High-session-count issue stays within the latency budget

- **WHEN** an issue has 50 or more coder sessions
- **THEN** `GET /api/issues/:number/coder-sessions` completes within 1 second in the project verification environment

### Requirement: REQ-API-198-001 Issue create accepts model with existing priority support

`POST /api/issues` SHALL accept optional `model` and `priority` fields in the same request body as title, body, and labels.

#### Scenario: Create issue with model and priority
- **WHEN** the server receives `POST /api/issues` with `{ title, body?, labels?, priority: "p1", model: "anthropic/claude-sonnet" }`
- **THEN** it creates the issue with both values persisted
- **AND** returns the created issue including `priority` and `model`

#### Scenario: Create issue with invalid model format
- **WHEN** the server receives `POST /api/issues` with `model: "invalid-model"`
- **THEN** it returns 400
- **AND** the error explains that `provider/model` format is required

### Requirement: Review APIs expose merge-base comparison data

`GET /api/issues/:number/diff`, `GET /api/issues/:number/commits`, and `GET /api/issues/:number/commits/:hash/diff` SHALL return availability-aware review payloads for the issue's pending merge content. Issue-level review data SHALL be framed around the current merge relationship between base and head, and issue-level diff data SHALL represent the merge-base-to-head change set rather than a generic two-dot base-vs-head comparison.

#### Scenario: Diff API returns merge-base comparison

- **WHEN** `GET /api/issues/:number/diff` is called for an issue with an existing worktree and accessible base/head branches
- **THEN** the response includes `available: true` and `reason: null`
- **AND** includes `base`, `head`, `mergeBase`, `ahead`, `behind`, `canFastForward`, and `comparison`
- **AND** `comparison` is `merge-base`
- **AND** the summary, file list, and per-file patch content are equivalent to `git diff <base>...<head>`

#### Scenario: Behind-base branch excludes base-only changes

- **WHEN** the issue branch is behind the base branch
- **THEN** `GET /api/issues/:number/diff` does not report files changed only on base
- **AND** the returned file count matches the issue's pending merge contribution from the merge base

#### Scenario: Commits API shares comparison metadata

- **WHEN** `GET /api/issues/:number/commits` is called for an issue with an existing worktree and accessible base/head branches
- **THEN** the response includes the same `base`, `head`, `mergeBase`, `ahead`, `behind`, `canFastForward`, and `comparison` metadata as the diff response
- **AND** returns the complete commit range that is reachable from head and not from base
- **AND** its summary counts are consistent with the issue-level diff response

#### Scenario: Commit diff remains commit-scoped

- **WHEN** `GET /api/issues/:number/commits/:hash/diff` is called for a commit that belongs to the issue branch
- **THEN** the response remains a single-commit diff payload
- **AND** it does not redefine the default issue-level Files changed semantic away from merge-base comparison

#### Scenario: Review data unavailable by lifecycle

- **WHEN** review data is unavailable because the worktree is removed, the issue has not started, a branch is missing, or git fails
- **THEN** the response data includes `available: false`
- **AND** `reason` is one of `worktree_removed`, `not_started`, `branch_missing`, or `git_error`
- **AND** `message` explains the cause for display in the UI

### Requirement: API 提供操作接口

`POST /api/issues/:number/rebase` SHALL schedule visible workflow rebase work for non-Done stages through the active WorkflowRun instead of enqueueing a hidden issue task queue `rebase` job. The response SHALL communicate that workflow work was scheduled, and the current stage task list SHALL become the canonical source of progress.

#### Scenario: Non-Done rebase schedules WorkflowRun task

- **WHEN** a client calls `POST /api/issues/:number/rebase` for an issue in Plan, Build, Check, or Integrate
- **THEN** the API SHALL append or reuse `rebase-branch` in the current WorkflowRun stage
- **AND** it SHALL NOT use the hidden issue task queue `rebase` job as the primary execution path
- **AND** the response SHALL indicate that rebase work is now represented in workflow task state

#### Scenario: Duplicate rebase request is idempotent for in-flight work

- **WHEN** a client calls `POST /api/issues/:number/rebase`
- **AND** the current stage already has a `rebase-branch` task in `pending` or `running` state
- **THEN** the API SHALL return success without scheduling a duplicate task
- **AND** the existing workflow task SHALL remain the canonical progress record

### Requirement: provider-api-cached-reads

The Provider API SHALL serve provider list and provider model reads from server-side in-memory provider state that is prewarmed before the HTTP server accepts requests.

#### Scenario: Provider state prewarmed before serving requests

- **WHEN** the server starts successfully
- **THEN** provider state has already built provider list and provider model-group snapshots
- **AND** the provider read endpoints can return data without performing full provider/model aggregation on the first request

#### Scenario: Provider list omits model IDs

- **WHEN** a client requests `GET /api/providers`
- **THEN** each provider item includes provider metadata such as `id`, `name`, `baseURL`, `configured`, `source`, `isBuiltin`, `isDefault`, and `apiKeyMasked`
- **AND** provider items SHALL NOT include a `models` field

#### Scenario: Provider models preserve selectable model response shape

- **WHEN** a client requests `GET /api/providers/models`
- **THEN** the response contains provider groups with `id`, `name`, `configured`, and `models`
- **AND** each model item contains `id`, `name`, `badges`, and `contextWindow`
- **AND** the response is read from provider state rather than rebuilt independently in the route handler

### Requirement: provider-list-omits-models

The web client SHALL treat `GET /api/providers` as a lightweight provider metadata endpoint and SHALL use `GET /api/providers/models` for selectable model data.

#### Scenario: Provider list UI consumes lightweight providers

- **WHEN** the AI settings provider list renders
- **THEN** it SHALL NOT read model IDs from provider items returned by `GET /api/providers`
- **AND** it SHALL continue to render provider connection status, source, default status, and masked API key state

#### Scenario: Model selectors consume model groups endpoint

- **WHEN** the AI settings model selectors render
- **THEN** they SHALL load selectable models from `GET /api/providers/models`
- **AND** model selection behavior SHALL remain unchanged

### Requirement: provider-api-performance-contract

Provider API changes SHALL be covered by regression tests that protect the lightweight response contract and cache refresh behavior.

#### Scenario: Lightweight provider response is tested

- **WHEN** provider API tests request `GET /api/providers`
- **THEN** tests verify provider items do not include `models`

#### Scenario: Cached provider model response is tested

- **WHEN** provider API tests request `GET /api/providers/models`
- **THEN** tests verify model groups preserve the expected response shape

#### Scenario: Cached state refresh is tested

- **WHEN** provider API tests mutate provider configuration
- **THEN** tests verify subsequent provider reads reflect the refreshed provider state

### Requirement: check-review-repair-state

`GET /api/issues/:number/stage-state` SHALL expose structured Check review repair state when Check review repair evidence exists. The structured state SHALL include attempts used, attempts max, attempts remaining, repair availability, last repair task, last repair status, follow-up review status, stop reason, and unresolved review summary when available.

#### Scenario: Failed review exposes repair state

- **WHEN** a client requests stage-state for an issue whose Check `review-passed` gate failed
- **THEN** the Check stage response SHALL include `checkRepair`
- **AND** `checkRepair` SHALL include attempts used, attempts max, attempts remaining, and whether repair is available
- **AND** `blockedReason` SHALL remain concise rather than becoming the only source of repair details

#### Scenario: Repair completion remains separate from review verdict

- **WHEN** `fix-review-findings` completed
- **AND** the subsequent `review-passed` check failed
- **THEN** `checkRepair.lastRepairStatus` SHALL indicate the repair completed
- **AND** `checkRepair.followUpReviewStatus` SHALL indicate the follow-up review failed
- **AND** the API SHALL NOT represent the completed repair task as review success

#### Scenario: Exhaustion is explicit

- **WHEN** the Check review repair budget is exhausted
- **THEN** `checkRepair.attemptsRemaining` SHALL be `0`
- **AND** `checkRepair.repairAvailable` SHALL be `false`
- **AND** `checkRepair.stopReason` SHALL explain that the maximum repair attempts were reached

### Requirement: check-review-recovery-actions

The issue recovery API SHALL preserve distinct user intents for Check review failures: retry checkpoint recovery, rerun review-only verification, and fix review findings. Repair actions SHALL NOT be hidden behind ambiguous checkpoint retry behavior.

#### Scenario: Retry checkpoint does not schedule exhausted repair

- **WHEN** a client retries a Check review failure after repair budget is exhausted
- **THEN** the API SHALL treat the request as checkpoint recovery
- **AND** it SHALL NOT schedule another `fix-review-findings` task
- **AND** response wording SHALL NOT imply that a new repair attempt was started

#### Scenario: Rerun review only is distinct from repair

- **WHEN** a client requests review-only rerun for a Check review failure
- **THEN** the API SHALL rerun or invalidate review verification work without appending `fix-review-findings`
- **AND** the response SHALL describe the action as review rerun rather than repair

#### Scenario: Fix review findings is explicit and bounded

- **WHEN** repair is available for a Check review failure
- **AND** a client requests fixing review findings
- **THEN** the API SHALL schedule or reuse `fix-review-findings`
- **AND** repeated requests while repair is pending or running SHALL be idempotent
- **AND** the API SHALL reject or clearly explain requests that exceed the automatic repair budget

### Requirement: API 提供状态查询接口

Server SHALL 提供 RESTful API 供 CLI 查询状态，基于 Hono 框架实现。Issue list and detail responses SHALL include structured start prerequisite and start eligibility data so clients do not parse issue body text.

#### Scenario: 获取全局状态
- **WHEN** CLI 请求 `GET /api/status`
- **THEN** 返回当前项目的 Issue 状态

#### Scenario: 获取所有项目状态
- **WHEN** CLI 请求 `GET /api/status?all=true`
- **THEN** 返回所有项目的 Issue 状态

#### Scenario: 获取单个 Issue 详情
- **WHEN** CLI 请求 `GET /api/issues/:number`
- **THEN** 返回指定 Issue 的详细信息
- **AND** the response includes `prerequisites` and `startEligibility`

#### Scenario: List Issues includes start eligibility
- **WHEN** a client requests `GET /api/issues`
- **THEN** each Issue item includes `prerequisites` and `startEligibility`
- **AND** if the Issue is waiting for prerequisite delivery, `startEligibility.waitingForDelivery` identifies the prerequisite issue numbers

### Requirement: Start handler 校验 issue status

`POST /api/issues/:number/start` SHALL 在执行前校验 issue status，blocked 的 issue 不允许 start。The same start handler SHALL also enforce start eligibility from issue-level start prerequisites before enqueueing work.

#### Scenario: Start blocked issue
- **WHEN** CLI 请求 `POST /api/issues/:number/start`
- **AND** issue status 为 `blocked`
- **THEN** server 返回 400 错误
- **AND** 错误信息包含 "blocked"

#### Scenario: Start active issue in draft stage
- **WHEN** CLI 请求 `POST /api/issues/:number/start`
- **AND** issue status 为 `active` 且 stage 为 `draft`
- **THEN** 正常启动 agent

#### Scenario: Start issue waiting for prerequisite delivery
- **WHEN** a client requests `POST /api/issues/201/start`
- **AND** Issue #201 has prerequisite issue #200
- **AND** Issue #200 is not delivered
- **THEN** server returns a 400-class response
- **AND** the response includes an actionable message equivalent to `Issue #201 is waiting for prerequisite #200 to be delivered.`
- **AND** the response includes structured `startEligibility` data with `waitingForDelivery` identifying Issue #200
- **AND** server SHALL NOT enqueue `start-pipeline`

#### Scenario: Start issue after prerequisites delivered
- **WHEN** a client requests `POST /api/issues/201/start`
- **AND** every prerequisite issue for Issue #201 is delivered
- **AND** Issue #201 otherwise satisfies the existing start checks
- **THEN** server enqueues `start-pipeline`
- **AND** returns the existing accepted start response

### Requirement: API accepts issue start prerequisite declarations

The HTTP API SHALL provide a structured way to declare that an Issue has a prerequisite issue that must be delivered before start. Declaration requests SHALL identify Issues by structured fields rather than requiring body text parsing.

#### Scenario: Declare start prerequisite
- **WHEN** a client declares that Issue #201 requires Issue #200 before start
- **THEN** the API records Issue #200 as a prerequisite issue for Issue #201
- **AND** the response includes updated `prerequisites` and `startEligibility` for Issue #201

#### Scenario: Reject circular start prerequisite declaration
- **WHEN** declaring a start prerequisite would make an Issue directly or indirectly require itself before start
- **THEN** the API returns a 400-class response
- **AND** the response reason is `circular-prerequisite`
- **AND** the rejected prerequisite is not recorded

### Requirement: API represents start eligibility with prerequisite language

The HTTP API SHALL name issue-level prerequisite response fields using `prerequisites`, `startEligibility`, and `waitingForDelivery`. The API SHALL NOT expose this behavior through a legacy dependency-status or blocked-start response model.

#### Scenario: Response describes a waiting issue
- **WHEN** an Issue is waiting for prerequisite issue #200 to be delivered
- **THEN** the API response includes `startEligibility.startable = false`
- **AND** the API response includes `startEligibility.waitingForDelivery` with Issue #200
- **AND** each prerequisite entry indicates whether its prerequisite issue is delivered

### Requirement: REQ-BDA-API-001 Issue APIs expose drift and rebase decision state

Issue list, issue show, and stage-state APIs SHALL expose normalized base drift and rebase opportunity state for active issue candidates.

#### Scenario: Issue response includes drift state

- **WHEN** an issue has evaluated base drift state
- **THEN** issue API responses SHALL include whether it is drifted, the rebase decision, safe-window status, defer reason when applicable, stale evidence flags, base SHA facts when available, and next action guidance

#### Scenario: Stage-state includes drift guidance

- **WHEN** a client reads stage-state for a drifted issue
- **THEN** the response SHALL include enough drift and rebase decision detail to render user guidance without inspecting raw workflow logs

#### Scenario: Conflict diagnostics are durable

- **WHEN** drift-driven `rebase-branch` fails with conflicts or conflict-resolution failure
- **THEN** issue or stage-state responses SHALL expose conflict files, failure reason, and next action guidance from durable projected state

### Requirement: Approve rejects missing or stale verification

Approval-related HTTP APIs SHALL NOT advance Check approval when full verification evidence is missing, failed, disabled, malformed, or stale for the current candidate implementation.

#### Scenario: Approve rejects missing verification evidence

- **WHEN** a user approves a Check-stage issue through the API
- **AND** approval output has no passing full verification evidence
- **THEN** the API SHALL reject approval
- **AND** it SHALL return a clear error instructing the user to rerun Check verification

#### Scenario: Approve rejects stale verification evidence

- **WHEN** a user approves a Check-stage issue through the API
- **AND** verification evidence does not match the current candidate implementation, review snapshot, or merge-ready snapshot
- **THEN** the API SHALL reject approval
- **AND** it SHALL NOT advance the issue to Integrate

### Requirement: Issue API exposes Check verification failures

Issue detail APIs SHALL expose failed or missing Check full verification evidence clearly enough for CLI and Web UI consumers to show why approval is unavailable.

#### Scenario: Issue detail includes failed Check verification

- **WHEN** Check full verification fails
- **THEN** issue detail data SHALL include the failed `health:check` status and output
- **AND** the output SHALL include command, summary, duration, and log excerpt when available

### Requirement: REQ-HTTP-RECOVERY-001 Retry endpoint uses WorkflowRun failed work

`POST /api/issues/:number/retry` SHALL base retry availability on the latest WorkflowRun current-stage retryable failed work instead of requiring `tasks.json` or a checkpoint to exist. The endpoint SHALL return distinguishable errors for no failed WorkflowRun, no retryable failed work, and missing required project/worktree/change artifacts.

#### Scenario: Retry Plan failure before tasks file exists
- **WHEN** the latest WorkflowRun failed in Plan while generating `tasks.json`
- **AND** `tasks.json` does not exist yet
- **THEN** `POST /api/issues/:number/retry` accepts the retry
- **AND** pipeline recovery is queued from the failed Plan work
- **AND** the response does not claim a checkpoint is required

#### Scenario: Retry unavailable reasons are distinct
- **WHEN** `POST /api/issues/:number/retry` cannot proceed
- **THEN** no failed WorkflowRun, no retryable failed work, missing worktree, and missing change artifacts are returned as distinguishable errors
- **AND** each error gives enough guidance for the user to choose retry, rerun, inspect artifacts, or intervene manually

### Requirement: REQ-HTTP-RECOVERY-002 Rerun endpoint restarts current stage

`POST /api/issues/:number/rerun` SHALL apply current-stage rerun semantics rather than retry semantics. The endpoint SHALL clear current-stage checkpoint and recovery state, preserve earlier passed stages, keep the current stage unchanged, and queue execution from the first current-stage work item.

#### Scenario: Rerun failed current stage from first work
- **WHEN** `POST /api/issues/:number/rerun` is called for a failed or blocked issue in a runnable stage
- **THEN** the endpoint clears current-stage checkpoint, failure, approval, blocked reason, and retry count
- **AND** the current stage remains unchanged
- **AND** earlier passed stages are not rerun
- **AND** pipeline recovery is queued from the first work item of the current stage

### Requirement: Epic API Endpoints

Server SHALL expose REST endpoints for Epic creation, listing, detail, membership management, and lifecycle actions.

#### Scenario: Create Epic through API

- **WHEN** a client sends `POST /api/epics` with title, description, and priority
- **THEN** the server creates an active Epic
- **AND** invalid input returns a structured validation error

#### Scenario: List Epics through API

- **WHEN** a client sends `GET /api/epics`
- **THEN** the response includes Epic status, priority, progress, and next issue data for each Epic

#### Scenario: Show Epic through API

- **WHEN** a client sends `GET /api/epics/:id`
- **THEN** the response includes full description, status, priority, linked issues, projected progress, and next issue data

#### Scenario: Add issue through API

- **WHEN** a client sends `POST /api/epics/:id/issues` with an issue id
- **THEN** the server links the issue to the Epic
- **AND** duplicate primary membership returns a structured error that identifies the existing Epic

#### Scenario: Remove issue through API

- **WHEN** a client sends `DELETE /api/epics/:id/issues/:issueId`
- **THEN** the server removes only that membership

#### Scenario: Mark Epic done through API

- **WHEN** a client sends `POST /api/epics/:id/done`
- **THEN** the server changes only the Epic status to `done`

#### Scenario: Close Epic through API

- **WHEN** a client sends `POST /api/epics/:id/close`
- **THEN** the server changes only the Epic status to `closed`

### Requirement: Issue Detail Primary Epic Data

Server SHALL expose a linked issue's primary Epic summary for Issue Detail without adding Epics to issue workflow lists.

#### Scenario: Linked issue detail includes primary Epic

- **WHEN** a client requests detail for an issue linked to an Epic
- **THEN** the response includes the primary Epic id, title, status, and priority

#### Scenario: Unlinked issue detail has no primary Epic

- **WHEN** a client requests detail for an issue without Epic membership
- **THEN** the response clearly indicates no primary Epic is linked

#### Scenario: Board lanes remain issue-only

- **WHEN** a client requests Board lane data or issue workflow lists
- **THEN** Epics are not returned as workflow items

### Requirement: Issue APIs expose attempt-derived recovery projection

Issue detail, stage-state, queue-related, and recovery API responses SHALL expose or use a shared recovery projection derived from the reconciled latest work item attempt. The projection SHALL identify the current work item, latest attempt state, workflow recovery summary state, and allowed actions.

#### Scenario: Issue detail includes recovery projection

- **WHEN** a client requests issue detail for an issue with an active WorkflowRun
- **THEN** the API SHALL reconcile the latest running attempt if needed
- **AND** the response SHALL expose recovery data that includes current work item identity, latest attempt state, workflow summary state, and allowed actions

#### Scenario: Stage-state agrees with issue detail

- **WHEN** a client requests `GET /api/issues/:number/stage-state`
- **THEN** the API SHALL use the same reconciled latest attempt state as issue detail
- **AND** recovery action availability SHALL match issue detail for the same issue

### Requirement: Retry targets only failed latest work attempts

`POST /api/issues/:number/retry` SHALL succeed only when the reconciled latest current-stage work item attempt is `failed`. Interrupted, running, completed, or absent latest attempts SHALL NOT be accepted as failed retry targets.

#### Scenario: Failed task attempt is retryable

- **WHEN** the reconciled latest current-stage task attempt is `failed`
- **THEN** `POST /api/issues/:number/retry` SHALL reset the failed task and downstream dependent work using existing retry behavior
- **AND** the response SHALL indicate retry was accepted

#### Scenario: Failed check attempt is retryable

- **WHEN** the reconciled latest current-stage check attempt is `failed`
- **THEN** `POST /api/issues/:number/retry` SHALL reset the failed check and downstream dependent work using existing check retry behavior
- **AND** the response SHALL indicate retry was accepted

#### Scenario: Interrupted attempt rejects retry with guidance

- **WHEN** the reconciled latest work item attempt is `interrupted`
- **THEN** `POST /api/issues/:number/retry` SHALL return a 409-style conflict
- **AND** the response SHALL explain that interrupted work is not failed work
- **AND** the response SHALL suggest resume, rerun stage, or inspect actions when available

#### Scenario: Stale running attempt reconciles before retry decision

- **WHEN** the latest attempt is stored as `running` but has no live execution evidence
- **AND** a client requests retry
- **THEN** the API SHALL reconcile the attempt before checking retry eligibility
- **AND** retry SHALL be rejected or accepted according to the reconciled attempt state rather than raw WorkflowRun status

### Requirement: Rerun and resume use interrupted recovery semantics

Recovery APIs SHALL keep rerun and resume distinct from retry. Rerun stage SHALL create fresh attempts for stage work. Resume for interrupted work SHALL not pretend the interrupted attempt failed.

#### Scenario: Rerun stage creates fresh attempts

- **WHEN** a client requests rerun for the current stage after interruption
- **THEN** the API SHALL restart the stage work from the appropriate stage boundary
- **AND** new attempts SHALL be created as work items execute

#### Scenario: Resume interrupted work does not require failed run

- **WHEN** a client requests resume for an interrupted latest attempt
- **THEN** the API SHALL use interrupted recovery semantics
- **AND** it SHALL NOT require `WorkflowRun.status = failed` as if resume were retry

