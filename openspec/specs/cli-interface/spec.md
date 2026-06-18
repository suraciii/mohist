# OpenSpec Capability: cli-interface

### Requirement: CLI 提供分组式命令界面

CLI SHALL 提供分组式命令界面，与 server 通信。

#### Scenario: 查看 help
- **WHEN** 用户执行 `mo --help`
- **THEN** 显示所有命令组和用法

#### Scenario: 查看子命令 help
- **WHEN** 用户执行 `mo issue --help`
- **THEN** 显示 issue 命令组的所有子命令

### Requirement: CLI 检测 server 状态

CLI SHALL 在执行命令前检测 server 是否运行。所有需要 server 的 CLI 命令 SHALL 在执行前检查 server 是否可用。server 不可用时 SHALL 打印友好错误信息并退出，而非抛出 ECONNREFUSED。

#### Scenario: Server 未运行
- **WHEN** 用户执行需要 server 的命令
- **AND** server 未运行
- **THEN** CLI 输出 "Server is not running. Start with: mo server start" 并以非零 exit code 退出
- **AND** 不输出 Node.js 的 ECONNREFUSED 堆栈信息

#### Scenario: Server 运行中
- **WHEN** 用户执行需要 server 的命令
- **AND** server 运行中
- **THEN** CLI 正常调用 server API

### Requirement: CLI 提供美化的输出

CLI SHALL 提供清晰、美化的终端输出。

#### Scenario: status 命令输出
- **WHEN** 用户执行 `mo status`
- **THEN** 显示格式化的状态表格
- **AND** 显示当前项目名称
- **AND** 使用颜色区分不同状态

#### Scenario: 错误信息友好
- **WHEN** 命令执行失败
- **THEN** 显示清晰的错误信息
- **AND** 提供可能的解决方案

### Requirement: Server API 扩展

Server SHALL 新增以下 API 端点支持本地 Issue CRUD。

#### Scenario: POST /api/issues
- **WHEN** Server 收到 POST /api/issues 请求
- **WITH** body: { title, body?, labels? }
- **THEN** Server 在当前项目创建 Issue
- **AND** 返回 Issue 详情

#### Scenario: PATCH /api/issues/:id
- **WHEN** Server 收到 PATCH /api/issues/:id 请求
- **WITH** body: { title?, body?, labels? }
- **THEN** Server 更新指定 Issue
- **AND** 返回更新后的 Issue

#### Scenario: POST /api/issues/:id/comments
- **WHEN** Server 收到 POST /api/issues/:id/comments 请求
- **WITH** body: { body }
- **THEN** Server 创建 comment
- **AND** 返回 comment 详情

#### Scenario: GET /api/labels
- **WHEN** Server 收到 GET /api/labels 请求
- **THEN** Server 返回当前项目所有使用过的 labels
- **AND** 按名称排序

### Requirement: CLI 支持 server 命令

CLI SHALL 支持 server 管理命令（无需 server 运行）。The `mo update` command SHALL be usable without the server running, and SHALL NOT require server availability to start.

#### Scenario: 启动 server
- **WHEN** 用户执行 `mo server start`
- **THEN** CLI 启动 server 进程
- **AND** CLI 等待 server 就绪
- **AND** CLI 显示 "Server started"

#### Scenario: 停止 server
- **WHEN** 用户执行 `mo server stop`
- **THEN** CLI 发送停止信号给 server
- **AND** CLI 显示 "Server stopped"

#### Scenario: mo update runs without server
- **WHEN** 用户执行 `mo update`
- **AND** server 未运行
- **THEN** CLI SHALL proceed with CLI and managed asset updates
- **AND** SHALL skip server and runner update stages with a clear message
- **AND** SHALL NOT fail with "Server is not running"

### Requirement: CLI removes dead workflow commands

The CLI SHALL NOT expose `issue approve`, `issue pause`, or `issue resume` commands. The `issue show` command SHALL NOT display `progress` or `stageInfo` (these fields are removed from the API response). The `formatStage()` function SHALL NOT map `waiting-design-review` or `waiting-review`.

#### Scenario: Dead commands not available
- **WHEN** user executes `mo issue --help`
- **THEN** the output SHALL NOT list `approve`, `pause`, or `resume` subcommands

#### Scenario: Dead command attempted
- **WHEN** user executes `mo issue approve`, `mo issue pause`, or `mo issue resume`
- **THEN** CLI SHALL display an unknown command error

#### Scenario: Issue show omits progress display
- **WHEN** user executes `mo issue show <number>`
- **THEN** the output SHALL NOT display progress bar or stage info block

### Requirement: Server status CLI omits task/worker info

The `mo server status` command SHALL NOT display `Workers`, `Running tasks`, or `Queued tasks` lines. The `fetchServerStatus()` function SHALL NOT reference removed status fields.

#### Scenario: Server status display
- **WHEN** user executes `mo server status` and server is running
- **THEN** the output SHALL NOT include lines about workers, running tasks, or queued tasks

### Requirement: CLI 共享 apiClient 实现

CLI 命令模块 SHALL 共享同一个 `apiClient` 实现，不各自定义重复版本。公共模块位于 `cli/api-client.ts`。

#### Scenario: 所有命令模块使用共享 apiClient
- **WHEN** 检查 `cli/commands/issue.ts`、`cli/commands/quick.ts`、`cli/commands/project.ts`
- **THEN** 均从 `../api-client` 导入 `apiClient` 函数
- **AND** 无文件内定义本地的 `apiClient` 函数
- **AND** 无文件内定义本地的 `API_BASE` 常量

#### Scenario: apiClient 行为不变
- **WHEN** CLI 通过共享 `apiClient` 调用 server API
- **THEN** 行为与重构前完全一致（HTTP 请求、JSON 解析、错误处理）

### Requirement: mo attach 命令连接 SSE 端点

`mo attach` SHALL 连接到 mohist server 的 SSE 端点 `/api/events`，订阅事件流，将事件格式化输出到终端。

#### Scenario: 基本监控
- **WHEN** 用户运行 `mo attach`
- **THEN** 连接到 server SSE 端点
- **AND** 实时显示所有 agent 相关事件
- **AND** 每个事件包含时间戳、事件类型、issue 编号和相关数据

支持的事件类型（7 种）：
- `agent_started` - agent 开始执行
- `agent_completed` - agent 正常完成
- `agent_paused` - agent 暂停等待用户输入
- `agent_error` - agent 执行出错
- `stage_changed` - 阶段变更
- `comment_added` - 添加评论
- `approval_requested` - 请求审批

#### Scenario: 项目过滤
- **WHEN** 用户运行 `mo attach --project myapp`
- **THEN** 查询 `/api/projects` 解析 myapp 为 project ID
- **AND** 连接到 SSE 端点并添加 `?projectId=<id>` 参数
- **AND** 只显示该项目的 agent 事件

#### Scenario: 使用当前项目
- **WHEN** 用户运行 `mo attach`（不带 --project）
- **AND** 当前目录在 mohist 项目中
- **THEN** 使用当前项目的 projectId 过滤事件
- **AND** 如果不在项目目录中，显示所有项目的事件

#### Scenario: 自动重连
- **WHEN** 用户运行 `mo attach --follow`
- **AND** SSE 连接断开
- **THEN** 打印 "Reconnecting..." 提示
- **AND** 等待 2 秒后自动重连
- **AND** 继续接收新事件（可能收到重复事件）

