## MODIFIED Requirements

### Requirement: REQ-CST-214 Tool lifecycle identity remains stable across live and replayed coder sessions

Coder session tracking SHALL preserve normalized tool name and tool call id identity from Agent runtime so a single real tool invocation is represented as one logical lifecycle in live events and replayed transcripts.

#### Scenario: Live coder tool event carries recovered name
- **WHEN** Agent runtime receives an ACP tool notification whose explicit name is only available as `name` or a top-level field
- **THEN** `coder_tool_call` events SHALL carry the recovered tool name instead of an empty or `unknown` value

#### Scenario: Start and update share one id
- **WHEN** a tool start and completion/update notification refer to the same provider call id or normalized synthetic id
- **THEN** live coder events and replayed session transcripts SHALL use the same `toolCallId`
- **AND** SHALL NOT render separate started and completed entries for the same invocation

#### Scenario: Raw payload details remain available
- **WHEN** a normalized tool update contains input, output, title, status, or metadata
- **THEN** those details SHALL remain available in persisted session data and transcript assembly
