## MODIFIED Requirements

### Requirement: EventBus 支持 question 事件

EventBus SHALL 支持以下新事件类型：

- `question_asked`: payload `{ issueId, projectId, questionId, question }`
- `question_answered`: payload `{ issueId, projectId, questionId, answer }`

#### Scenario: question_asked 事件推送
- **WHEN** ask_user 工具创建一个新问题
- **THEN** EventBus emit `question_asked` 事件
- **AND** SSE 客户端收到该事件

#### Scenario: question_answered 事件推送
- **WHEN** 用户通过 API 回复一个问题
- **THEN** EventBus emit `question_answered` 事件
- **AND** SSE 客户端收到该事件
