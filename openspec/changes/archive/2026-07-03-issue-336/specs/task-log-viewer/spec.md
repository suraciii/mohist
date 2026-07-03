### Requirement: The task expand area renders the execution log line-by-line

The Web's task expand area SHALL render a captured ops task's execution log line-by-line, so a failed task's real command output is readable without leaving the UI. Each rendered line SHALL display its source label and timestamp alongside the text, and the log region SHALL be scrollable so a long log can be navigated to the failing command's output. The panel SHALL fetch the log via the issue-path query endpoint keyed by the task the user expanded.

#### Scenario: A failed ops task exposes its real command output

- **WHEN** a deliberately failing ops task (e.g. a rebase conflict) is expanded in the task progress panel
- **THEN** the expand area SHALL render the captured execution log line-by-line
- **AND** the user SHALL be able to scroll to the failing command's actual output (conflicting commit, file, git error)

#### Scenario: Each line shows its source and timestamp

- **WHEN** the log panel renders a line
- **THEN** the line SHALL display its source label and timestamp in addition to the text
- **AND** the source SHALL let the user tell apart phases (e.g. `workspace-prep` vs `action:rebase` vs `cleanup`)

### Requirement: The log panel reflects truncation when head lines were dropped

When a task's log was truncated at capture time (head dropped, tail kept), the panel SHALL surface the `truncated` status so the user understands the visible lines are the retained tail and earlier lines are absent. The retained tail — which contains the error context — SHALL be fully rendered and scrollable.

#### Scenario: A truncated log is shown with a truncation indicator

- **WHEN** the user expands a task whose log response reports `truncated` as true
- **THEN** the panel SHALL render the retained tail lines and SHALL indicate that earlier lines were truncated
- **AND** the error-bearing tail SHALL be visible without leaving the UI

### Requirement: Existing task status, message, and output rendering are unchanged

The task log panel SHALL be an addition to the task expand area. The existing rendering of task status, message, structured output JSON, and failure-kind guidance SHALL remain unchanged — the log is a fourth class of evidence, not a replacement for the terminal verdict or the structured conclusion.

#### Scenario: Task status and message rendering are preserved

- **WHEN** a task is rendered after this change
- **THEN** the task's status icon, title, message, and structured output SHALL render exactly as before
- **AND** the log panel SHALL appear in addition to, not in place of, those elements
