### Requirement: Consecutive exploratory tool calls collapse into one grouped row

Two or more consecutive exploratory tool calls (read, grep, glob, search, and equivalent context-gathering tools) within a turn SHALL be merged into a single expandable grouped row, rather than rendered as one row per call. A lone exploratory call (no adjacent exploratory call) SHALL render as a standard tool row and SHALL NOT be wrapped in a grouped row.

#### Scenario: A run of consecutive reads and searches merges
- **WHEN** a turn contains two or more consecutive exploratory tool calls (for example several reads followed by several greps)
- **THEN** those calls SHALL be merged into a single expandable grouped row
- **AND** SHALL NOT be rendered as one separate row per call

#### Scenario: A single exploratory call is not grouped
- **WHEN** a turn contains an exploratory tool call with no adjacent exploratory call
- **THEN** the call SHALL render as a standard tool row
- **AND** SHALL NOT be wrapped in a grouped row

#### Scenario: Grouping is interrupted by non-exploratory calls
- **WHEN** a run of exploratory calls is interrupted by a non-exploratory tool call (for example a bash or edit call)
- **THEN** the exploratory calls before and after the interruption SHALL form separate grouped rows
- **AND** the interrupting non-exploratory call SHALL render as its own standard tool row

### Requirement: The grouped row summarizes the batch on its collapsed line

The collapsed grouped row SHALL summarize the batch in a single line, including a count of calls by action type (for example "5 reads · 3 searches · 2 globs"). The summary SHALL be recognizable as an exploratory/gathering action and SHALL reflect the actual composition of the merged calls.

#### Scenario: Collapsed group shows per-type counts
- **WHEN** a grouped row containing five reads, three searches, and two globs renders collapsed
- **THEN** the row summary line SHALL include the per-type counts of the merged calls
- **AND** SHALL summarize the batch as a single exploratory action

### Requirement: Individual calls are revealed on expand

Expanding a grouped row SHALL reveal the individual tool calls that were merged, each rendered as a standard tool row (subject to the same status, title, parameter, duration, and expand-to-detail behavior as a standalone tool row). A grouped row that contains a failed call SHALL signal the failure on its collapsed summary line.

#### Scenario: Expanding a group reveals individual rows
- **WHEN** a user expands a collapsed grouped row
- **THEN** the individual exploratory tool calls SHALL be revealed, each as a tool row
- **AND** each revealed row SHALL behave the same as a standalone tool row (including expand-to-detail)

#### Scenario: A grouped row containing a failure signals it on the summary
- **WHEN** a grouped row contains at least one tool call whose state is failed
- **THEN** the collapsed summary line SHALL signal the failure (for example with a danger treatment or failed label)
- **AND** SHALL NOT require expansion to indicate that a failure occurred within the batch