**注意**: 不使用 Last-Event-ID 断点续传。重连时从最新事件开始接收。

#### Scenario: server 未运行
- **WHEN** 用户运行 `mo attach`
- **AND** mohist server 未运行
- **THEN** 显示错误信息 "Error: Server is not running"
- **AND** 显示提示 "Start the server with: mo server start"
- **AND** 退出状态码 1

#### Scenario: 优雅退出
- **WHEN** 用户按 Ctrl+C 或进程收到 SIGTERM
- **THEN** 关闭 SSE 连接
- **AND** 打印 "Detached."
- **AND** 正常退出（状态码 0）

#### Scenario: 未知事件类型
- **WHEN** 收到未定义的事件类型
- **THEN** 打印原始事件数据（event type + data）
- **AND** 继续处理后续事件

### Requirement: 后端事件订阅修复

后端 SHALL 将 `agent_paused` 添加到 SSE 事件订阅列表，使 pause 事件能够发送到客户端。

#### Scenario: agent_paused 事件可见
- **WHEN** agent 执行到暂停点
- **THEN** `agent_paused` 事件通过 SSE 发送到所有连接的客户端
- **AND** 客户端显示 `|| agent paused` 消息

### Requirement: CLI 阶段名与实现一致

CLI 输出中的阶段名 SHALL 使用当前 canonical pipeline stage model。

#### Scenario: issue list 显示正确阶段名
- **WHEN** 用户运行 `mo issue list`
- **THEN** 阶段列显示 `backlog`/`plan`/`build`/`check`/`integrate`/`done`
- **AND** it does not show deprecated `draft`, `designing`, or `implementing` stage names

### Requirement: CLI 提供 provider 管理命令 [NEW]

CLI SHALL 提供 `mo providers` 命令组用于管理 LLM provider 配置，不需要 server 运行。

#### Scenario: 列出所有 provider
- **WHEN** 用户执行 `mo providers list`
- **THEN** CLI 显示所有内置 provider 的状态表格（ID、名称、配置状态、掩码后的 API Key、baseURL）
- **AND** 命令在 server 未运行时也能正常工作

#### Scenario: 配置 provider API Key
- **WHEN** 用户执行 `mo providers login <providerID>`
- **THEN** CLI 交互式提示输入 API Key（输入隐藏）
- **AND** 保存到 `~/.mohist/config.jsonc`
- **AND** 显示确认信息

#### Scenario: 删除 provider 配置
- **WHEN** 用户执行 `mo providers logout <providerID>`
- **THEN** CLI 从 config.jsonc 删除该 provider 的 apiKey
- **AND** 显示确认信息

### Requirement: REQ-CLI-001 CLI shows simplified current session state

CLI issue/status output SHALL render the same simplified current opencode session call states as Web UI.

#### Scenario: CLI shows running
- **WHEN** an issue has a current session with status `running`
- **THEN** CLI output SHALL show `Running`

#### Scenario: CLI shows checking session
- **WHEN** an issue has a current session with status `probing`
- **THEN** CLI output SHALL show `Checking session`
- **AND** it SHOULD include probe timing when available

#### Scenario: CLI shows session failed
- **WHEN** an issue has a current session with status `failed`
- **THEN** CLI output SHALL show `Session failed`
- **AND** it SHOULD include `failureReason` when available

#### Scenario: CLI shows no active session
- **WHEN** an issue has no current active session call
- **THEN** CLI output SHALL show `No active session` where current session state is displayed

### Requirement: issue-recovery-verbs-are-user-intent-based

The CLI SHALL present issue recovery verbs according to user intent. `reopen` SHALL mean reopening a closed issue, `resume` SHALL mean continuing paused or interrupted work, and recovery help text SHALL not mention restart.

#### Scenario: Reopen command is closed-only

- **WHEN** the user runs `mo issue reopen <number>`
- **THEN** the CLI calls the reopen API
- **AND** the command semantics are described as reopening a closed issue

#### Scenario: Resume command targets paused or interrupted work

- **WHEN** the user runs `mo issue resume <number>`
- **THEN** the CLI calls the resume API
- **AND** success output describes the issue as resumed rather than reopened

#### Scenario: Failed recovery guidance omits restart

- **WHEN** a failed or needs-action recovery command returns an error
- **THEN** the CLI guidance references retry, rerun, or rewind as appropriate
- **AND** the CLI does not recommend restart

#### Scenario: Closed guidance uses reopen only

- **WHEN** a closed issue blocks further progress in CLI output
- **THEN** the guidance recommends `mo issue reopen <number>`
- **AND** it does not recommend resume, retry, or restart for that closed-only case

### Requirement: Issue CLI accepts long body input without shell-sensitive escaping

`mo issue create` and `mo issue update` SHALL accept issue body input as a literal string, as a curl-style `@file` reference, and as `-` to read the full body from stdin before sending the request to the API.

#### Scenario: Create issue from body file reference
- **WHEN** the user runs `mo issue create "Title" --body @body.md`
- **THEN** the CLI reads `body.md` as UTF-8 text
- **AND** sends the file contents as the issue body

#### Scenario: Create issue from explicit body file option
- **WHEN** the user runs `mo issue create "Title" --body-file body.md`
- **THEN** the CLI reads `body.md` as UTF-8 text
- **AND** sends the file contents as the issue body

#### Scenario: Create issue from stdin
- **WHEN** the user pipes content into `mo issue create "Title" --body -`
- **THEN** the CLI reads the full stdin stream
- **AND** sends the streamed text as the issue body

#### Scenario: Update issue from body file reference
- **WHEN** the user runs `mo issue update 42 --body @body.md`
- **THEN** the CLI reads `body.md` as UTF-8 text
- **AND** sends the file contents as the updated issue body

#### Scenario: Update issue from stdin
- **WHEN** the user pipes content into `mo issue update 42 --body -`
- **THEN** the CLI reads the full stdin stream
- **AND** sends the streamed text as the updated issue body

#### Scenario: Preserve literal body behavior
- **WHEN** the user runs `mo issue create "Title" --body "literal markdown body"`
- **THEN** the CLI sends the provided string unchanged as the issue body

### Requirement: Issue CLI normalizes touched priority inputs and fails invalid inputs with exit code 1

The touched issue CLI flows SHALL normalize priority inputs case-insensitively for create, update, and list, and SHALL terminate with exit code `1` when touched argument validation or body-ingestion fails.

#### Scenario: Create accepts uppercase priority
- **WHEN** the user runs `mo issue create "Title" -p P2`
- **THEN** the CLI accepts the value
- **AND** sends normalized priority `p2`

#### Scenario: Update accepts uppercase priority
- **WHEN** the user runs `mo issue update 42 -p P0`
- **THEN** the CLI accepts the value
- **AND** sends normalized priority `p0`

#### Scenario: List accepts uppercase priority filter
- **WHEN** the user runs `mo issue list -p P1`
- **THEN** the CLI accepts the value
- **AND** applies the same filter as `-p p1`

#### Scenario: Invalid priority fails non-zero
- **WHEN** the user runs `mo issue create "Title" -p urgent`
- **THEN** the CLI prints a clear validation error
- **AND** exits with code `1`

#### Scenario: Missing body file fails non-zero
- **WHEN** the user runs `mo issue create "Title" --body @missing.md`
- **THEN** the CLI prints a clear file-read error
- **AND** exits with code `1`

