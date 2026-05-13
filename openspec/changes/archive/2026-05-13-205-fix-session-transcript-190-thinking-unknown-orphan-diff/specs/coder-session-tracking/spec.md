## MODIFIED Requirements

### Requirement: Transcript assembly preserves emitted reasoning and text ordering

Session transcript assembly SHALL preserve the emitted alternation between reasoning and assistant text by closing the currently open opposite stream part before appending the next chunk type.

#### Scenario: Text closes active reasoning before continuing

- **WHEN** a text chunk arrives while a reasoning part is still open
- **THEN** the assembler completes the reasoning part before appending text
- **AND** the stored transcript keeps the original emitted order

#### Scenario: Non-stream parts close active text or reasoning

- **WHEN** a tool, error, or terminal part is appended while text or reasoning is still streaming
- **THEN** the assembler closes the active streaming part before inserting the new part

### Requirement: Tool lifecycle correlation preserves one logical tool call

Session transcript assembly SHALL merge tool lifecycle events into one logical tool part even when start and update events use different synthetic and provider ids.

#### Scenario: Synthetic and provider ids resolve to one tool part

- **WHEN** a tool start uses a synthetic transcript-local id and later updates arrive with the provider tool id
- **THEN** the assembler correlates them to the same logical tool part
- **AND** the transcript does not render orphan `unknown` tool rows for that lifecycle

### Requirement: File-changing tools expose normalized diff metadata

The transcript normalization layer SHALL enrich `apply_patch`, `edit`, and `write` tool parts with canonical file-change metadata for downstream rendering.

#### Scenario: File-changing tools provide shared diff contract

- **WHEN** `apply_patch`, `edit`, or `write` runs in a tracked session
- **THEN** the normalized tool metadata includes changed-file summaries and a unified diff string when one can be produced
- **AND** raw tool payloads remain available for audit/debugging
