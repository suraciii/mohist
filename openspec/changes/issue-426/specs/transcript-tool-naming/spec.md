### Requirement: Tool call display title is never the literal "unknown"

Every tool call rendered in the transcript SHALL present a display title that is a recognizable, human-readable string. The literal string "unknown" MUST NOT appear as a tool call's display title under any circumstance — not as the primary label, not as an accessible name, and not as a subtitle that becomes the visible title.

#### Scenario: Tool call with a known semantic name

- **WHEN** a tool call carries a known tool name (e.g. `bash`, `read`, `edit`, `grep`)
- **THEN** the transcript renders a display title that identifies the tool and, where applicable, its target (e.g. the command text, the file path, the search query)

#### Scenario: No tool call renders the literal unknown

- **WHEN** any tool call is rendered in the transcript
- **THEN** its visible display title is never the literal string "unknown"

### Requirement: Display title is inferred from input when the tool name is missing

When the tool name is missing or equals "unknown", the title derivation SHALL infer a recognizable title from the call's title and raw input fields. The derivation MUST inspect the semantically meaningful fields of the input — command/script, file path, search/query/pattern, url/uri, patch text, todos, delegation/subagent markers, and skill markers — and surface recognizable content as the title.

#### Scenario: Missing name but input carries a command

- **WHEN** the tool name is missing or "unknown" and the raw input contains a command/script field
- **THEN** the display title surfaces the command (e.g. derives a `bash` semantic name and shows the command text)

#### Scenario: Missing name but input carries a file path

- **WHEN** the tool name is missing or "unknown" and the raw input contains a file path
- **THEN** the display title surfaces the path (or its basename) so the user can see what file the tool targeted

#### Scenario: Missing name but input carries a search query

- **WHEN** the tool name is missing or "unknown" and the raw input contains a search/query/pattern field
- **THEN** the display title surfaces the query string

#### Scenario: Missing name but input carries a url

- **WHEN** the tool name is missing or "unknown" and the raw input contains a url/uri
- **THEN** the display title surfaces the url

#### Scenario: Title text reveals the tool family

- **WHEN** the tool name is missing or "unknown" but the call title or input text reveals a tool family (e.g. "apply_patch", "Loaded skill:", delegation/subagent markers)
- **THEN** the derivation resolves a semantic tool name from that text and renders the corresponding recognizable title

### Requirement: Last-resort title is a generic descriptive label

When no semantic tool name can be inferred and no recognizable content can be extracted from the title or raw input, the display title SHALL fall back to a generic, human-readable descriptive label. It MUST NOT fall back to "unknown".

#### Scenario: Unidentifiable tool still gets a readable title

- **WHEN** the tool name is missing/"unknown" and the title and raw input yield no recognizable content
- **THEN** the display title is a generic descriptive label (e.g. "Tool call") that remains readable and is never "unknown"

### Requirement: An upstream collection gap is escalated, not patched in display

If investigation shows the "unknown" originates from the event collection/reflow pipeline (the name/title/input never arrived) rather than the display layer, that gap SHALL be recorded as evidence and escalated to a separate issue. The display layer MUST NOT be modified to compensate for a missing collection-side field within this change.

#### Scenario: Root cause traced to collection side

- **WHEN** the missing name is caused by the event collection/reflow pipeline not carrying the name/title/input at all
- **THEN** the gap is recorded with evidence and a separate issue is opened, and the transcript display layer is not changed to paper over the missing field