#### Scenario: Conflicting body sources fail non-zero
- **WHEN** the user runs `mo issue create "Title" --body @a.md --body-file b.md`
- **THEN** the CLI prints a clear validation error about conflicting body sources
- **AND** exits with code `1`

### Requirement: REQ-CLI-198-001 CLI issue create supports model on initial creation

`mo issue create` SHALL accept `--model <provider/model>` and send that value in the initial `POST /api/issues` request alongside title, body, labels, and priority when provided.

#### Scenario: Create issue with model
- **WHEN** the user runs `mo issue create "Fix login bug" --model anthropic/claude-sonnet`
- **THEN** the CLI sends `model: "anthropic/claude-sonnet"` in the create request body
- **AND** the created issue is shown as created successfully

#### Scenario: Create issue with body source and model
- **WHEN** the user runs `mo issue create "Fix login bug" --body @body.md --model anthropic/claude-sonnet`
- **THEN** the CLI resolves the body source before sending the request
- **AND** the same create request includes the resolved body text and `model`

#### Scenario: Invalid model format from create path
- **WHEN** the user runs `mo issue create "Fix login bug" --model invalid-model`
- **THEN** the CLI surfaces the API error clearly
- **AND** exits with status code 1

### Requirement: CLI provides shared agent skill management

The CLI SHALL provide local commands that install Mohist-provided coder skill discovery stubs and read version-matched built-in skill content without requiring the Mohist server.

#### Scenario: Install shared agent skill stubs

- **WHEN** the user runs `mo skills install`
- **THEN** the CLI writes `.agents/skills/mohist/SKILL.md`
- **AND** the CLI writes `.agents/skills/mohist-explore/SKILL.md`
- **AND** each installed file is a lightweight discovery stub rather than the full packaged guidance
- **AND** each installed `SKILL.md` includes `name`, `description`, and `hidden: true` frontmatter

#### Scenario: Install to explicit path

- **WHEN** the user runs `mo skills install --path <repo>`
- **THEN** the CLI writes shared skill stubs under `<repo>/.agents/skills`
- **AND** the CLI does not write shared skill stubs under the current working directory unless it is the selected path

#### Scenario: Existing user-authored skills remain untouched

- **WHEN** the user runs `mo skills install`
- **THEN** the CLI manages only the Mohist-provided built-in skill names
- **AND** does not create, overwrite, delete, or scan unrelated user-authored skill directories such as `.agents/skills/mohist-po/`

#### Scenario: Internal Mohist skills are untouched

- **WHEN** the user runs `mo skills install`, `mo skills list`, `mo skills get`, or `mo skills path`
- **THEN** the CLI does not create, update, delete, or scan `.mohist/skills`
- **AND** `SkillService` behavior is unchanged

### Requirement: CLI serves packaged built-in skill content

The CLI SHALL resolve Mohist-provided built-in skill content from packaged skill assets so `mo skills` always serves content that matches the running CLI version.

#### Scenario: List visible built-in skills

- **WHEN** the user runs `mo skills list`
- **THEN** the CLI lists non-hidden built-in Mohist skills sorted by name
- **AND** hidden discovery stubs are not shown as duplicate list entries

#### Scenario: List visible built-in skills as JSON

- **WHEN** the user runs `mo skills list --json`
- **THEN** the CLI returns JSON entries for the visible built-in skills
- **AND** each entry includes the skill name and description

#### Scenario: Get built-in skill content

- **WHEN** the user runs `mo skills get mohist`
- **THEN** the CLI prints the packaged full `mohist` skill guidance
- **AND** the output matches the current built-in skill-data content rather than any repository-installed stub

#### Scenario: Get built-in skill content with supplementary files

- **WHEN** the user runs `mo skills get mohist --full`
- **THEN** the CLI prints the packaged full `mohist` skill guidance
- **AND** appends supplementary files from packaged `references/` and `templates/` directories in deterministic sorted order

#### Scenario: Get all built-in skills

- **WHEN** the user runs `mo skills get --all`
- **THEN** the CLI returns the visible built-in Mohist skill set backed by packaged full content

#### Scenario: Resolve built-in skill path

- **WHEN** the user runs `mo skills path mohist`
- **THEN** the CLI prints the packaged directory path for the built-in `mohist` skill data

#### Scenario: Override packaged skill root for development and tests

- **WHEN** the environment sets `MOHIST_SKILLS_DIR` to a valid built-in skill asset root
- **THEN** `mo skills list`, `get`, and `path` resolve skills from that override path instead of the default packaged lookup

### Requirement: REQ-CLI-ISSUE-LIST-TRIAGE-FILTERS Issue list supports triage-oriented scope filters

The CLI and issue list API SHALL support triage-oriented issue scopes for active pipeline work, multiple stages, and attention items without adding workflow stages or identity ownership concepts.

#### Scenario: Active alias lists pipeline work only
- **WHEN** the user runs `mo issue list -s active`
- **THEN** the command SHALL list issues in `plan`, `build`, `check`, or `integrate` that are not closed or completed
- **AND** it SHALL NOT list backlog issues solely because their status is `active`

#### Scenario: Multi-stage filter uses OR semantics
- **WHEN** the user runs `mo issue list -s build,check`
- **THEN** the command SHALL list issues whose stage is `build` or `check`

#### Scenario: Invalid stage fails clearly
- **WHEN** the user runs `mo issue list -s unknown`
- **THEN** the command SHALL print a clear invalid stage or alias error
- **AND** it SHALL exit with a non-zero status
- **AND** it SHALL NOT silently return an empty list

#### Scenario: Stage filters compose with existing filters
- **WHEN** the user combines stage selection with priority, label, archived, or all filters
- **THEN** stage selection SHALL be applied as OR within the stage set
- **AND** all other filters SHALL be applied with AND semantics

#### Scenario: Attention filter lists user-decision items
- **WHEN** the user runs `mo issue list --attention`
- **THEN** the command SHALL list issues awaiting approval, blocked, interrupted, delivery blocked, integrate failed, or done/completed but not merged
- **AND** it SHALL NOT include normal running or probing issues unless another attention condition is present

#### Scenario: Attention filter composes and has explicit empty state
- **WHEN** the user combines `--attention` with stage, priority, or label filters
- **THEN** the command SHALL apply `--attention` with AND semantics against those filters
- **AND** when no issues match, the command SHALL display a clear attention-specific empty state

#### Scenario: No personal ownership shortcut is added
- **WHEN** the user views `mo issue list --help`
- **THEN** the help SHALL document `--attention`, comma-separated status values, and the `active` alias
- **AND** it SHALL NOT document or expose `--my`

### Requirement: REQ-CLI-ISSUE-SHOW-COMPACT Issue show supports compact output

The CLI SHALL provide a compact issue show mode for quick human-readable status checks while preserving the default full issue detail output.

#### Scenario: Compact show emits one-line summary
- **WHEN** the user runs `mo issue show <id> --compact`
- **THEN** the command SHALL print a single-line summary containing issue number, stage, status, priority, and title
- **AND** the output SHALL be human-readable text, not JSON

#### Scenario: Compact show omits long sections
- **WHEN** the user runs `mo issue show <id> --compact`
- **THEN** the command SHALL NOT output body, comments, stage checks, approval output, session details, or other long detail sections

