### Requirement: Streamed assistant text preserves the agent's original paragraph structure

Accumulating streamed assistant text deltas into a transcript text part MUST preserve the agent's original paragraph and sentence boundaries. The accumulated text SHALL equal the exact, lossless concatenation of the streamed deltas in arrival order — no delta dropped, reordered, or having its whitespace altered — so that every paragraph boundary present in the stream is retained.

#### Scenario: Paragraph boundary spanning two deltas is preserved

- **WHEN** two consecutive text deltas are appended to the same open text part and a paragraph boundary in the agent's output falls across the two deltas
- **THEN** the accumulated text preserves that boundary so the rendered output shows the paragraphs separately rather than fused into a run-on (e.g. never "usage:Let me")

#### Scenario: Lossless delta concatenation

- **WHEN** a sequence of text deltas is appended to an open text part
- **THEN** the resulting text equals the deltas concatenated in arrival order with no characters lost, added, reordered, or whitespace stripped

### Requirement: Interrupted-and-resumed text keeps its segment boundaries

When a reasoning delta interrupts an open text part (closing it) and assistant text later resumes in a new text part, the boundary between the earlier text segment and the later text segment MUST be preserved. The two segments SHALL render as distinct blocks with their original content and boundaries, never fused into a single merged paragraph.

#### Scenario: Text resumes after a reasoning block

- **WHEN** an open text part is closed by an intervening reasoning delta and a subsequent text delta opens a new text part
- **THEN** the earlier text segment and the later text segment render as separate blocks and their paragraph boundaries match the agent's original output

### Requirement: Rendered assistant text matches the source output

The assistant text rendered in the transcript SHALL match the agent's original output paragraph structure. Cross-paragraph or cross-sentence fusion MUST NOT occur anywhere in the rendered assistant text.

#### Scenario: Multi-paragraph assistant output renders with intact boundaries

- **WHEN** the agent's original output contains multiple paragraphs and that output is streamed into the transcript
- **THEN** the rendered assistant text displays each paragraph as a distinct paragraph with no adjacent paragraphs merged
