## Context

The current candidate stamps more lineage than master, but it does so on top of an unstable model:

- Issue and Epic each have a random id plus a Project-local number.
- Epic-owned `EpicIssueRow` / active-membership rows are the membership authority.
- Issue and WorkflowRun carry copied Epic ids so their stores can stamp without querying Epic.
- Copy propagation introduced Issue lineage versions and a multi-step Workflow binding state.
- `EventCatalog` became a second, manually maintained lineage matrix with exclusions for catalog
  types that have no producer.

This model repeatedly failed review because normal crash windows created stale routing, orphaned
binding runs, invalid control states, or dead letters. Those are consequences of duplicated facts,
not isolated missing guards.

The authoritative target is:

- `design/decisions/issue-owns-epic-membership.md`
- `design/workflow/issue-coordination.md`
- `design/event-protocol.md`
- `design/architecture.md` (aggregate and transaction boundary)
- `design/conventions.md` (identity)

## Goals / Non-Goals

### Goals

- Establish one permanent identity for Issue and Epic: their Project-scoped number.
- Make Issue the only writer of current Epic affiliation.
- Preserve Epic lifecycle, done revival, closed rejection, reopen, progress, and sequential
  auto-advance without Epic-owned membership truth.
- Ensure every database transaction contains one aggregate state plus that aggregate's events.
- Make every cross-aggregate step retryable and idempotent without revisions or binding states.
- Stamp complete local lineage with `projectid`, `issue`, `epic`, `workflowrunid`, and structural
  `stage`, plus the existing unique identities for AgentSession/Agent/Runner.
- Route Web and server consumers from envelope context, not payload identity.
- Migrate current state and delete the old model instead of keeping compatibility branches.

### Non-Goals

- Expression subscriptions and Agent route-table execution.
- Rewriting historical CloudEvent envelopes.
- A generic Kubernetes-style owner/controller framework.
- Cross-project Issue/Epic numbers or globally unique Issue/Epic numbers.
- Backwards compatibility for id-based Issue/Epic APIs or stored aliases.

## Decisions

### D1 - Project-scoped number is the domain identity

Issue identity is `IssueKey(ProjectId, IssueNumber)` and Epic identity is
`EpicKey(ProjectId, EpicNumber)`. These are strong domain values, not a formatted string spread
through call sites. A shared grain-key codec losslessly encodes/decodes the values for Orleans.

Persistence references use the same components. Related rows and metadata (comments,
prerequisites, workflow profile selection, inbox, session origin, subscriptions, event query
indexes, and other projections) migrate to Project + number or to the independently identified
owning resource when that is the actual relationship. There is no retained random surrogate exposed
as domain identity.

Resource and event sources become:

```text
/projects/{projectId}/issues/{issueNumber}
/projects/{projectId}/epics/{epicNumber}
/mohist/projects/{projectId}/issues/{issueNumber}
/mohist/projects/{projectId}/epics/{epicNumber}
```

### D2 - Issue owns current Epic affiliation

Issue stores `EpicNumber?`. `AssignEpic(newNumber)` atomically replaces the old number and emits
`IssueEpicChanged(previous, current)`. `RemoveEpic(expectedNumber)` clears only when the expected
number is still current. Repeating an assignment to the current value is a no-op.

Epic does not store a writable member set. An indexed Issue query supplies its current member list,
status counts, and startable candidates. The query is allowed to be stale; every Issue command
revalidates its own Epic number and lifecycle before changing state.

Rejected alternatives:

- Epic-owned join/active rows: a second membership authority and a temptation to widen the Epic
  transaction.
- Issue-side cache of Epic-owned membership: still two facts and needs a synchronization protocol.
- generic `OwnerRef { Type, Id }`: introduces unused multi-owner/controller semantics and hides the
  domain relationship.

### D3 - Link, unlink, and move are recoverable commands

`Epic.LinkIssue(issueNumber)` reads the Issue's current affiliation first. If already linked to this
Epic, it returns the prior success even if the Epic was closed after the original commit. Otherwise
the Epic rejects a new link while closed and commands `Issue.AssignEpic(epicNumber)`.

Moving from Epic A to B is one Issue commit from A to B. It never creates a two-Epic intermediate
state. The durable `IssueEpicChanged` reaction:

1. re-reads the current Issue;
2. commands old and new Epic to recompute;
3. updates the Issue's active WorkflowRun with the complete current context, if one exists.

The event payload identifies affected old/new Epic numbers, but handlers never use a stale payload
as the new truth. Redelivery re-resolves current Issue state.

### D4 - Epic progress is derived, Epic lifecycle remains authoritative

Epic owns status and progression policy. `Start` commits Epic state and `EpicStarted`; the durable
reaction invokes `Epic.Advance`. `Advance` queries current Issues, chooses a candidate, and sends
`Issue.TryStartFromEpic(epicNumber)`. Issue checks affiliation, dependencies, status, and current run.

`IssueEpicChanged`, `IssueCompleted`, `IssueCancelled`, and relevant reopen/readiness events trigger
idempotent Epic recompute/advance. A done Epic with a newly assigned open Issue converges to running;
a closed Epic rejects new assignment until reopen. These Epic state changes commit separately from
the Issue membership commit.

### D5 - IssueWorkStarted is the Workflow handoff

Issue allocates a `WorkflowRunId`, records it in its own state, changes its own lifecycle, and emits
`IssueWorkStarted` in one Issue transaction. It does not create or write WorkflowRun there.

The durable handler re-reads Issue and proceeds only when the event still identifies its current
active run. It calls:

```text
WorkflowRun.EnsureStarted(
  workflowRunId,
  { ProjectId, IssueNumber, EpicNumber? })
```

`EnsureStarted` creates and starts the run in one WorkflowRun transaction and is idempotent by run
id. A crash before creation, after creation before reply, or during handler acknowledgement is
recovered by the same event delivery. There is no `AwaitingBinding`, binding-pending marker,
confirmation command, or lineage revision.

Workflow results travel back by durable events. Issue completion/abort commands include the
expected `WorkflowRunId`, so delayed results from an old run cannot mutate a newer Issue run.

### D6 - WorkflowRun holds a small local Issue context

WorkflowRun stores exactly:

```text
ProjectId
IssueNumber
EpicNumber?
```

This context exists for correlation, profile lookup, and event stamping. Issue remains the write
authority for affiliation. After `IssueEpicChanged`, the handler reads current Issue state and sends
the complete context to the current run. Duplicate or delayed events therefore converge without a
monotonic revision. A terminal or superseded run treats the refresh as a no-op.

Workflow code does not reference the Issue aggregate, repository, or domain types. The context is a
Published Language input at the boundary, preserving the static Issue -> Workflow dependency.

### D7 - Event extensions use only canonical identities

The matrix is:

| Producer | Always | Conditional local context |
|---|---|---|
| WorkflowRun | `projectid`, `workflowrunid` | `issue`, `epic`, structural `stage` |
| Issue | `projectid`, `issue` | `epic` |
| Epic | `projectid`, `epic` | none |
| AgentSession | `projectid`, `sessionid` | `agentid`, `issue`, `epic`, `workflowrunid`, `stage` |
| Runner | `runnerid` | `projectid` |
| Inbox item persisted | `projectid`, `issue` | inherited `epic`, `workflowrunid`, `stage` |

Absent optional context is omitted, never empty. Each producer constructs extensions only from its
own committed state or attached metadata. No producer loads another aggregate while appending.
Historical envelopes remain immutable.

`stage` is determined from the domain event structure. Any Workflow event variant carrying Stage
stamps it, including feedback-requested; a non-stage-bearing event does not, regardless of type
prefix.

### D8 - Conformance is producer-family based

`EventCatalog` contains stable type names only. It does not duplicate the matrix per type.
Conformance support accepts a producer family, producer context, and emitted event shape, then
asserts the family rules. Specs exercise every real producer path and every serializable domain
event variant.

There is no `CatalogOnlyTypes` exemption. A catalog type with no producer is merely a reserved type;
it is not evidence of producer conformance. When a producer is added, its production-path spec must
pass the family rule.

## Aggregate Sequences

### Link or move

```text
Epic.LinkIssue -> Issue.AssignEpic
                      |
                      +-- commit Issue + IssueEpicChanged
                                      |
                                      +-> old Epic.Recompute
                                      +-> new Epic.Recompute
                                      +-> current WorkflowRun.UpdateIssueContext
```