#### Scenario: Default show remains full detail
- **WHEN** the user runs `mo issue show <id>` without `--compact`
- **THEN** the command SHALL preserve the existing full detail output behavior

### Requirement: REQ-CLI-ISSUE-DIFF-STAT Issue diff supports stat output

The CLI SHALL provide a diff stat mode that reports file-level change scale without printing the full patch and uses the same comparison semantics as full issue diff.

#### Scenario: Diff stat omits patch content
- **WHEN** the user runs `mo issue diff <id> --stat`
- **THEN** the command SHALL print file-level changed-file, addition, and deletion information
- **AND** it SHALL NOT print full patch hunks or `diff --git` patch blocks

#### Scenario: Default diff remains full patch
- **WHEN** the user runs `mo issue diff <id>` without `--stat`
- **THEN** the command SHALL preserve full patch output behavior

#### Scenario: Diff stat shares comparison semantics
- **WHEN** the user compares `mo issue diff <id>` and `mo issue diff <id> --stat`
- **THEN** both commands SHALL use the same base branch, issue branch, and merge-base comparison semantics

#### Scenario: Diff unavailable states are distinct
- **WHEN** issue diff data is unavailable because the issue has not started, the worktree is removed, a branch is missing, or git comparison fails
- **THEN** `mo issue diff <id> --stat` SHALL print clear feedback that distinguishes the reason
- **AND** it SHALL exit with a non-zero status

#### Scenario: No-change diff is explicit
- **WHEN** issue diff data is available but contains no changed files
- **THEN** `mo issue diff <id> --stat` SHALL print a clear no-changes message
- **AND** it SHALL NOT print patch content

### Requirement: CLI 是 thin client

CLI SHALL NOT 包含业务逻辑，所有逻辑在 server 侧。For issue-level start readiness, the CLI SHALL render server-provided `prerequisites`, `isDraft`, `canStart`, and `blocker` data and SHALL NOT compute start readiness by parsing issue body text.

#### Scenario: CLI 调用 server API
- **WHEN** 用户执行 `mo issue list`
- **THEN** CLI 调用 `GET /api/issues`
- **AND** CLI 格式化输出 server 返回的数据
- **AND** CLI 不做任何业务决策

#### Scenario: CLI 不存储状态
- **WHEN** CLI 执行任何命令
- **THEN** CLI 不在本地存储任何业务状态
- **AND** 所有状态由 server 管理

#### Scenario: CLI renders start readiness from API data
- **WHEN** `mo issue list` or `mo issue show <number>` receives an Issue whose `blocker` is `WaitingFor(Issue)` identifying Issue #200
- **THEN** the CLI output includes a concise waiting reason equivalent to `Waiting for #200`
- **AND** the CLI does not parse the Issue body to infer that reason

#### Scenario: CLI renders draft state from API data
- **WHEN** `mo issue list` or `mo issue show <number>` receives an Issue whose `isDraft = true`
- **THEN** the CLI output indicates the Issue is a draft
- **AND** the CLI does not parse the Issue body or labels to infer draft state

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
- **WHEN** `mo issue create` returns an issue still in a startable backlog state
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

### Requirement: REQ-BDA-CLI-001 CLI displays base drift and rebase decisions

The CLI SHALL render base drift and rebase opportunity state from server API responses without re-deriving drift policy locally.

#### Scenario: Issue show displays drift state

- **WHEN** the user runs `mo issue show <number>` for a drifted active issue
- **THEN** the output SHALL show that the issue is behind base
- **AND** it SHALL show the rebase decision and next action

#### Scenario: Deferred rebase explains why

- **WHEN** an issue has a deferred rebase opportunity
- **THEN** CLI output SHALL show the defer reason such as running agent work or waiting for a task boundary

#### Scenario: Stale approval is not presented as actionable

- **WHEN** a Check issue has stale approval evidence due to base drift
- **THEN** CLI output SHALL NOT present the approval as currently actionable
- **AND** it SHALL guide the user to rebase or rerun Check

#### Scenario: Rebase conflict details are visible

- **WHEN** a rebase opportunity or task has conflict diagnostics
- **THEN** CLI output SHALL show conflict files, failure reason, and next action guidance

### Requirement: Issue show exposes Check verification approval blockers

`mo issue show <number>` SHALL surface failed or missing Check full verification evidence when it blocks Check approval.

#### Scenario: Failed Check verification appears in issue show

- **WHEN** Check full verification fails for an issue
- **THEN** `mo issue show <number>` SHALL show the failed Check verification gate
- **AND** it SHALL include command, summary, duration, and log excerpt when available

#### Scenario: Missing Check verification explains unavailable approval

- **WHEN** Check approval is unavailable because full verification evidence is missing
- **THEN** `mo issue show <number>` SHALL show that approval is blocked by missing Check verification evidence

### Requirement: REQ-CLI-RECOVERY-001 Recovery copy uses retry rerun rewind vocabulary

CLI-facing workflow recovery messages SHALL use the recovery vocabulary `retry`, `rerun`, and `rewind`. Workflow recovery copy SHALL NOT reintroduce `restart` as a recovery action; the only allowed `restart` usage is unrelated server restart commands or the removed restart endpoint explaining that restart is unavailable.

#### Scenario: Retry and rerun guidance uses approved terms
- **WHEN** a workflow recovery command or endpoint fails and the CLI displays the error
- **THEN** the guidance uses `retry`, `rerun`, or `rewind` as appropriate
- **AND** it does not tell the user to restart the workflow or pipeline

#### Scenario: Approval rejection copy avoids restart terminology
- **WHEN** an issue approval is rejected and CLI output describes the follow-up behavior
- **THEN** the message does not say the pipeline will restart
- **AND** it uses current recovery vocabulary or neutral state-transition wording


### Requirement: Epic CLI Commands

CLI SHALL provide a `mo epic` top-level command group — peer to `mo issue` and `mo project` — that wires eight subcommands to the existing project-scoped Epic HTTP endpoints (`EpicRoutes.cs`). The CLI SHALL NOT modify Epic domain state directly; it SHALL only consume the existing HTTP API. All `mo epic` subcommands SHALL accept `--project <name>` / `--project-id <id>` to override the current active project (same resolution mechanism as `mo issue`), and `-o table|json` for output selection (table shape via `MohistCliApi.TableShape`). Epic and Issue share the project-local numbering namespace but are distinct entities; the `mo epic` group SHALL access Epic entities only and SHALL NOT silently fall through to Issue data.

#### Scenario: List Epics

- **WHEN** a user runs `mo epic list`
- **THEN** the CLI calls `GET /api/projects/{projectRef}/epics` for the resolved project
- **AND** in `table` mode prints Epic number, title, status, and priority for each Epic
- **AND** in `json` mode returns the complete API response fields unchanged
- **AND** an empty project prints a clear empty state rather than an error

#### Scenario: Create Epic

- **WHEN** a user runs `mo epic create <title> [--description <text>] [--priority <p0|p1|p2|p3>]`
- **THEN** the CLI sends `POST /api/projects/{projectRef}/epics` with title, optional description, and optional priority
- **AND** prints the newly created Epic identifier on success

#### Scenario: Create Epic missing title fails clearly

- **WHEN** a user runs `mo epic create` without a title argument
- **THEN** the CLI prints a clear validation error
- **AND** exits with a non-zero status without calling the API

#### Scenario: Show Epic by number or id

