## MODIFIED Requirements

### Requirement: CLI 是 thin client

CLI SHALL NOT 包含业务逻辑，所有逻辑在 server 侧。For issue-level start prerequisites, the CLI SHALL render server-provided `prerequisites`, `startEligibility`, and `waitingForDelivery` data and SHALL NOT compute start eligibility by parsing issue body text.

#### Scenario: CLI 调用 server API
- **WHEN** 用户执行 `mo issue list`
- **THEN** CLI 调用 `GET /api/issues`
- **AND** CLI 格式化输出 server 返回的数据
- **AND** CLI 不做任何业务决策

#### Scenario: CLI 不存储状态
- **WHEN** CLI 执行任何命令
- **THEN** CLI 不在本地存储任何业务状态
- **AND** 所有状态由 server 管理

#### Scenario: CLI renders waiting for delivery from API data
- **WHEN** `mo issue list` or `mo issue show <number>` receives an Issue whose `startEligibility.waitingForDelivery` contains Issue #200
- **THEN** the CLI output includes a concise waiting reason equivalent to `Waiting for #200`
- **AND** the CLI does not parse the Issue body to infer that reason

### Requirement: CLI 支持本地 Issue CRUD

CLI SHALL 通过 Server API 支持本地 Issue 的创建、读取、更新、删除操作。The CLI SHALL also expose a server-backed command or option to declare an issue-level start prerequisite between two Issues.

#### Scenario: CLI 调用 Server API 创建 Issue
- **WHEN** 用户执行 `mo issue create "title"`
- **THEN** CLI 发送 POST /api/issues 请求到 Server
- **AND** Server 在本地 SQLite 创建 Issue
- **AND** CLI 显示创建结果

#### Scenario: CLI 调用 Server API 列出 Issues
- **WHEN** 用户执行 `mo issue list`
- **THEN** CLI 发送 GET /api/issues 请求到 Server
- **AND** Server 从本地 SQLite 查询 Issues
- **AND** CLI 格式化显示结果

#### Scenario: CLI 调用 Server API 更新 Issue
- **WHEN** 用户执行 `mo issue update <id> --title "new"`
- **THEN** CLI 发送 PATCH /api/issues/:id 请求到 Server
- **AND** Server 更新本地 SQLite
- **AND** CLI 显示更新结果

#### Scenario: CLI 调用 Server API 添加评论
- **WHEN** 用户执行 `mo issue comment <id> "text"`
- **THEN** CLI 发送 POST /api/issues/:id/comments 请求到 Server
- **AND** Server 在本地 SQLite 创建 comment
- **AND** CLI 显示成功消息

#### Scenario: CLI declares start prerequisite
- **WHEN** 用户 declares that Issue #201 requires Issue #200 to be delivered before start through the CLI
- **THEN** CLI sends a structured request to the Server API
- **AND** CLI displays that Issue #201 now has prerequisite Issue #200

#### Scenario: CLI surfaces circular declaration rejection
- **WHEN** the Server API rejects a CLI start prerequisite declaration with reason `circular-prerequisite`
- **THEN** CLI prints a clear error explaining that the prerequisite would make the Issue require itself before start
- **AND** CLI exits with a non-zero status

### Requirement: Issue create success output guides the next step only for startable issues

Successful `mo issue create` output SHALL print the created issue number and priority, and SHALL show the `mo issue start` hint only when the created issue is startable according to server-provided start eligibility.

#### Scenario: Start tip shown for backlog issue
- **WHEN** `mo issue create` returns an issue still in a startable draft or backlog state
- **AND** `startEligibility.startable` is not false because of waiting prerequisite delivery
- **THEN** the CLI prints `Created issue #N: <title>`
- **AND** prints the issue priority
- **AND** prints `Tip: Run 'mo issue start <number>' to begin processing`

#### Scenario: Start tip omitted for non-startable issue
- **WHEN** `mo issue create` returns an issue already outside the initial startable state
- **THEN** the CLI prints `Created issue #N: <title>`
- **AND** prints the issue priority
- **AND** does not print the start tip

#### Scenario: Start tip omitted while waiting for delivery
- **WHEN** `mo issue create` or a later CLI display receives an Issue whose start eligibility is waiting for prerequisite delivery
- **THEN** the CLI does not tell the user to start that Issue now
- **AND** the CLI prints a waiting reason equivalent to `Waiting for #N`

## ADDED Requirements

### Requirement: CLI start uses server start eligibility rejection

`mo issue start <number>` SHALL use the Server API start endpoint as the source of truth for start eligibility. When the server rejects start because a prerequisite issue is waiting for delivery, the CLI SHALL surface that message without starting any local workflow behavior.

#### Scenario: Start command rejected while waiting for delivery
- **WHEN** the user runs `mo issue start 201`
- **AND** the Server API returns that Issue #201 is waiting for prerequisite Issue #200 to be delivered
- **THEN** CLI prints the server-provided actionable message
- **AND** CLI exits with a non-zero status
- **AND** CLI does not make any additional request intended to enqueue or resume pipeline work

### Requirement: CLI issue detail shows start prerequisites

`mo issue show <number>` SHALL display issue-level start prerequisites and whether each prerequisite issue has been delivered when the API response includes prerequisite data.

#### Scenario: Show prerequisite delivery states
- **WHEN** the user runs `mo issue show 201`
- **AND** Issue #201 has prerequisite issues #200 and #199
- **THEN** CLI displays the prerequisite issues
- **AND** CLI indicates which prerequisite issues are delivered and which are still waiting for delivery
