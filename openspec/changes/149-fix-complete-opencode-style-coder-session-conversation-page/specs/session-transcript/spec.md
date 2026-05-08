## ADDED Requirements

### Requirement: Stable normalized session transcript

The system SHALL assemble coder session events into a stable read-only transcript made of Mohist turns and Coder assistant parts. The same persisted event set SHALL always produce the same turn order, part order, and transcript metadata.

#### Scenario: Same-second events replay deterministically

- **WHEN** multiple session events have the same timestamp
- **THEN** the transcript orders prompts before assistant activity, tool starts before tool updates, and terminal events after emitted assistant parts
- **AND** repeated API fetches return the same transcript shape

#### Scenario: Prompt opens a turn

- **WHEN** a `mohist_prompt` event is assembled
- **THEN** it opens a new turn with user role `mohist`
- **AND** the next prompt or terminal event closes the previous open turn

#### Scenario: Transcript metadata is exposed

- **WHEN** a session transcript is assembled
- **THEN** metadata includes last activity, event count, tool count, turn count, changed files, transcript warnings, and whether unknown tools remain

### Requirement: Tool identity and merge normalization

The transcript assembler SHALL normalize tool identity and merge tool start/update events before exposing tool parts to the frontend. Inferable payloads SHALL NOT render as `unknown` tools.

#### Scenario: Nested and top-level tool ids merge

- **WHEN** tool start and update events carry `toolCall.toolCallId`, top-level `toolCallId`, `id`, or `callId`
- **THEN** they merge into one tool part with status, input, output, error, title, target, and timestamps where available

#### Scenario: Tool name inferred from payload

- **WHEN** a tool payload lacks `toolName` but includes `name`, title such as `apply_patch`, raw input shape, or raw output metadata that identifies the tool
- **THEN** the assembler exposes the inferred normalized tool name
- **AND** only non-inferable payloads may remain unknown with a transcript warning

#### Scenario: No-id ACP payloads merge by correlation

- **WHEN** ACP tool start/update events have no id but share inferable name and target/title correlation
- **THEN** the assembler merges them into the same tool part

### Requirement: Prompt summary with raw prompt audit

Mohist prompts SHALL expose a readable summary for default display while preserving the complete raw prompt for audit.

#### Scenario: Structured Mohist task prompt summarized

- **WHEN** a prompt contains Mohist task sections such as role, task, contract, or context files
- **THEN** the transcript exposes summary fields including title, subtitle/output path, kind, and context where inferable
- **AND** the full prompt remains available as raw text

#### Scenario: Legacy missing prompt fallback

- **WHEN** historical assistant/tool events exist but no Mohist prompt was persisted
- **THEN** the transcript contains an incomplete legacy turn that clearly states the prompt was not recorded

### Requirement: File change summaries

File-changing tools SHALL expose file-level change summaries suitable for default UI rendering.

#### Scenario: apply_patch summary

- **WHEN** an `apply_patch` payload contains Add File, Update File, Delete File, or Move to operations
- **THEN** the transcript exposes changed file summaries with created, modified, deleted, or moved operations
- **AND** additions/deletions are included when available or safely estimable

#### Scenario: raw patch retained

- **WHEN** file change summaries are exposed
- **THEN** raw patch, raw input, and raw output remain available for expandable audit details
