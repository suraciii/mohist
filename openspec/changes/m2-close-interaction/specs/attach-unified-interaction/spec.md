## ADDED Requirements

### Requirement: mo attach SHALL handle question_asked events
mo attach SHALL listen for `question_asked` SSE events and enter QUESTION_MODE, displaying the question and prompting the user to reply.

#### Scenario: Agent asks a question while mo attach is connected
- **WHEN** mo attach receives a `question_asked` event
- **THEN** mo attach SHALL display the question content and enter QUESTION_MODE with a prompt indicating the user can type an answer

#### Scenario: Agent asks a question while already in GATE_MODE
- **WHEN** mo attach is in GATE_MODE and receives a `question_asked` event
- **THEN** this SHALL NOT happen because gate pause and ask_user are temporally mutually exclusive

### Requirement: mo attach SHALL route user input based on interaction mode
In QUESTION_MODE, user input SHALL be sent via `POST /questions/:id/reply`. In GATE_MODE, user input SHALL be sent via `POST /issues/:number/messages`.

#### Scenario: User replies in QUESTION_MODE
- **WHEN** user types text and presses enter in QUESTION_MODE
- **THEN** mo attach SHALL POST the text to `/api/questions/<questionId>/reply` with `{ answer: text }`

#### Scenario: User sends message in GATE_MODE
- **WHEN** user types text and presses enter in GATE_MODE
- **THEN** mo attach SHALL POST the text to `/api/issues/<issueNumber>/messages` with `{ message: text }` (existing behavior)

#### Scenario: User types in IDLE mode
- **WHEN** user types text and presses enter in IDLE mode
- **THEN** mo attach SHALL display "No paused agent or pending question" and return to IDLE

### Requirement: mo attach SHALL exit QUESTION_MODE on question_answered or agent completion
QUESTION_MODE SHALL end when the question is answered or the agent finishes.

#### Scenario: Question answered externally
- **WHEN** mo attach is in QUESTION_MODE and receives a `question_answered` event for the current questionId
- **THEN** mo attach SHALL return to IDLE

#### Scenario: Agent completes while in QUESTION_MODE
- **WHEN** mo attach is in QUESTION_MODE and receives `agent_started` or `agent_completed`
- **THEN** mo attach SHALL return to IDLE

#### Scenario: Reply fails due to expired question
- **WHEN** user submits a reply and the API returns 409 or 410
- **THEN** mo attach SHALL display the error and return to IDLE

### Requirement: event-formatter SHALL format question events
`question_asked` and `question_answered` events SHALL have formatted output in mo attach.

#### Scenario: question_asked event displayed
- **WHEN** mo attach receives a `question_asked` event
- **THEN** event-formatter SHALL display a formatted line including the issue ID and question text

#### Scenario: question_answered event displayed
- **WHEN** mo attach receives a `question_answered` event
- **THEN** event-formatter SHALL display a formatted confirmation line