### Start and complete

```text
Epic.Advance -> Issue.TryStartFromEpic
                       |
                       +-- commit Issue + IssueWorkStarted
                                           |
                                           +-> WorkflowRun.EnsureStarted
                                                  |
                                                  +-- commit WorkflowRun + events

WorkflowRunCompleted -> Issue.Complete(expectedRunId)
                              |
                              +-- commit Issue + IssueCompleted
                                                    |
                                                    +-> Epic.Advance
```

No synchronous call in either sequence returns to an aggregate already on the call stack.

## Transaction Invariant

For every command above:

- the database transaction writes one aggregate's rows/state;
- it appends only that aggregate's newly raised domain events;
- it does not read or lock another aggregate to make the command atomic;
- handlers run after commit and target another aggregate through an idempotent command.

Read models may be consulted before a target command. Correctness comes from target revalidation,
not atomicity between the read and command.

## Failure Recovery

| Failure | Durable fact | Recovery |
|---|---|---|
| Epic accepts link, Issue not committed | none | retry LinkIssue |
| Issue commits, link reply lost | Issue has target EpicNumber | repeated LinkIssue observes idempotent success; event still dispatches |
| Epic recompute save fails | Issue affiliation is authoritative | durable IssueEpicChanged redelivery retries recompute |
| old affiliation event arrives late | Issue may have newer EpicNumber | handler re-reads Issue; target commands receive current context |
| Issue starts, WorkflowRun absent | Issue has run id + IssueWorkStarted | redelivery retries EnsureStarted |
| WorkflowRun created, reply lost | WorkflowRun exists under allocated id | EnsureStarted returns idempotent success |
| old Workflow result arrives late | Issue has a different current run id | expected-run guard rejects/no-ops |

No failure requires a cross-aggregate rollback, distributed lock, binding status, or manual repair of
an intermediate relationship row.

## Migration Plan

1. Introduce typed scoped keys and migrate Issue-side storage/references while retaining a compiling
   intermediate adapter only inside the migration commit.
2. Move current affiliation into Issue.`EpicNumber?`, derived from the old current membership rows;
   migrate Epic queries/progression to Issue state.
3. Migrate Epic identity and all remaining foreign references to Project + number.
4. Replace Workflow binding/revision state with `IssueWorkStarted` + `EnsureStarted` and the small
   context.
5. Switch all producers and consumers to the canonical event extensions.
6. Drop random id columns, old relationship tables, fallback aliases, catalog lineage registry,
   binding/revision fields, and migration-only adapters.
7. Do not rewrite historical event rows. Current-state migration is verified independently and is
   idempotent for deployment retry.

## Risks / Mitigations

- **Wide identity migration.** Use focused schema/round-trip specs for every reference owner and an
  audit that fails on remaining IssueId/EpicId domain references.
- **Stale Epic candidate query.** Issue command revalidates affiliation and readiness; stale reads
  become rejection/no-op and a later advance retries.
- **Reordered durable events.** Reactions re-read current Issue state before commands; old payloads
  cannot overwrite current affiliation/context.
- **Workflow temporarily absent after Issue start.** This is an expected process interval, not a new
  Workflow status. IssueWorkStarted is durable and EnsureStarted is idempotent.
- **Historical envelopes retain old keys.** They remain immutable and may have reduced lineage in
  old history views; no compatibility fallback remains in the target model.
- **Large candidate already contains superseded code.** Tasks remove obsolete abstractions and tests
  as they migrate each boundary; no task may preserve them merely to reduce diff size.

## Resolved Questions

- Issue or Epic owns membership? **Issue.**
- Does same-context mutual dependency violate aggregate boundaries? **No.** It constrains call/flow,
  while transactions still stop at one aggregate.
- Generic owner/controller? **No.** `EpicNumber?` is the complete domain fact.
- Keep both id and number? **No.** Project-scoped number is the identity.
- Per-type EventCatalog lineage declarations? **No.** Producer-family and structural rules are
  simpler and test the real production surface.
- Workflow binding state/revision? **No.** Durable start event plus idempotent EnsureStarted is enough.
