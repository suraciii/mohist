# Task Log

TaskLog preserves the execution evidence needed to explain a Task without
turning high-volume process output into Workflow state.

## Design Drivers

- A WorkResult answers whether work succeeded. A TaskLog explains how execution
  reached that result. Combining them would make a large or failed log upload
  threaten result reliability.
- Transcript records what an Agent said, and Artifacts record files it produced.
  Process output has a different owner, retention policy, and access pattern.
- Every line must pass one ordering and redaction boundary before buffering.
  Allowing each Action to invent a log path would make sequence and secret
  handling inconsistent.
- Logs are bounded evidence. When the cap is reached, recent error context is
  more useful than early setup chatter, so retention keeps the tail.

## Boundary

TaskLog belongs to Runner execution. It is associated with Workflow or Agent
work but never stored inside WorkflowRun or AgentJob result state.

```text
task execution -> ordered/redacted log sink -> bounded buffer -> TaskLog channel -> store
       |
       +---------------------------------------> WorkResult channel -> work owner
```

The two channels are separate facts. TaskLog may be associated with
`workflow-runs` or `agent-jobs`; owner, work ID, and sequence identify its read
boundary.

## Model

```text
Work
 |-- status / message / output       <- final result
 |-- Artifacts                       <- produced files
 |-- AgentSession transcript         <- Agent conversation
 `-- TaskLog                         <- execution trace
      `-- LogEntry[]
           |-- seq        monotonic, cursor pagination + jump anchor
           |-- timestamp
           |-- source     workspace-prep | action:rebase | cleanup | ...
           `-- text
```

stdout and stderr share one ordered stream. `source` preserves the meaningful
execution boundary without claiming that operating-system streams have separate
product semantics.

## Collection

### Single funnel

All output enters one sink. The sink masks configured secrets before text can
enter a buffer, assigns a monotonic sequence, records injected time and source,
and appends to the per-work collector.

Process collection must capture the final line even without a trailing newline,
drain readable output after process exit, and terminate a stuck read when the
process timeout kills execution. These are evidence-completeness rules, not an
Action-specific logging API.

### Collector

The per-work collector is append-only between flushes. Its capacity is bounded;
overflow removes the oldest retained entries while sequence numbers continue
to increase. This makes truncation compatible with cursor reads and preserves
the most recent failure context.

## Delivery and Failure Isolation

TaskLog uses a separate upload channel and never enters the WorkResult payload.

| Phase | When |
|---|---|
| Terminal flush | Flush retained entries before the final work report so completed work has its available evidence. |
| Incremental flush | Periodically publish batches for live viewing; live delivery is best effort and the store remains authoritative. |

Log delivery failure is diagnostic state and must not rewrite TaskRun or
AgentJob success. Conversely, a successful WorkResult does not imply that every
live log delta reached every viewer.

## Read Contract

Each entry exposes only monotonic `seq`, timestamp, source, and redacted text.
Reads are scoped by owner and work identity and use sequence as the cursor and
jump anchor. Storage layout and index names are implementation details; the
durable contract is ordered, bounded, redacted evidence with monotonic sequence.

## Relationship to existing

| Concept | Answers | Domain |
|---|---|---|
| TaskLog | Why did execution reach this result? | Runner execution |
| Transcript | What did the Agent say and do conversationally? | Session |
| Artifact | Which files were produced? | Workflow |
| WorkResult | Did the work succeed, and what structured result did it return? | TaskRun or AgentJob |
