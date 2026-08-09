# WorkflowRun State Persistence

This document defines the content boundary and read/write cost rules for
persisted WorkflowRun state in `WorkflowRuns.State`. See
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

State is the persisted authority for one WorkflowRun. It contains the minimal
runtime facts required for current decisions: status, assignment, state-machine
fields for each Stage and TaskRun, Workspace and Repository references, and
task output. The WorkflowRun state owner loads this record when it becomes
active and rewrites it after each state change.

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

A running Server recognizes one canonical State format. Do not add per-row
`SchemaVersion` or `StateSchemaVersion` to `WorkflowRuns`. One writer and
startup-time database migration do not require permanent format branching, and
multiple formats must not coexist on the read path.

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
- WorkflowRun State migration must be idempotent by persisted bytes. Do not
  reserialize canonical State when it does not change. Increment shadow ETag
  exactly once in the same save transaction only when State changes. A backing
  key change alone does not advance State ETag. Repetition must not change
  State, ETag, or migration counts.

Before destructive production-State rewrite, create a consistent SQLite backup
and verify that it opens. In WAL mode, do not copy only the main `.db` file.
Use SQLite online backup or `VACUUM INTO` so committed WAL content is present.
Restore from that backup instead of inventing a reverse transformation.

After migration, every read entry deserializes State directly with the current
model. It neither probes historical fields nor calls a converter. A database
with incomplete migration cannot enter the service phase. Acceptance requires
zero historical-conversion calls on the read path. Migration code can remain at
the cold-start boundary while old database upgrade remains supported.

## Implementation Gaps

Measured during the Check stage of Issue #521:

- State averages 325 KiB per row and reaches 3.6 MiB. The 364-row table uses
  118 MiB, an order of magnitude above budget. Repeated dispatch snapshots per
  task are the primary cause; see the task-dispatch gap.
- Every read calls `JSON.Deserialize<WorkflowRun>`. Combined with three-second
  `mo run watch` polling and frequent Runner reports, this causes Server LOH
  allocation pressure. More than 95 percent of measured LOH allocation came
  from STJ string transcoding on this path, and RSS peaked at 2 GiB.
- Legacy format detection still parses complete State on every read, violating
  the write-time migration boundary. A measured database had 254 of 364 rows
  still needing conversion: 221 completed, 26 failed, and 7 stopped. `failed`
  is nonterminal in the WorkflowRun lifecycle, so migration cannot omit it.
- Status observation has no versioned cache or conditional response even though
  the row ETag can support one.
- Write amplification also affects SQLite. `mohist.db` reached 9.2 GiB, without
  a retention policy for events, transcripts, or telemetry. Existing
  `CleanupPolicyOptions` covers only Workspace.
