## ADDED Requirements

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

## MODIFIED Requirements

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
