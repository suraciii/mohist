### Requirement: The collector flushes incrementally in batches during execution

During task execution, the per-work `TaskLogCollector` SHALL flush captured lines to the independent task-log upload channel in incremental batches as the task runs, rather than only once at completion. Flushing SHALL be driven by a flush trigger that fires on an elapsed interval and/or a reached line-count threshold of new lines, accumulating a batch before uploading (攒批). The cadence SHALL accept second-level latency; the runner SHALL NOT issue one upload request per captured line. The flush trigger's timing SHALL be driven by an injectable clock so it is deterministic and testable without wall-clock.

#### Scenario: Lines accumulated during execution are flushed before the task completes

- **WHEN** a task is still executing and the flush trigger fires (interval elapsed or line-count threshold reached)
- **THEN** the collector SHALL upload the accumulated new lines as one batch through the independent task-log channel
- **AND** the upload SHALL happen before the task reaches a terminal state

#### Scenario: One request per line is never issued

- **WHEN** a command emits many lines in quick succession
- **THEN** the runner SHALL coalesce them into batched flushes
- **AND** it SHALL NOT send a separate upload request for each individual line

#### Scenario: Flush timing is deterministic under an injectable clock

- **WHEN** tests drive the collector with a fake clock and trigger
- **THEN** flush firing SHALL be governed by that injected time
- **AND** it SHALL NOT depend on real wall-clock

### Requirement: Each incremental batch carries only newly captured lines via a sent-seq watermark

The collector SHALL track a sent-sequence watermark — the highest `seq` already included in a prior flush — so each incremental batch carries only entries whose `seq` exceeds that watermark. A line that has already been included in a prior increment SHALL NOT be re-sent in a later increment. The watermark SHALL advance as increments are produced.

#### Scenario: A second flush excludes lines sent by the first

- **WHEN** the collector flushes a batch containing lines with seq 1–10, then more lines 11–20 are captured and a second flush fires
- **THEN** the second batch SHALL contain only the new lines (seq 11–20)
- **AND** it SHALL NOT repeat the lines already sent (seq 1–10)

#### Scenario: An empty increment produces no upload

- **WHEN** the flush trigger fires but no new lines have been captured since the last flush
- **THEN** the collector SHALL produce no batch and SHALL issue no upload

### Requirement: A terminal reconciliation batch reconciles the authoritative complete log

On task completion, the runner SHALL emit a terminal batch that serves as the authoritative reconciliation of the complete log for the work item. The terminal batch SHALL be retained from Phase 1's terminal flush and SHALL ensure the authoritative store ends with the complete set of non-discarded lines even when a prior incremental upload failed, timed out, or was dropped. Phase 1's terminal-batch upload behavior SHALL not regress.

#### Scenario: A failed incremental upload is reconciled by the terminal batch

- **WHEN** an incremental flush's upload fails (timeout or network error) and the task then completes
- **THEN** the terminal batch SHALL re-supply the lines the failed increment carried
- **AND** the authoritative store SHALL end with the complete non-discarded log

### Requirement: Capture invariants from Phase 1 are preserved

The runner SHALL preserve Phase 1's capture invariants unchanged under incremental flushing: secret masking happens at the sink entry before any buffering or seq assignment; every line carries a work-scoped monotonic `seq`, timestamp, and source; over-capacity logs truncate by dropping the head and keeping the tail with a `truncated` marker and without reusing discarded seqs; `runCommand`/`git()` line emission loses no line (a missing trailing newline still yields its last line, and pending output is drained after process exit); and the aggregate `CommandResult` return contract for `runCommand`/`git()` SHALL remain unchanged for existing callers.

#### Scenario: Masking, monotonic seq, and head-drop truncation remain in effect

- **WHEN** the runner captures lines during execution under incremental flushing
- **THEN** each buffered line SHALL still be masked at the sink entry, carry a monotonic seq, and be subject to head-drop truncation keeping the tail
- **AND** discarded head seqs SHALL not be reused

#### Scenario: The runCommand/git() aggregate return and no-loss guarantees hold

- **WHEN** an ops command runs with line-by-line forwarding to the sink under incremental flushing
- **THEN** no line SHALL be lost across the process close boundary
- **AND** the aggregate CommandResult SHALL still contain the complete stdout/stderr for existing callers

### Requirement: Task-log uploads remain best-effort and never block the report or task execution

A failed incremental or terminal task-log upload SHALL be logged and swallowed. It SHALL NEVER block, fail, or delay the task's execution or the subsequent `report`, which carries the verdict. The verdict-bearing report SHALL take precedence over the best-effort log channel.

#### Scenario: An incremental upload failure does not stop execution or the report

- **WHEN** an incremental task-log upload fails while the task is still running
- **THEN** the failure SHALL be logged and swallowed
- **AND** the task SHALL continue executing and the terminal report SHALL still be delivered
