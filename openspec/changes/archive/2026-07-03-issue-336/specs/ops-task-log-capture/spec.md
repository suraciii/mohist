### Requirement: A single sink funnels all ops command output

The runner SHALL expose one unified capture sink (`ActionContext.log.write(source, text)`) through which every ops command's output (git, shell) flows, replacing the current pattern where each call site assembles its own `combinedOutput` string that never leaves the process. The sink SHALL return the assigned line sequence number. Executor phases — workspace preparation, branch-stability checks, the action body, and clean-worktree enforcement — SHALL emit their output through this sink, tagged with a `source` that identifies the phase (e.g. `workspace-prep`, `branch-check`, `action:<name>`, `cleanup`). No ops output SHALL reach buffering, upload, or display by bypassing the sink, because any bypass either leaks a secret the masker missed, drops a sequence number, or records a line twice.

#### Scenario: Workspace preparation output is tagged workspace-prep

- **WHEN** an ops task's workspace is prepared (git clone/checkout) and the command emits stdout/stderr
- **THEN** the sink SHALL receive the output tagged with source `workspace-prep`
- **AND** the output SHALL NOT be silently discarded as it is before this change

#### Scenario: Branch-stability and cleanup phases tag their own source

- **WHEN** a branch-stability check or a clean-worktree enforcement runs a git command
- **THEN** the sink SHALL receive that command's output tagged with a distinct source (`branch-check` or `cleanup`)
- **AND** the source SHALL be distinguishable from the action body's output

#### Scenario: The action body forwards command output line-by-line

- **WHEN** the action body runs an ops command (e.g. git rebase, git push, openspec, health-check)
- **THEN** the command's stdout/stderr SHALL be forwarded to the sink line-by-line tagged with an action source
- **AND** the captured lines SHALL include the text that explains a failure (conflicting commit, file, git error)

### Requirement: runCommand emits merged output line-by-line with no-loss guarantees

`runCommand` (`system/process.ts`) SHALL accept an optional `onLine` callback that emits child process output **merged** line-by-line, with no stream dimension — stdout and stderr SHALL flow through the same callback sharing one line-number sequence. The emitter SHALL preserve a line even when the child omits a trailing newline, and SHALL perform a post-exit drain so no pending output is lost when the process closes. The existing aggregate return contract (the `CommandResult` with `stdout`/`stderr`/`exitCode`) SHALL remain unchanged, so current call sites that consume the full aggregate output do not regress.

#### Scenario: A command without a trailing newline still yields its last line

- **WHEN** a child process writes output that does not end with a newline and then exits
- **THEN** the `onLine` callback SHALL still receive that final line
- **AND** the line SHALL NOT be dropped

#### Scenario: Pending output is drained after the process exits

- **WHEN** a child process exits while buffered output has not yet been delivered to `onLine`
- **THEN** the emitter SHALL drain the remaining output once after exit before resolving
- **AND** no pending line SHALL be lost across the close boundary

#### Scenario: The aggregate return contract is preserved for existing callers

- **WHEN** an existing call site invokes `runCommand` or `git()` without depending on the new callback
- **THEN** the returned `CommandResult` SHALL still contain the complete `stdout` and `stderr`
- **AND** the caller's behavior SHALL not regress

### Requirement: Secret masking happens at the sink entry, before buffering

The sink SHALL mask known credential patterns (e.g. credentials embedded in git remote URLs) before the text is assigned a sequence number, appended to any buffer, uploaded, or displayed. Masked data SHALL be the only form that ever leaves the sink, so there is no window where unmasked output is already persisted. Masking SHALL cover all output sources because the sink is the single funnel.

#### Scenario: A credential in git remote output is masked before it is buffered

- **WHEN** an ops command emits a line containing an embedded credential (e.g. a git remote URL with credentials)
- **THEN** the sink SHALL replace the credential pattern with a mask before the line is buffered or assigned a seq
- **AND** neither the buffered, uploaded, nor displayed representation SHALL contain the raw credential

### Requirement: Each line carries a monotonic sequence number, timestamp, and source

Every log entry produced by the sink SHALL carry a sequence number that is monotonically increasing within the work item, a timestamp, and a source label. The sequence number SHALL be the canonical line-ordering key and SHALL remain stable so that cursor pagination and ordering are deterministic.

#### Scenario: Sequence numbers are monotonic across phases

- **WHEN** the sink writes lines from several executor phases for one work item
- **THEN** each successive line SHALL receive a sequence number greater than the previous one
- **AND** the order SHALL reflect the order the lines were written

### Requirement: A per-work collector buffers entries and flushes once on completion (Phase 1 terminal batch)

Each work item SHALL own a `TaskLogCollector` that buffers log entries (producer appends only) and flushes them as a terminal batch when the task completes. Phase 1 SHALL NOT require real-time streaming during execution; the complete log SHALL become available once the task finishes. Flushing SHALL upload the buffered entries through the independent task-log channel.

#### Scenario: The full log is uploaded once when the task completes

- **WHEN** an ops task finishes (success or failure)
- **THEN** the collector SHALL flush its buffered entries as a single terminal batch through the independent upload channel
- **AND** the flushed batch SHALL contain every non-discarded line captured during execution

### Requirement: Over-capacity logs truncate by dropping the head and keeping the tail

When a single task's captured log exceeds the capacity limit, the runner SHALL truncate by dropping the oldest (head) lines and keeping the most recent (tail) lines, because the error context that locates a failure lives at the tail. The truncation SHALL be marked with a `truncated` indicator. Sequence numbers belonging to discarded head lines SHALL NOT be reused, so the remaining sequence numbers stay monotonic and contiguous in value, keeping cursor pagination stable.

#### Scenario: A log exceeding the limit keeps the tail and is marked truncated

- **WHEN** a task captures more lines than the capacity limit allows
- **THEN** the runner SHALL discard the oldest head lines and retain the most recent tail lines
- **AND** the result SHALL carry a `truncated` marker
- **AND** the retained lines' sequence numbers SHALL be greater than every discarded line's sequence number

#### Scenario: Discarded sequence numbers are not reused

- **WHEN** head lines are dropped during truncation
- **THEN** the sequence numbers of the retained tail lines SHALL NOT restart or overlap the discarded numbers
- **AND** ordering by sequence number SHALL remain stable
