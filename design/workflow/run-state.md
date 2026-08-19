# WorkflowRun State Persistence

This document defines the content boundary and read/write cost rules for
persisted WorkflowRun state. See
[`task-dispatch.md`](task-dispatch.md) for dispatch-snapshot semantics and
storage lifecycle.

## Design Drivers

WorkflowRun state is read and rewritten as one authoritative decision record. That gives one owner
an unambiguous view of scheduling and recovery, but it also makes every unnecessary byte part of
the cost of every state transition. Three boundaries follow:

- Current decision facts belong in State; traceable history belongs in events.
- Redeliverable execution input belongs in a short-lived attempt snapshot; copying it into State
  makes old attempts increase the cost of unrelated current decisions.
- Format compatibility belongs at startup migration; carrying several historical shapes through
  the live read path makes every request pay for an upgrade that should happen once.

The size budget below is therefore a correctness boundary, not only a storage optimization. An
unbounded State record can make observation and progress compete for the same memory and write
capacity.

## Definition

State is the persisted authority for one WorkflowRun. It contains only the
runtime facts required for current scheduling, recovery, and presentation
decisions. The WorkflowRun state owner loads this record when it becomes active
and rewrites it after each state change.

State is not:

- History. Event storage owns traceable history.
- A repository for the dispatch contract. Dispatch snapshots follow
  [`task-dispatch.md`](task-dispatch.md) and do not enter State.
- Storage for large content. Rebuildable or referenced content such as Prompt
  body, complete task-output aggregation, and dispatch payload is not copied
  into State.

## Content Boundary

- Every field in State must answer a current decision question about scheduling,
  retry, recovery, lock, or state presentation. A field retained only for
  possible future use does not enter State.
- Content that grows with task or retry count must be budgeted per TaskRun. One
  TaskRun carries only fields required for its own decisions and no complete
  duplicate shared data such as every Prompt or all earlier task output.
- A superseded or terminal attempt does not retain a dispatch snapshot.
  [`recovery.md`](recovery.md) determines the attempt count in a recovery chain.
  State adds no historical limit. Enforce size through the content boundary,
  not truncation.
- A normal active run must remain within hundreds of KiB. A State record above
  1 MiB violates the content boundary and is a defect, not a capacity request.

## Read and Write Cost

- **Write:** The established shape rewrites complete State on every state
  change. State size multiplied by event frequency is write amplification. The
  content boundary is the only control for it.
- **Read:** A complete read by run ID for report, dispatch, log, or control-plane
  status pays the complete deserialization cost. Callers must not use it as a
  cheap metadata query. A query needing only status or another scalar must use
  projected columns without deserializing State.
- Legacy JSON migration is a write-time obligation, not a read-time obligation.
  A read path must not parse the complete document to probe for migration.

## Format Evolution and Migration

A running Server recognizes one canonical State format. A per-record format
discriminator would create permanent branching for a one-way startup upgrade,
so multiple formats must not coexist on the read path.

A State format change is a database upgrade completed before the new Server
accepts requests:

- Database initialization is the sole upgrade entry point. EF migration owns
  schema changes and unambiguous transformations expressible with SQLite JSON.
  An ordered, idempotent C# data upgrader in the same initialization owns
  transformations requiring Workflow semantics, structural comparison, or
  rejection of ambiguity. Do not duplicate a rule in SQL and C#.
- Across several release versions, pending EF migrations and data upgraders run
  in order to reach the current format. Each migration converges one way.
  Historical support remains in cold-start migration and does not enter the
  current read model.
- A data upgrader first performs a read-only preflight. It finds all candidates,
  converts them through one rule, and deserializes each result with the current
  model. If any row is ambiguous or unreadable, it writes no State. Server
  startup fails and identifies the WorkflowRun.
- After preflight succeeds, one database transaction writes all converted rows
  and increments each affected ETag. The conversion does not filter by
  WorkflowRun lifecycle. `failed` supports retry and rerun and must migrate
  without semantic change. A row already in canonical format must not be
  rewritten.
- A data upgrader must be idempotent. A failed write rolls back with the
  transaction. The next startup reevaluates persisted data and retries without
  process-memory progress.
- WorkflowRun State migration must be idempotent by persisted bytes. It must not
  rewrite a canonical record or advance its revision when the content does not
  change.

Before a destructive production-State rewrite, create and verify a consistent
database backup. Restore from that backup instead of inventing a reverse
transformation.

After migration, every read entry deserializes State directly with the current
model. It neither probes historical fields nor calls a converter. A database
with incomplete migration cannot enter the service phase. Acceptance requires
zero historical-conversion calls on the read path. Migration code can remain at
the cold-start boundary while old database upgrade remains supported.

## Status

Dispatch snapshots are external to WorkflowRun State and follow the lifecycle
defined in [`task-dispatch.md`](task-dispatch.md). List and status reads use
small projections and a versioned status cache. A cold-start upgrader converts
historical State before the service accepts traffic, so normal reads do not
carry a legacy converter. These boundaries keep arbitration and observation
cost proportional to current facts rather than execution history.

Retention for events, transcripts, and telemetry is a database-wide lifecycle
concern. It must not expand WorkflowRun State or its read path.
