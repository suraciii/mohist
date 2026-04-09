# attach-unified-interaction

This specification defines the unified interaction model for `mo attach`, enabling it to handle both gate pauses and ask_user questions through a single interface.

## Overview

The `mo attach` command provides real-time monitoring and interaction with agent events via SSE. It now supports two interaction modes:

1. **GATE_MODE**: When the agent pauses at a gate waiting for user approval
2. **QUESTION_MODE**: When the agent asks a question via `ask_user` tool

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

## Interaction State Machine

```typescript
type InteractionState = 
  | { type: 'IDLE' }
  | { type: 'GATE_MODE'; issueId: string; issueNumber: number }
  | { type: 'QUESTION_MODE'; questionId: string; question: string; issueId: string };
```

### State Transitions

| Event | Current State | New State |
|-------|--------------|-----------|
| `agent_paused` | IDLE | GATE_MODE |
| `question_asked` | IDLE | QUESTION_MODE |
| `agent_started` | Any | IDLE |
| `agent_completed` | Any | IDLE |
| `question_answered` | QUESTION_MODE | IDLE |

## User Experience

### QUESTION_MODE Visual Display

```
┌─────────────────────────────────────────────────────────────┐
│  [Question] Agent is asking for issue #123:                  │
│                                                              │
│  "Which approach do you prefer: A or B?"                     │
│                                                              │
│  Type your answer below, or 'quit' to detach:               │
└─────────────────────────────────────────────────────────────┘
> 
```

### Quit Behavior in QUESTION_MODE

When user types `quit` or `exit` in QUESTION_MODE:
- The system displays a warning: "Warning: Quitting without answering. The agent will wait 24h for timeout."
- Additional guidance: "Use 'mo question reply <questionId>' later to answer, or let it timeout."
- The attach session terminates normally

## Event Formatting

### question_asked
- **Symbol**: `??`
- **Color**: Yellow
- **Display**: Issue ID and question text

### question_answered
- **Symbol**: `✓`
- **Color**: Green
- **Display**: Confirmation message

## API Endpoints

### POST /api/questions/:id/reply
Submit an answer to a pending question.

**Request Body**:
```json
{
  "answer": "User's response text"
}
```

**Response Codes**:
- `200`: Answer accepted
- `409`: Question already answered or expired
- `410`: Agent no longer waiting for this question

## Error Handling

- **409/410 errors**: Display appropriate message and return to IDLE state
- **Network errors**: Display error message and return to IDLE state
- **State mismatch**: If question_answered event doesn't match current questionId, ignore (may be for a different question)
