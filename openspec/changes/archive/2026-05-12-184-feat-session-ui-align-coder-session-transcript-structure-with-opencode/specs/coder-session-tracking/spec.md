## MODIFIED Requirements

### Requirement: Transcript assembly preserves stable tool identity and turn semantics

Coder session tracking SHALL preserve the information needed to reconstruct stable prompt-led turns, merged tool lifecycle state, and readable historical replay across live and completed sessions.

#### Scenario: Tool lifecycle updates resolve to one logical tool

- **WHEN** a tool emits start and update or completion events for the same invocation
- **THEN** transcript assembly merges those events into one logical tool record whenever identity can be inferred
- **AND** replay does not show duplicate running and completed entries for the same tool invocation

#### Scenario: Unknown-tool fallback is last resort

- **WHEN** a tool name is absent or malformed in tracked events
- **THEN** transcript assembly infers tool identity from toolName, name, title, payload shape, or metadata before falling back to `unknown`

#### Scenario: Historical replay stays ordered and readable

- **WHEN** prompts, assistant output, tool updates, and terminal events share close timestamps
- **THEN** transcript assembly still produces deterministic turn ordering with prompts opening turns before assistant activity and terminal events closing them last
