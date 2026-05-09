## MODIFIED Requirements

### Requirement: Tool lifecycle normalization

Coder session transcript assembly SHALL normalize raw tool lifecycle events into one logical tool part per real tool call. `tool_call` and `tool_call_update` events for the same provider call id, ACP call id, nested tool call id, or deterministic correlation key SHALL merge into a single stable transcript part.

#### Scenario: Tool start and update merge by id

- **WHEN** a persisted session contains `tool_call` and `tool_call_update` events for the same `toolCallId`, nested `toolCall.toolCallId`, `id`, or `callId`
- **THEN** the transcript exposes exactly one tool part for that tool call
- **AND** the tool part contains the best available name, title, input, output, status, timestamps, and error data

#### Scenario: No-id tool events merge by correlation

- **WHEN** a tool start event and a later update event do not carry a stable id but share inferable normalized name plus target or title
- **THEN** the transcript merges them into one logical tool part
- **AND** ambiguous name-only fallback correlation adds a transcript warning rather than silently implying certainty

#### Scenario: Inferable tools avoid unknown fallback

- **WHEN** a raw tool event lacks `toolName` but contains a known `name`, title, raw input shape, command, file path, pattern, patch text, todo payload, or raw output metadata
- **THEN** the transcript infers a useful normalized name and display title
- **AND** the visible transcript does not show an orphan `unknown running...` entry

#### Scenario: Tool status is normalized for transcript display

- **WHEN** raw tool lifecycle status is pending, started, running, completed, failed, cancelled, or timeout-like
- **THEN** the transcript exposes an accurate display status of pending, running, completed, failed, or cancelled where available
- **AND** only non-terminal logical tools appear as active/running in the UI
