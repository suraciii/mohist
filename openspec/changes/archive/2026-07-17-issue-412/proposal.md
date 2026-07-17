## Why

The failed candidate treated missing event lineage as an envelope-only problem. That led it to
preserve two identities for every Issue/Epic, keep Epic membership authoritative in Epic-owned
rows, copy that affiliation into Issue and WorkflowRun, and add revisions and binding states to
keep the copies synchronized. The result crossed aggregate transaction boundaries and grew a
recovery protocol whose failure modes were harder than the original requirement.

The root problem is ambiguous identity and write authority. Before lineage can be simple, Issue
and Epic need one identity each and current Epic affiliation needs one owner.

## What Changes

- **BREAKING: Issue and Epic use Project-scoped numbers as their only identity.** Issue identity is
  `(ProjectId, IssueNumber)` and Epic identity is `(ProjectId, EpicNumber)`. Random `IssueId` /
  `EpicId`, alias resolution, and id-based routes/contracts are removed. Grain keys, persistence
  references, API/CLI/Web contracts, profiles, comments, prerequisites, sessions, and event sources
  migrate to the scoped identities.
- **BREAKING: Issue owns current Epic affiliation.** Issue stores nullable `EpicNumber`; assigning
  a different Epic replaces the old value in one Issue transaction. Epic no longer owns writable
  membership or active-membership rows. Its member list, progress, and candidates are queries over
  Issue state.
- **Epic and Issue coordinate without a shared transaction.** `Epic.LinkIssue` handles acceptance
  and idempotency, then commands `Issue.AssignEpic`. Issue emits `IssueEpicChanged`; durable
  reactions recompute the old/new Epic and refresh the active WorkflowRun from current Issue state.
  Epic progression queries candidates and sends guarded commands that Issue revalidates.
- **Workflow handoff is reduced to one durable fact.** Issue persists its allocated
  `WorkflowRunId` and `IssueWorkStarted`. The durable reaction calls
  `WorkflowRun.EnsureStarted(runId, { ProjectId, IssueNumber, EpicNumber? })`. The old
  `AwaitingBinding`, pending marker, lineage revision, and confirmation protocol are removed.
- **Lineage names match the single identity model.** Envelopes use `projectid`, `issue`, `epic`,
  `workflowrunid`, `stage`, `agentid`, `sessionid`, and `runnerid`. They do not use `issueid`,
  `epicid`, `issueno`, or `epicno`.
- **Conformance follows producers, not a second registry.** `EventCatalog` remains a type catalog.
  Producer-family rules and structural Stage detection validate every real production path; there
  is no per-type required-attribute registry or `CatalogOnlyTypes` bypass list.
- **The old candidate is removed, not compatibility-wrapped.** Existing current state is migrated;
  historical event envelopes are not rewritten. Old schema, dual reads/writes, membership rows,
  binding states, revision guards, and fallback aliases are deleted after cutover.

## Capabilities

- `scoped-work-item-identity`: Issue and Epic have one Project-scoped number identity across the
  domain model, persistence, actors, routes, clients, and references.
- `issue-owned-epic-membership`: Issue is the sole write authority for its current optional Epic;
  Epic derives membership and progress and drives Issue through guarded commands.
- `aggregate-coordination`: cross-aggregate Issue/Epic/Workflow processes use one-aggregate commits,
  durable events, current-state re-resolution, and idempotent commands without synchronous cycles.
- `event-lineage-stamping`: every producer stamps its locally committed business context with the
  number-only protocol and never queries another aggregate while appending an event.
- `event-producer-conformance`: conformance tests cover each actual producer family and determine
  Stage requirements from event structure rather than a duplicated catalog matrix.

## Impact

- Server: Issue, Epic, Workflow, Session, Event, Inbox, Hermes, API, query services, grains, EF Core
  rows/migrations, and their specs.
- CLI and Web: Issue/Epic resource references and event timeline routing.
- Persistence: composite Project-scoped references replace Issue/Epic random ids; current Epic
  affiliation moves into Issue state; obsolete Epic membership and binding/revision storage is
  removed.
- OpenSpec: the prior D5 denormalization/binding design and completed task record are superseded.
  All tasks in the replacement plan start incomplete.
- Risk: high. The work is sequenced by authority boundary and verified with focused migration,
  idempotency, redelivery, and transaction-boundary specs before the full suite.