- **WHEN** a user runs `mo epic show <id|num>`
- **THEN** the CLI calls `GET /api/projects/{projectRef}/epics/{id}` passing the argument verbatim to the API's dual-track resolver
- **AND** prints Epic description, status, priority, projected progress, next issue, and the linked issue list

#### Scenario: Epic show is namespace-isolated from issue show

- **WHEN** a user runs `mo epic show 8`
- **THEN** the CLI returns Epic #8 (for example, the Labels Epic)
- **AND** it SHALL NOT return Issue #8 (a workflow task) even though both share the project-local number 8

#### Scenario: Update Epic fields

- **WHEN** a user runs `mo epic update <id|num> [--title <text>] [--description <text>] [--priority <p0|p1|p2|p3>]`
- **THEN** the CLI sends `PATCH /api/projects/{projectRef}/epics/{id}` with only the supplied optional fields
- **AND** prints the updated Epic on success

#### Scenario: Link an issue into an Epic

- **WHEN** a user runs `mo epic link <epic-id|num> <issue-id|num>`
- **THEN** the CLI sends `POST /api/projects/{projectRef}/epics/{id}/issues` with the issue reference
- **AND** prints a clear success confirmation identifying both the Epic and the linked issue

#### Scenario: Link surfaces duplicate membership conflict

- **WHEN** a user runs `mo epic link` for an issue that already belongs to another Epic
- **AND** the API returns a `DUPLICATE_EPIC_MEMBERSHIP` conflict
- **THEN** the CLI surfaces the conflict clearly, identifying the existing Epic
- **AND** the CLI SHALL NOT report silent success
- **AND** the CLI exits with a non-zero status

#### Scenario: Unlink an issue from an Epic

- **WHEN** a user runs `mo epic unlink <epic-id|num> <issue-id>`
- **THEN** the CLI sends `DELETE /api/projects/{projectRef}/epics/{id}/issues/{issueId}`
- **AND** prints a clear success confirmation

#### Scenario: Mark Epic done when all issues delivered

- **WHEN** a user runs `mo epic done <id|num>`
- **AND** every linked issue is delivered
- **THEN** the CLI sends `POST /api/projects/{projectRef}/epics/{id}/done`
- **AND** prints confirmation that the Epic status changed to `done`

#### Scenario: Done surfaces not-ready conflict

- **WHEN** a user runs `mo epic done <id|num>`
- **AND** the Epic still has undelivered linked issues
- **AND** the API returns an `EPIC_NOT_READY_TO_MARK_DONE` conflict
- **THEN** the CLI surfaces the conflict clearly, indicating undelivered issues block the transition
- **AND** the CLI SHALL NOT report silent success
- **AND** the CLI exits with a non-zero status

#### Scenario: Close Epic

- **WHEN** a user runs `mo epic close <id|num>`
- **THEN** the CLI sends `POST /api/projects/{projectRef}/epics/{id}/close`
- **AND** prints confirmation that the Epic status changed to `closed`

#### Scenario: Terminal Epic lifecycle transition surfaces already-terminal conflict

- **WHEN** a user runs `mo epic done` or `mo epic close` on an Epic that is already terminal
- **AND** the API returns an `EPIC_ALREADY_TERMINAL` conflict
- **THEN** the CLI surfaces the conflict clearly
- **AND** the CLI SHALL NOT report silent success
- **AND** the CLI exits with a non-zero status

#### Scenario: Project override applies to all subcommands

- **WHEN** a user runs any `mo epic` subcommand with `--project <name>` or `--project-id <id>`
- **THEN** the CLI resolves the target project via the same mechanism as `mo issue`
- **AND** all Epic API calls for that invocation use the resolved project

#### Scenario: Output format selection applies to all subcommands

- **WHEN** a user runs any `mo epic` subcommand with `-o table` or `-o json`
- **THEN** the CLI formats output accordingly
- **AND** table mode uses `MohistCliApi.TableShape`
- **AND** json mode emits the API response verbatim without table formatting, color codes, or borders

#### Scenario: Epic group help

- **WHEN** a user runs `mo epic --help`
- **THEN** the CLI lists all eight subcommands: `list`, `create`, `show`, `update`, `link`, `unlink`, `done`, `close`
- **AND** each subcommand `--help` lists its positional arguments and options

#### Scenario: No Epic start command

- **WHEN** a user inspects `mo epic` subcommands
- **THEN** no subcommand starts workflow execution for an Epic
- **AND** Epics remain non-executable goal containers (status transitions only)

#### Scenario: CLI integration test coverage

- **WHEN** the CLI integration test suite runs
- **THEN** it SHALL cover `mo epic list` for both empty and non-empty projects
- **AND** it SHALL cover `mo epic create` failing clearly when the title argument is missing
- **AND** it SHALL cover `mo epic link` surfacing the duplicate-membership conflict
- **AND** it SHALL cover `mo epic done` surfacing the not-ready conflict

### Requirement: CLI reports attempt-derived recovery guidance

CLI issue status, issue show, and recovery command output SHALL report recovery guidance from the same API recovery projection used by the Web UI.

#### Scenario: CLI shows running recovery state

- **WHEN** an issue's latest attempt state is `running` with live execution evidence
- **THEN** CLI output SHALL describe the work as running
- **AND** guidance SHALL be wait or stop rather than retry

#### Scenario: CLI shows failed retry guidance

- **WHEN** an issue's latest attempt state is `failed`
- **AND** retry is an allowed action
- **THEN** CLI output SHALL present retry as available failed-work recovery

#### Scenario: CLI shows interrupted guidance

- **WHEN** an issue's latest attempt state is `interrupted`
- **THEN** CLI output SHALL distinguish interrupted work from failed work
- **AND** guidance SHALL mention resume, rerun stage, or inspect actions according to the API projection

#### Scenario: CLI agrees with API and UI fixtures

- **WHEN** the same issue fixture is rendered through API, Web UI, and CLI
- **THEN** all three surfaces SHALL agree on latest attempt state and recovery action availability

### Requirement: CLI provides mo issue feedback command group

The CLI SHALL expose a `mo issue feedback` command group with `list` and `show` subcommands for querying approval feedback records.

#### Scenario: Feedback subcommands appear in help

- **WHEN** the user runs `mo issue --help`
- **THEN** the output SHALL list `feedback` as a subcommand group
- **AND** `mo issue feedback --help` SHALL list `list` and `show` subcommands

#### Scenario: Feedback list command invoked

- **WHEN** the user runs `mo issue feedback list 42`
- **THEN** the CLI SHALL call `GET /api/issues/42/feedback`
- **AND** display the results in a formatted table

#### Scenario: Feedback show command invoked

- **WHEN** the user runs `mo issue feedback show 42 --feedback fb_123`
- **THEN** the CLI SHALL call `GET /api/issues/42/feedback/fb_123`
- **AND** display the feedback details

#### Scenario: Feedback commands require server

- **WHEN** the user runs any `mo issue feedback` command
- **AND** the server is not running
- **THEN** the CLI SHALL display "Server is not running. Start with: mo server start"
- **AND** exit with non-zero status

### Requirement: CLI feedback commands support JSON output for agent consumption

The `mo issue feedback list` and `mo issue feedback show` commands SHALL support `--output json` for machine-readable output suitable for agent consumption.

#### Scenario: Feedback list as JSON

