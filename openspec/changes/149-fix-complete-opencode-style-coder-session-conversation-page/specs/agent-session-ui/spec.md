## MODIFIED Requirements

### Requirement: Session page renders readable Mohist/Coder conversation

The dedicated session page SHALL render a read-only opencode-style conversation where Mohist prompts, Coder responses, reasoning, tools, errors, and conclusions are readable in order.

#### Scenario: Prompt summary defaults over raw prompt

- **WHEN** a Mohist prompt has summary metadata or inferable structured sections
- **THEN** the page shows the readable summary by default
- **AND** the full raw prompt is collapsed behind `Show full prompt`

#### Scenario: Speaker labels are explicit

- **WHEN** the transcript renders turns and assistant parts
- **THEN** user-visible labels distinguish `Mohist` and `Coder` rather than generic task or turn labels

### Requirement: Context and todo tools use progressive disclosure

Consecutive context-gathering tools SHALL be grouped to reduce transcript noise while preserving raw details.

#### Scenario: Context tools are grouped

- **WHEN** consecutive read, glob, grep, list, membrowse, memread, or memsearch tools appear within a turn
- **THEN** they render as a `Context gathered` group with human-readable counts
- **AND** expanding the group shows each tool's details

#### Scenario: Todo updates are summarized

- **WHEN** a todowrite tool appears
- **THEN** it renders as an `Updated todo list` summary by default
- **AND** todo details are available on expansion

### Requirement: File-changing tools render file-level results

File-changing tools SHALL render created, modified, deleted, or moved file summaries before raw patch details.

#### Scenario: apply_patch displays file summary

- **WHEN** an apply_patch/edit/write tool has changed-file metadata or inferable patch data
- **THEN** the default card shows file paths, operations, and additions/deletions where available
- **AND** raw patch/input/output remains expandable

### Requirement: Session header and states are user-facing

The session page SHALL communicate user-facing session state and transcript context in the header and empty/error states.

#### Scenario: Header communicates transcript status

- **WHEN** the session detail is loaded
- **THEN** the header shows issue context, stage, model, turn count, last activity, changed files or artifact summary, and live/finalizing/completed/failed/stale state where applicable

#### Scenario: Loading and error states are distinct

- **WHEN** the page is loading, waiting for first activity, missing legacy prompt data, empty, or has an API error
- **THEN** each state has distinct user-visible wording

### Requirement: Live scrolling respects reader position

Live transcript updates SHALL not force-scroll users away from historical content they are reading.

#### Scenario: New content while away from bottom

- **WHEN** text, tool updates, recovery updates, or completion events arrive while the user is not near the bottom
- **THEN** the page does not force-scroll
- **AND** a jump-to-bottom affordance appears and restores follow mode when clicked
