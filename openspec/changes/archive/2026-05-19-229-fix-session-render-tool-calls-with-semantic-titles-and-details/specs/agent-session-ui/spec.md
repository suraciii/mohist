## MODIFIED Requirements

### Requirement: Session transcript renders semantic tool rows

The session transcript page SHALL render tool calls using the most specific semantic title and summary available instead of a generic tool-family label.

#### Scenario: Skill tool shows loaded skill name

- **WHEN** a completed `skill` tool call has a loaded skill name in normalized title, input, or metadata
- **THEN** the transcript row shows that skill name, such as `Loaded skill: software-design`, instead of only `skill` or `unknown`

#### Scenario: Context group preserves child tool targets

- **WHEN** context-gathering tools are grouped in the transcript
- **THEN** the collapsed group may show an aggregate summary
- **AND** expanding the group shows per-tool semantic targets for `read`, `list`, `glob`, `grep`, `search`, and `search_files`

#### Scenario: Execution and delegation tools show semantic summaries

- **WHEN** the transcript renders `bash`, `shell`, `task`, `question`, `webfetch`, `websearch`, `todowrite`, or `todo`
- **THEN** each row shows semantic summaries such as command, cwd, exit code, subagent description, URL, query, or todo progress before any raw JSON fallback

### Requirement: Mutation tools render reviewable change content

Transcript rows for file-changing tools SHALL expose reviewable file-level changes as the primary expanded view.

#### Scenario: apply_patch renders per-file diffs

- **WHEN** an `apply_patch` tool call includes `patchText` or normalized patch metadata
- **THEN** the expanded tool view shows affected files with operation type and additions/deletions when available
- **AND** each file entry exposes an expandable diff body

#### Scenario: edit and write render semantic change views

- **WHEN** an `edit` or `write` tool call includes file target and before/after or diff metadata
- **THEN** the expanded tool view shows the target file plus a diff or written-content view
- **AND** raw JSON is shown only as a fallback when no semantic change representation can be derived

### Requirement: Prompt metadata avoids duplicate output target lines

The prompt card SHALL display one canonical output-target line when prompt subtitle and output-path metadata describe the same location.

#### Scenario: Duplicate output target is collapsed

- **WHEN** prompt summary metadata contains both `outputPath` and a subtitle equivalent to `Output: <same path>`
- **THEN** the transcript page shows that output target once
- **AND** no duplicate output-path line is rendered in the prompt block
