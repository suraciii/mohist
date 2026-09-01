# Task Log

TaskLog preserves bounded execution evidence for a Task. It explains how work
reached a WorkResult without making high-volume process output part of Workflow
state.

## Design Drivers

- WorkResult answers whether work succeeded. TaskLog explains execution. They
  have separate reliability boundaries.
- Transcript records Agent conversation. Artifacts record produced files.
  Process output has a different owner, retention policy, and access pattern.
- Every line passes one ordering and redaction boundary before buffering. An
  Action cannot choose its own log path or secret-handling rules.
- The log is bounded. On overflow, keep recent error context and continue the
  sequence.

## Model

TaskLog belongs to Runner execution. It may be associated with Workflow or
Agent work, but it is not WorkflowRun state or AgentJob result state.

```text diagram
               +----------------+
               | Task execution |
               +--------+-------+
             +----------+----------+
             v                     v
 +-----------------------+  +------------+
 | ordered/redacted sink |  | WorkResult |
 +-----------+-----------+  +------+-----+
             |                     |
             v                     v
    +----------------+      +------------+
    | bounded buffer |      | work owner |
    +--------+-------+      +------------+
             |
             v
        +---------+
        | TaskLog |
        +----+----+
             |
             v
         +-------+
         | store |
         +-------+
```

The log and result are separate facts. TaskLog may be associated with
`workflow-runs` or `agent-jobs`. Owner, work ID, and sequence define its read
boundary.

```text literal
Work
  status / message / output    # final result
  Artifacts                    # produced files
  AgentSession transcript      # Agent conversation
  TaskLog                      # execution trace
    LogEntry[]
      seq        # monotonic, cursor pagination + jump anchor
      timestamp
      source     # workspace-prep | action:rebase | cleanup | ...
      text
```

stdout and stderr share one ordered stream. `source` preserves a meaningful
execution boundary without giving the operating-system streams separate
product semantics.

## Collection

### Single funnel

All output enters one sink. The sink masks configured secrets before buffering,
assigns a monotonic sequence, records injected time and source, and appends to
the per-work collector.

Collection must capture a final line without a trailing newline, drain readable
output after process exit, and terminate a stuck read when a timeout kills the
process. These are evidence-completeness rules, not an Action-specific logging
API.

### Collector

The per-work collector is append-only between flushes and has bounded
capacity. On overflow, remove the oldest retained entries while sequence
numbers continue to increase. Cursor reads then remain valid and recent failure
context stays available.

## Delivery and Failure Isolation

TaskLog uses a separate upload channel and never enters the WorkResult payload.
A terminal flush sends retained entries before the final work report. Incremental
flushes publish batches for live viewing, but live delivery is best effort and
the store remains authoritative.

Log delivery failure is diagnostic state. It must not rewrite TaskRun or
AgentJob success. A successful WorkResult does not imply that every live log
delta reached every viewer.

### Terminal Ownership

Settlement records which work owns the terminal log: owner kind, owner identity,
work identity, and producing Runner. The store accepts a terminal flush only
when it matches recorded ownership. It never derives ownership from run status,
task order, or task counts. Missing ownership is a missing fact, not an
estimated state.

## Read Contract

Each entry exposes only monotonic `seq`, timestamp, source, and redacted text.
Reads use owner and work identity, with sequence as cursor and jump anchor.
Storage layout and index names are not part of this contract.

The related records have separate meanings:

- Transcript records what the Agent said and did. It belongs to the Session.
- Artifact records which files were produced. It belongs to the Workflow.
- WorkResult records whether work succeeded and its structured result. It
  belongs to the AgentJob.
- TaskLog records execution evidence. It belongs to Runner execution.

## Status

The current store derives terminal ownership heuristically from WorkflowRun
state. The target design requires settlement to persist ownership before
accepting the terminal flush.
