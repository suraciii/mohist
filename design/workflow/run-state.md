# WorkflowRun State Persistence

WorkflowRun persists neutral WorkflowActionAttempt orchestration records. Agent-backed attempts retain only
AgentJob and AgentSession references. AgentJob owns execution state.

This document defines the content boundary and read/write cost rules for persisted WorkflowRun State. Dispatch
snapshot semantics and storage lifecycle are defined in [`task-dispatch.md`](task-dispatch.md).

## Design Drivers

WorkflowRun State is one authoritative decision record that is loaded and rewritten after state changes. Every
unnecessary byte increases observation and progress cost. Three boundaries follow:

- Current decision facts belong in State. Traceable history belongs in events.
- Redeliverable execution input belongs in a short-lived attempt snapshot. Copying it into State makes old
  attempts increase unrelated decision cost.
- Format compatibility belongs at startup migration. Historical shapes must not branch through the live read
  path.

The size budget is a correctness boundary, not only a storage optimization. An unbounded State record can make
observation and progress compete for memory and write capacity.

## Model

State is the persisted authority for one WorkflowRun. It contains only runtime facts required for current
scheduling, retry, recovery, and presentation decisions. The WorkflowRun state owner loads it when active and
rewrites it after each state change.

State is not:

- History. Event storage owns traceable history.
- The dispatch contract. Dispatch snapshots follow [`task-dispatch.md`](task-dispatch.md) and do not enter State.
- Large content. Prompt bodies, complete task-output aggregation, and dispatch payloads are rebuildable or
  referenced and are not copied into State.

## Semantics

### Content Boundary

Every State field must answer a current question about scheduling, retry, recovery, locking, or presentation.
A field retained only for possible future use does not enter State.

Content that grows with task or retry count must be budgeted per WorkflowActionAttempt. An attempt carries
only fields required for its own decisions. It does not duplicate shared data such as every Prompt or all
earlier task output.

A superseded or terminal attempt does not retain a dispatch snapshot. [`recovery.md`](recovery.md) determines
recovery-chain attempt count. State adds no historical limit. Enforce size through the content boundary, not
truncation.

A normal active run must remain within hundreds of KiB. A State record above 1 MiB violates the content
boundary and is a defect, not a capacity request.

WorkflowRun State retains an Approval Feedback list capped at 10 entries total. An open feedback consumes one
slot. Eviction removes the oldest resolved entries and never an open entry. Rerun and `rerun-from-stage` discard stale
open feedback obligations when they replace or reset stage execution. Request facts remain in events.

Resolved feedback beyond the window is not archived in full. Events retain request and task-completion facts,
but resolution details of evicted cycles are not reconstructable. [`definition.md`](definition.md#approval-feedback)
defines Approval Feedback and Feedback Tasks. This document defines only their State boundary.

### Read and Write Cost

The established shape rewrites complete State on every state change. State size multiplied by event frequency
is write amplification. The content boundary is the only control for it.

A complete read by run ID for report, dispatch, log, or control-plane status pays complete deserialization
cost. Callers must not use it as a cheap metadata query. A query needing only status or another scalar uses
projected columns without deserializing State.

Legacy JSON migration is a write-time obligation. A read path must not parse the complete document to probe
for migration.

### Format Evolution and Migration

A running Server recognizes one canonical State format. A per-record discriminator would create permanent
branching for a one-way startup upgrade, so multiple formats must not coexist on the read path.

A State format change is a database upgrade completed before the new Server accepts requests. The
verification-command cutover does not rewrite persisted task-attempt properties or a bound Workflow
Definition. Unknown historical properties are ignored by the current model. Existing active Runs without a
bound Definition are drained or stopped operationally before deployment. Terminal historical rows remain
readable.

```text diagram
                              yes  +--------+             +-----------+
                             +---->| Commit +------------>| Canonical |
+-----------+      +--------+|     +--------+             +-----------+
| Preflight +----->| Valid? ++
+-----------+      +--------+|no   +---------------+
                             +---->| Startup fails |
                                   +---------------+
```

Database initialization is the sole upgrade entry point:

- EF migration owns schema changes and unambiguous transformations expressible with SQLite JSON.
- An ordered, idempotent C# data upgrader in the same initialization owns transformations requiring Workflow
  semantics, structural comparison, or rejection of ambiguity.
- A rule must not be duplicated in SQL and C#.
- Pending EF migrations and data upgraders run in order across releases. Each migration converges one way.
  Historical support remains in cold-start migration and does not enter the current read model.

A data upgrader first performs a read-only preflight. It finds all candidates, converts them through one rule,
and deserializes each result with the current model. If any row is ambiguous or unreadable, it writes no
State. Startup fails and identifies the WorkflowRun.

After preflight succeeds, one database transaction writes all converted rows and increments each affected
ETag. Conversion does not filter by WorkflowRun lifecycle. `failed` supports retry and rerun and must migrate
without semantic change. A canonical row must not be rewritten.

A data upgrader is idempotent. A failed write rolls back with the transaction. The next startup reevaluates
persisted data and retries without process-memory progress. State migration is idempotent by persisted bytes
and must not rewrite a canonical record or advance its revision when content is unchanged.

Before a destructive production-State rewrite, create and verify a consistent database backup. Restore from
that backup instead of inventing a reverse transformation.

After migration, every read entry deserializes State directly with the current model. It neither probes
historical fields nor calls a converter. A database with incomplete migration cannot enter the service phase.
Acceptance requires zero historical-conversion calls on the read path. Migration code may remain at the
cold-start boundary while old database upgrade remains supported.

### Historical Interruption Compatibility

Canonical State has no interrupted Action status, task or Stage `WorkInterruption`, or `recoverable-interrupted` projection. These
shapes are legacy startup-migration input only. They are not a live recovery mechanism and cannot authorize
Runner redelivery, AgentJob launch, replacement execution, or a new deadline.

The cold-start upgrader classifies legacy status and interruption fields from raw JSON before normal
deserialization. It may remove an interruption or map an Action attempt to `Failed` only when persisted
terminal facts identify that exact owning attempt or checks batch. A terminal WorkflowRun or Stage alone is
not proof.

An active, mismatched, malformed, or otherwise ambiguous row fails startup with zero writes. The upgrader must
not invent a result, reason, timestamp, event, or state transition.

Removing these fields from current State does not remove stored `TaskInterrupted` or `ChecksInterrupted` events. They remain
readable history under [`event-protocol.md`](../event-protocol.md#historical-workflow-interruption-events) and never
reconstruct current State.

## Status

Dispatch snapshots remain external to WorkflowRun State and follow [`task-dispatch.md`](task-dispatch.md). List and
status reads use small projections and a versioned status cache. A cold-start upgrader converts historical
State before service traffic, so normal reads do not carry a legacy converter. Retention for events,
transcripts, and telemetry remains a database-wide lifecycle concern and does not expand WorkflowRun State or
its read path.
