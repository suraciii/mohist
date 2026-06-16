## MODIFIED Requirements

### Requirement: Web UI 实时响应 agent 暂停状态

Web UI SHALL 监听 `agent_paused` SSE 事件，在收到事件后刷新 issue 详情和列表数据，使审批提示自动出现。审批卡 SHALL 提供 `Approve` 和 `Request changes` 两个操作，不提供 `Reject` 或 `Send back` 操作。

#### Scenario: agent 暂停后审批面板自动显示

- **WHEN** agent 完成一个带 approval 的阶段
- **AND** 用户正在 Web UI 查看该 issue
- **THEN** 不需要手动刷新，审批面板自动显示 "Approve" 和 "Request changes" 按钮

#### Scenario: Issue 卡片状态实时更新

- **WHEN** agent 暂停
- **AND** 用户在看板页面
- **THEN** 对应 issue 卡片自动更新状态指示器（显示 "Needs Approval" 或类似标记）

## ADDED Requirements

### Requirement: Approval card provides Approve and Request changes actions

The approval card in Issue Detail SHALL present two user-facing actions: `Approve` and `Request changes`. The card SHALL NOT present `Reject`, `Send back`, or any other terminal rejection action. The `Request changes` action SHALL require a feedback text input.

#### Scenario: Approve action advances the workflow

- **WHEN** the user clicks `Approve`
- **AND** all required checks have passed
- **THEN** the approval is accepted
- **AND** the workflow advances to the next stage

#### Scenario: Request changes requires feedback text

- **WHEN** the user clicks `Request changes`
- **THEN** a feedback text input SHALL be presented
- **AND** the action SHALL NOT submit without feedback text

#### Scenario: Request changes triggers feedback loop

- **WHEN** the user submits `Request changes` with feedback text
- **THEN** the API SHALL be called to create the feedback record
- **AND** the stage SHALL resume as running
- **AND** the UI SHALL refresh to show the `apply-feedback` task in progress

#### Scenario: Approval card does not show Reject or Send back

- **WHEN** the approval card is rendered
- **THEN** the visible actions SHALL be `Approve` and `Request changes`
- **AND** `Reject` and `Send back` SHALL NOT appear as user-facing labels or actions

### Requirement: Approval history shows feedback-resolution trail

The Issue Detail approval history SHALL display the complete feedback-resolution trail: approval request, feedback requested, feedback task execution, resolution summary, check rerun results, and next approval request.

#### Scenario: Feedback cycle is visible in approval history

- **GIVEN** a stage has gone through: approval request -> feedback requested -> feedback task applied -> checks rerun -> approval requested again
- **WHEN** the user views the approval history timeline
- **THEN** the timeline SHALL show each step in chronological order
- **AND** the feedback body SHALL be accessible
- **AND** the resolution summary SHALL be displayed

#### Scenario: Multiple feedback cycles are distinct

- **GIVEN** a user has requested changes twice for the same stage
- **WHEN** the approval history is rendered
- **THEN** each feedback cycle SHALL be visually distinct
- **AND** each cycle SHALL show its own feedback body, resolution, and check results

#### Scenario: Feedback history is separate from comments

- **WHEN** the approval history is rendered
- **THEN** feedback items SHALL appear as workflow control history
- **AND** feedback SHALL NOT be displayed only as generic comments
- **AND** the approval history SHALL clearly distinguish feedback from discussion comments

### Requirement: Approval history renders feedback resolution summary

When feedback has been resolved, the approval history SHALL display the resolution summary written by the agent.

#### Scenario: Resolved feedback shows resolution

- **WHEN** feedback has `status = resolved` and a `resolutionSummary` is present
- **THEN** the approval history SHALL display the resolution summary
- **AND** the display SHALL indicate that the feedback was addressed

#### Scenario: Open feedback shows pending state

- **WHEN** feedback has `status = open`
- **THEN** the approval history SHALL indicate the feedback is awaiting application
- **AND** the pending `apply-feedback` task SHALL be visible in stage progress

### Requirement: Request changes action is available only at approval gates

The `Request changes` action SHALL only be available when the stage is awaiting user approval. It SHALL NOT be available during normal stage execution.

#### Scenario: Request changes hidden during running stage

- **WHEN** the stage is running (not awaiting approval)
- **THEN** the `Request changes` action SHALL NOT be displayed