- **WHEN** the user runs `mo issue feedback list 42 --output json`
- **THEN** the CLI SHALL output a valid JSON array
- **AND** each element SHALL match the stable feedback JSON schema

#### Scenario: Feedback show as JSON

- **WHEN** the user runs `mo issue feedback show 42 --feedback fb_123 --output json`
- **THEN** the CLI SHALL output a valid JSON object
- **AND** the object SHALL match the stable feedback JSON schema

#### Scenario: JSON output omits formatting

- **WHEN** `--output json` is used
- **THEN** the CLI SHALL NOT include table borders, color codes, or other terminal formatting
- **AND** the output SHALL be parseable by standard JSON parsers

### Requirement: CLI feedback commands support stage filtering

The `mo issue feedback` commands SHALL support `--stage` filtering to scope results to a specific workflow stage.

#### Scenario: List feedback filtered by stage

- **WHEN** the user runs `mo issue feedback list 42 --stage plan`
- **THEN** the CLI SHALL call the API with `?stage=plan`
- **AND** only feedback records for the `plan` stage SHALL be displayed

#### Scenario: Show latest feedback for a stage

- **WHEN** the user runs `mo issue feedback show 42 --latest --stage build`
- **THEN** the CLI SHALL retrieve the most recent feedback for the `build` stage
- **AND** display the result

### Requirement: CLI feedback commands support --latest flag

The `mo issue feedback show` command SHALL support `--latest` to retrieve the most recently created feedback record without specifying a feedback id.

#### Scenario: Show latest feedback

- **WHEN** the user runs `mo issue feedback show 42 --latest`
- **THEN** the CLI SHALL retrieve the most recently created feedback record for the issue
- **AND** display the result

#### Scenario: Show latest with stage filter

- **WHEN** the user runs `mo issue feedback show 42 --latest --stage plan`
- **THEN** the CLI SHALL retrieve the most recently created feedback record for `plan` stage
- **AND** display the result

### Requirement: CLI feedback commands support explicit project id

The `mo issue feedback` commands SHALL accept `--project-id` for explicit project targeting, and SHALL use the current project context when omitted.

#### Scenario: Explicit project id

- **WHEN** the user runs `mo issue feedback list 42 --project-id proj_abc`
- **THEN** the CLI SHALL use `proj_abc` as the project context for the API call

#### Scenario: Current project context used by default

- **WHEN** the user runs `mo issue feedback list 42` without `--project-id`
- **THEN** the CLI SHALL use the current project context

### Requirement: mo update displays product-level stages

`mo update` SHALL display user-facing product-level stages during the update process and SHALL NOT remain silent during long waits.

#### Scenario: Update stages are displayed

- **WHEN** user executes `mo update`
- **THEN** the CLI SHALL display a sequence of product-level stages
- **AND** each stage SHALL use user-facing language (e.g., "Updating CLI", "Preparing workflow runner")
- **AND** raw build output or implementation details SHALL NOT be the primary user-facing output

#### Scenario: Long readiness wait shows progress

- **WHEN** `mo update` is waiting for server readiness
- **AND** the wait exceeds a bounded progress interval
- **THEN** the CLI SHALL display the current wait reason (e.g., "waiting for Mohist API", "waiting for Web assets")
- **AND** SHALL update the displayed reason when the readiness stage transitions

#### Scenario: Runner-stopped window is visible

- **WHEN** `mo update` stops the runner for the server update phase
- **THEN** the CLI SHALL display that workflows are paused while the server updates
- **AND** this visibility SHALL persist until the runner is restored

### Requirement: mo update performs recovery on failure or interruption

`mo update` SHALL attempt to restore the runner on failure, timeout, or user interruption when the runner was running before the update began.

#### Scenario: Failed update restores runner

- **WHEN** `mo update` fails after stopping the runner
- **THEN** the CLI SHALL attempt to restart the runner service
- **AND** SHALL report the recovery outcome
- **AND** the exit code SHALL reflect the original failure

#### Scenario: Ctrl-C triggers recovery

- **WHEN** user presses Ctrl-C during `mo update`
- **AND** the runner was stopped for the update
- **THEN** the CLI SHALL attempt best-effort recovery
- **AND** SHALL print the final server and runner availability state

#### Scenario: Recovery failure provides actionable guidance

- **WHEN** runner recovery fails
- **THEN** the CLI SHALL print the specific unavailable capability
- **AND** SHALL provide a direct next action command

### Requirement: mo update reports final outcome by capability

The final output of `mo update` SHALL report one of three outcomes: ready, recovered with warnings, or failed with specific unavailable capabilities.

#### Scenario: Success outcome

- **WHEN** all update stages complete without error
- **THEN** the CLI SHALL print "Update complete. Mohist is ready."
- **AND** exit code SHALL be 0

#### Scenario: Recovered outcome

- **WHEN** update completes with non-critical recovery (e.g., runner restored after failure, skill assets missing)
- **THEN** the CLI SHALL print "Update recovered with warnings"
- **AND** SHALL list the warnings

#### Scenario: Failed outcome with specific capability

- **WHEN** update fails and recovery cannot restore a critical capability
- **THEN** the CLI SHALL print "Update failed: <capability> unavailable"
- **AND** exit code SHALL be non-zero

### Requirement: CLI update refreshes managed runner runtime

Full `mo update` SHALL rebuild the runner distribution and restart the managed runner runtime when the runner is installed and manageable. The command SHALL report whether runner refresh was performed or skipped, and skipped runner refresh SHALL include the reason.

#### Scenario: Full update refreshes installed runner
- **WHEN** the user runs `mo update`
- **AND** the local runner is installed and manageable
- **THEN** the CLI SHALL rebuild `packages/runner/dist`
- **AND** the CLI SHALL restart the managed runner service after the build succeeds
- **AND** the CLI output SHALL report that runner build and restart were performed

#### Scenario: Full update explains skipped runner refresh
- **WHEN** the user runs `mo update`
- **AND** runner refresh is skipped because the runner is not installed, not manageable, or not in scope
- **THEN** the CLI output SHALL report that runner refresh was skipped
- **AND** the output SHALL include the skip reason

### Requirement: CLI update verification detects stale runner runtime

Update verification SHALL validate runner runtime identity instead of only checking whether the runner service is active or connected. Verification SHALL fail or report an explicit degraded result when the live runner code identity does not match the current source or rebuilt distribution identity.

#### Scenario: Verification passes for matching runner runtime
- **WHEN** update verification runs after `mo update`
- **AND** the live runner code identity matches the current source or rebuilt `packages/runner/dist`
- **THEN** verification SHALL report the runner runtime as current
- **AND** it SHALL NOT rely only on service active or connected status

#### Scenario: Verification detects stale runner runtime
- **WHEN** update verification runs after `mo update`
- **AND** the runner service is active or connected
- **AND** the live runner code identity does not match the current source or rebuilt `packages/runner/dist`
- **THEN** verification SHALL report stale runner runtime evidence
- **AND** the update result SHALL NOT present runner runtime availability as fully healthy

#### Scenario: Verification records intentional runner skip
- **WHEN** update verification runs after a runner refresh was intentionally skipped
- **THEN** verification SHALL include the skipped runner refresh status and reason
- **AND** it SHALL distinguish intentional skip from a stale live runner mismatch

### Requirement: CLI server-only update is explicit about runner scope

`mo update server` SHALL have explicit server-only semantics. The command SHALL not imply that runner build output or live runner runtime code was refreshed, and SHALL provide clear next-step guidance when runner refresh remains necessary.

