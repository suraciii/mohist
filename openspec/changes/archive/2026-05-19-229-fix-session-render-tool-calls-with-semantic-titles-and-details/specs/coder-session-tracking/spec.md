## MODIFIED Requirements

### Requirement: Transcript normalization preserves semantic tool identity across updates

The persisted coder session transcript SHALL preserve the best available tool family, display title, and semantic details across tool lifecycle updates so replay matches the final live state.

#### Scenario: Late tool updates replace generic titles

- **WHEN** a `tool_call_update` arrives with a more specific title, target, input, output, or metadata than the original `tool_call`
- **THEN** the normalized transcript record refreshes its user-facing semantic fields
- **AND** persisted replay uses the updated values instead of the initial generic label

#### Scenario: Skill and task are inferred from semantic payloads

- **WHEN** provider payloads omit or mislabel the tool family but `title`, `rawInput`, or metadata clearly describe a `skill` or `task`
- **THEN** transcript normalization classifies the tool under that semantic family rather than leaving it as `unknown`

### Requirement: Common tool families persist structured semantic details

The normalized transcript payload SHALL persist structured semantic details for common tool families so live rendering and replay share one display contract.

#### Scenario: Context, execution, planning, interaction, and skill tools keep semantic details

- **WHEN** `read`, `list`, `glob`, `grep`, `search`, `search_files`, `bash`, `shell`, `todowrite`, `todo`, `task`, `question`, `webfetch`, `websearch`, or `skill` tools are normalized
- **THEN** the persisted transcript includes family-appropriate details such as paths, patterns, includes, offsets, limits, commands, cwd, exit codes, todo statuses, subagent metadata, URLs, queries, questions, and loaded skill names when available

#### Scenario: Mutation tools persist reviewable change metadata

- **WHEN** `apply_patch`, `edit`, or `write` tools are normalized
- **THEN** the persisted transcript includes per-file change metadata with file path, operation type, additions/deletions when available, and diff or content payloads when derivable

### Requirement: Live and replay transcript rendering stay semantically equivalent

For the same session data, the live transcript view and the replayed persisted transcript SHALL expose equivalent semantic tool rows and prompt metadata.

#### Scenario: Refresh after live updates preserves semantic rendering

- **WHEN** a session first renders tool rows from live events and the user later refreshes into persisted replay
- **THEN** semantic tool titles, grouped child tool targets, mutation change views, todo visibility, execution summaries, and prompt output-target deduplication remain materially the same
