## Requirements

### Requirement: Agent status API 暴露 ask_user 等待状态

`AgentStatus` 返回值 SHALL 新增 `waitingQuestions` 数组，包含当前所有在 ask_user 中等待回答的 agent 信息：`{ issueId, issueNumber, projectId, questionId, question }`。

#### Scenario: agent 在 ask_user 中等待
- **WHEN** Main Agent 调用 ask_user 工具并阻塞等待回答
- **THEN** `GET /api/agent/status` 返回的 `waitingQuestions` 数组包含该 issue 的条目
- **AND** 条目包含 issueId、issueNumber、projectId、questionId 和 question 内容

#### Scenario: 用户回答后等待状态清除
- **WHEN** 用户通过 `POST /api/questions/:id/reply` 回答了一个问题
- **THEN** `waitingQuestions` 数组中对应条目被移除
- **AND** `GET /api/agent/status` 不再包含该 issue

#### Scenario: ask_user 超时后等待状态清除
- **WHEN** ask_user 等待超时（24 小时）
- **THEN** `waitingQuestions` 数组中对应条目被移除

### Requirement: AgentRunnerService 追踪 ask_user 等待状态

AgentRunnerService SHALL 维护 `waitingQuestions` Map（`issueId → { questionId, question }`），在 ask_user 创建问题时添加条目，在回答或超时后移除条目。

#### Scenario: ask_user 通知等待状态
- **WHEN** ask_user 工具创建一个问题并开始等待
- **THEN** ask_user 通过 `onWaitingChange` 回调通知 AgentRunnerService
- **AND** AgentRunnerService 在 waitingQuestions Map 中添加条目

#### Scenario: ask_user 完成后清除等待状态
- **WHEN** ask_user 工具收到用户回答或超时
- **THEN** ask_user 通过 `onWaitingChange` 回调通知 AgentRunnerService
- **AND** AgentRunnerService 从 waitingQuestions Map 中移除条目