#### Scenario: Server-only update reports runner not refreshed
- **WHEN** the user runs `mo update server`
- **THEN** the CLI SHALL update only server-scoped runtime components
- **AND** the CLI output SHALL state that runner build output and runner runtime were not refreshed by this command

#### Scenario: Server-only update gives runner follow-up guidance
- **WHEN** the user runs `mo update server`
- **AND** runner refresh may still be needed for local workflow execution
- **THEN** the CLI output SHALL provide a clear follow-up action for refreshing the runner
- **AND** it SHALL not report overall local runtime freshness as if the runner had been updated

### Requirement: CLI issue create parses YAML frontmatter from body file

`mo issue create --body-file <file>` SHALL parse YAML frontmatter from the body file when the file begins with `---`. Recognized frontmatter fields SHALL be extracted and used to auto-populate the issue's `workflowProfileId` and `risk` fields.

#### Scenario: Body file with recommended_workflow auto-fills workflow profile

- **WHEN** the user runs `mo issue create "Title" --body-file body.md`
- **AND** `body.md` begins with YAML frontmatter containing `recommended_workflow: feature-flow`
- **THEN** the CLI SHALL parse the `recommended_workflow` field
- **AND** send `workflowProfileId: "feature-flow"` in the create request body
- **AND** the created issue SHALL have the `feature-flow` workflow profile assigned

#### Scenario: Body file with risk auto-fills risk

- **WHEN** the user runs `mo issue create "Title" --body-file body.md`
- **AND** `body.md` frontmatter contains `risk: high`
- **THEN** the CLI SHALL parse the `risk` field
- **AND** send `risk: "high"` in the create request body
- **AND** the created issue SHALL have risk set to `high`

#### Scenario: Body file frontmatter with both workflow and risk

- **WHEN** the user runs `mo issue create "Title" --body-file body.md`
- **AND** frontmatter contains both `recommended_workflow` and `risk`
- **THEN** the CLI SHALL parse and send both values in the create request

#### Scenario: Body file without frontmatter emits warning

- **WHEN** the user runs `mo issue create "Title" --body-file body.md`
- **AND** `body.md` does not begin with `---` (no frontmatter)
- **THEN** the CLI SHALL emit a warning: "No frontmatter found in body file. Consider including recommended_workflow and risk."
- **AND** the issue SHALL still be created successfully

#### Scenario: Malformed YAML frontmatter emits warning

- **WHEN** the user runs `mo issue create "Title" --body-file body.md`
- **AND** `body.md` begins with `---` but contains invalid YAML
- **THEN** the CLI SHALL emit a warning about the parse failure
- **AND** the issue SHALL still be created successfully with the full body text

#### Scenario: Body file with unrecognized frontmatter fields

- **WHEN** frontmatter contains fields other than `recommended_workflow`, `recommended_workflow_reason`, or `risk`
- **THEN** the CLI SHALL silently ignore unrecognized fields
- **AND** the issue SHALL still be created with recognized fields applied

### Requirement: CLI flags override frontmatter values

Explicit CLI flags SHALL take precedence over values parsed from body file frontmatter.

#### Scenario: --workflow-profile flag overrides frontmatter

- **WHEN** the user runs `mo issue create "Title" --body-file body.md --workflow-profile mohist/default`
- **AND** `body.md` frontmatter contains `recommended_workflow: feature-flow`
- **THEN** the CLI SHALL use `mohist/default` from the explicit flag
- **AND** the CLI SHALL emit a note indicating the flag overrode the frontmatter value

#### Scenario: Explicit risk flag overrides frontmatter (if risk flag exists)

- **WHEN** an explicit risk-related flag is provided alongside a body file with frontmatter risk
- **THEN** the explicit flag SHALL take precedence

### Requirement: CLI issue create emits frontmatter-aware success output

Successful `mo issue create` output SHALL include workflow and risk information when present, and SHALL update the start tip to include the workflow context.

#### Scenario: Success output includes workflow and risk

- **WHEN** `mo issue create` succeeds with a workflow profile and risk set (from frontmatter or flags)
- **THEN** the output SHALL include "Workflow: <profile>" and "Risk: <level>"

#### Scenario: Success output without workflow still works

- **WHEN** `mo issue create` succeeds without a workflow profile or risk
- **THEN** the output SHALL follow the existing format without adding workflow or risk lines
### Requirement: Issue create success output guides the next step from server start readiness

Successful `mo issue create` output SHALL print the created issue number and priority, and SHALL guide the next step from server-provided start readiness (`canStart` / `blocker`). Because new Issues default to draft, the default create output SHALL NOT show a start tip and SHALL instead guide marking the Issue ready. The start tip SHALL be shown only when the created Issue is ready and startable.

#### Scenario: Created draft issue guides marking ready
- **WHEN** `mo issue create` returns a draft Issue (`canStart = false`, `blocker` of `Draft`)
- **THEN** the CLI prints `Created issue #N: <title>`
- **AND** prints the issue priority
- **AND** does NOT print a `mo issue start` tip
- **AND** prints guidance equivalent to marking the Issue ready before starting

#### Scenario: Start tip shown for ready startable issue
- **WHEN** `mo issue create` returns a ready Issue (`isDraft = false`) with no blocker
- **THEN** the CLI prints `Created issue #N: <title>`
- **AND** prints the issue priority
- **AND** prints `Tip: Run 'mo issue start <number>' to begin processing`

#### Scenario: Start tip omitted while waiting for delivery
- **WHEN** `mo issue create` or a later CLI display receives a ready Issue whose `blocker` is `WaitingFor(Issue)` identifying Issue #N
- **THEN** the CLI does not tell the user to start that Issue now
- **AND** the CLI prints a waiting reason equivalent to `Waiting for #N`

### Requirement: CLI start uses server start readiness rejection

`mo issue start <number>` SHALL use the Server API start endpoint as the source of truth for start readiness. When the server rejects start because the Issue is a draft or is waiting for a prerequisite to be delivered, the CLI SHALL surface that message without starting any local workflow behavior.

#### Scenario: Start command rejected for a draft issue
- **WHEN** the user runs `mo issue start 201`
- **AND** the Server API returns that Issue #201 is still a draft
- **THEN** CLI prints the server-provided actionable message
- **AND** CLI exits with a non-zero status
- **AND** CLI does not make any additional request intended to enqueue or resume pipeline work

#### Scenario: Start command rejected while waiting for delivery
- **WHEN** the user runs `mo issue start 201`
- **AND** the Server API returns that Issue #201 is waiting for prerequisite Issue #200 to be delivered
- **THEN** CLI prints the server-provided actionable message
- **AND** CLI exits with a non-zero status
- **AND** CLI does not make any additional request intended to enqueue or resume pipeline work

### Requirement: CLI toggles issue draft state

The CLI SHALL create new Issues as draft by default and SHALL allow the user to mark an Issue ready or return it to draft. Create SHALL send the appropriate `isDraft` value; update SHALL accept an option to set `isDraft`. The CLI SHALL NOT compute `isDraft`, `canStart`, or `blocker` locally.

#### Scenario: Create defaults to draft

- **WHEN** the user runs `mo issue create "Title"` without an explicit draft/ready choice
- **THEN** the CLI creates the Issue as draft
- **AND** the resulting Issue has `isDraft = true`

