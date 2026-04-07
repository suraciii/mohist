## MODIFIED Requirements

### Requirement: ask_user 工具阻塞等待用户回复

ask_user 工具 SHALL 接受一个 question 字符串参数，创建问题记录，emit 事件，然后阻塞直到用户通过 API 回复或超时。`AskUserContext` SHALL 新增可选的 `onWaitingChange` 回调，在等待开始时调用 `onWaitingChange(issueId, questionId)`，在等待结束时调用 `onWaitingChange(issueId, null)`。`AskUserContext` 新增 `issueRepo` 字段，用于当 `projectId` 缺失时查询 issue。

#### Scenario: agent 提问并收到回复
- **WHEN** Main Agent 调用 ask_user("这个 API 应该返回 JSON 还是 XML？")
- **THEN** 一条 pending 问题记录创建到 questions 表
- **AND** EventBus emit `question_asked` 事件，payload 包含正确的 projectId（非空字符串）
- **AND** 如果 `onWaitingChange` 回调存在，调用 `onWaitingChange(issueId, questionId)`
- **AND** 工具执行阻塞，不返回给 LLM
- **WHEN** 用户通过 API 回复 "JSON"
- **THEN** 如果 `onWaitingChange` 回调存在，调用 `onWaitingChange(issueId, null)`
- **AND** 工具返回 "用户回答: JSON" 给 LLM
- **AND** LLM 继续执行下一步

#### Scenario: 用户超时未回复
- **WHEN** Main Agent 调用 ask_user
- **AND** 用户在超时时间内未回复
- **THEN** 问题状态设为 `expired`
- **AND** 如果 `onWaitingChange` 回调存在，调用 `onWaitingChange(issueId, null)`
- **AND** 工具返回 "No answer received within timeout. Proceed with your best judgment."
- **AND** LLM 可以继续执行或选择停止

#### Scenario: question_asked 事件携带正确 projectId
- **WHEN** ask_user 工具创建问题
- **AND** `AskUserContext.projectId` 有值
- **THEN** `question_asked` 事件的 payload.projectId SHALL 等于 context.projectId
- **AND** payload.projectId SHALL NOT 为空字符串

#### Scenario: projectId 缺失时通过 issue 查询
- **WHEN** ask_user 工具创建问题
- **AND** `AskUserContext.projectId` 为 undefined
- **THEN** ask_user 工具 SHALL 通过 `context.issueRepo.findById(issueId)` 查询 issue
- **AND** `question_asked` 事件的 payload.projectId SHALL 等于 issue.projectId
- **AND** payload.projectId SHALL NOT 为空字符串