#### Scenario: Create explicitly ready

- **WHEN** the user runs `mo issue create "Title"` with the ready option
- **THEN** the CLI sends `isDraft = false` in the create request
- **AND** the resulting Issue is ready subject to its prerequisites

#### Scenario: Mark an issue ready from the CLI

- **WHEN** the user runs `mo issue update <number>` with the ready option
- **THEN** the CLI sends `isDraft = false` in the update request
- **AND** the CLI does not start the Issue

### Requirement: CLI provides mo agent command group

The CLI SHALL provide a top-level `mo agent` command group with `create`, `list`, `show`, `update`, and `delete` subcommands, mirroring the existing `mo issue` verb model. The command group SHALL communicate with the server through the shared `apiClient` and SHALL NOT contain business logic. All commands SHALL require the server to be running and SHALL surface the standard "Server is not running" error when it is unavailable.

#### Scenario: agent subcommands appear in help

- **WHEN** the user runs `mo agent --help`
- **THEN** the output SHALL list `create`, `list`, `show`, `update`, and `delete` subcommands

#### Scenario: agent commands require server

- **WHEN** the user runs any `mo agent` subcommand
- **AND** the server is not running
- **THEN** the CLI SHALL display "Server is not running. Start with: mo server start"
- **AND** SHALL exit with a non-zero status

### Requirement: mo agent create

`mo agent create` SHALL accept `--name <n>` (required) and `--instructions <text>` (required), and SHALL create an Agent in the current project context via `POST /agents`. It SHALL return the created agent id on success. It SHALL accept optional flags `--description <text>`, `--agent-config <json|@file>`, `--skills <csv>`, and `--max-concurrent-runs <int>`. The `--instructions` flag SHALL accept a literal string, a curl-style `@file` reference, and `-` to read from stdin, consistent with `mo issue create --body` behavior. When the server returns a name-conflict error, the CLI SHALL surface a readable conflict message and exit non-zero.

#### Scenario: Create with required fields

- **WHEN** the user runs `mo agent create --name reviewer --instructions "You are a senior reviewer."`
- **THEN** the CLI SHALL send `POST /agents` with `name` and `instructions`
- **AND** the CLI SHALL print the created agent id

#### Scenario: Create with optional fields

- **WHEN** the user runs `mo agent create --name reviewer --instructions "..." --description "..." --agent-config '{"model":"..."}' --skills "mohist,fsd" --max-concurrent-runs 2`
- **THEN** the CLI SHALL send all provided optional fields in the create request body

#### Scenario: Instructions read from file or stdin

- **WHEN** the user runs `mo agent create --name reviewer --instructions @prompt.md` or `--instructions -`
- **THEN** the CLI SHALL resolve the instructions text from the file or stdin before sending the request
- **AND** SHALL send the resolved text verbatim

#### Scenario: Missing required field fails

- **WHEN** the user runs `mo agent create` without `--name` or `--instructions`
- **THEN** the CLI SHALL print a clear validation error
- **AND** SHALL exit with code 1

#### Scenario: Name conflict surfaced clearly

- **WHEN** the user runs `mo agent create --name reviewer` and the server returns HTTP 409
- **THEN** the CLI SHALL print a readable conflict error naming the conflicting `name`
- **AND** SHALL exit with a non-zero status

### Requirement: mo agent list with status filters

`mo agent list` SHALL list Agents in the current project context. By default it SHALL list only `status` = `active` Agents. It SHALL support `--all` to include archived Agents and `--status <status>` to filter to a single status value (e.g. `--status archived`). The output SHALL be tabular and human-readable by default.

#### Scenario: List defaults to active

- **WHEN** the user runs `mo agent list`
- **THEN** the CLI SHALL call `GET /agents`
- **AND** SHALL display only active Agents

#### Scenario: List includes archived with --all

- **WHEN** the user runs `mo agent list --all`
- **THEN** the CLI SHALL call `GET /agents?all=true`
- **AND** SHALL display both active and archived Agents

#### Scenario: List filtered by status

- **WHEN** the user runs `mo agent list --status archived`
- **THEN** the CLI SHALL call `GET /agents?status=archived`
- **AND** SHALL display only archived Agents

### Requirement: mo agent show accepts name or id

`mo agent show <name-or-id>` SHALL resolve the argument as either the Agent `name` or `id` in the current project context and SHALL display the full Agent record, including `createdAt` and `updatedAt`.

#### Scenario: Show by name

- **WHEN** the user runs `mo agent show reviewer`
- **AND** an Agent named `reviewer` exists in the current project
- **THEN** the CLI SHALL resolve the name to the Agent and display the full record

#### Scenario: Show by id

- **WHEN** the user runs `mo agent show agent_abc123`
- **THEN** the CLI SHALL display the full Agent record for that id

#### Scenario: Show includes timestamps

- **WHEN** the user runs `mo agent show <name-or-id>` for any existing Agent
- **THEN** the output SHALL include `createdAt` and `updatedAt`

#### Scenario: Show unknown Agent fails

- **WHEN** the user runs `mo agent show <name-or-id>` and no matching Agent exists
- **THEN** the CLI SHALL print a clear not-found error
- **AND** SHALL exit with a non-zero status

### Requirement: mo agent update

`mo agent update <name-or-id>` SHALL resolve the argument as name or id and SHALL accept updates to `--name`, `--description`, `--instructions`, `--agent-config`, `--skills`, and `--max-concurrent-runs`. A rename SHALL be subject to the same project-scoped uniqueness rules as create. The CLI SHALL NOT permit changing `createdAt` and SHALL reflect the refreshed `updatedAt` returned by the server.

#### Scenario: Update mutable fields

- **WHEN** the user runs `mo agent update reviewer --instructions "New prompt"`
- **THEN** the CLI SHALL send `PATCH /agents/{id}` with the changed field
- **AND** SHALL display the updated Agent

#### Scenario: Rename applies uniqueness check

- **WHEN** the user runs `mo agent update reviewer --name coder`
- **AND** `coder` is already used by another Agent in the project
- **THEN** the CLI SHALL surface the server's 409 conflict as a readable error
- **AND** SHALL exit with a non-zero status

#### Scenario: Update reflects refreshed updatedAt

- **WHEN** the user runs `mo agent update <name-or-id>` and the update succeeds
- **THEN** the displayed record SHALL show a refreshed `updatedAt`

### Requirement: mo agent delete performs soft archive

`mo agent delete <name-or-id>` SHALL resolve the argument as name or id and SHALL call `DELETE /agents/{id}`, which archives the Agent. The CLI output SHALL make clear that the Agent was archived rather than hard-deleted.

#### Scenario: Delete archives the Agent

- **WHEN** the user runs `mo agent delete reviewer`
- **THEN** the CLI SHALL call `DELETE /agents/{id}`
- **AND** the output SHALL state that the Agent was archived

#### Scenario: Deleted name cannot be reused

- **WHEN** the user runs `mo agent delete reviewer`
- **AND** later runs `mo agent create --name reviewer`
- **THEN** the create SHALL fail with a name-conflict error
- **AND** the CLI SHALL surface the conflict clearly

#### Scenario: Delete unknown Agent fails

- **WHEN** the user runs `mo agent delete <name-or-id>` and no matching Agent exists
- **THEN** the CLI SHALL print a clear not-found error
- **AND** SHALL exit with a non-zero status
