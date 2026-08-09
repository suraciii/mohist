# Issue Owns Epic Membership (issue-412)

## Background

The old model kept both random IDs and Project-scoped numbers for Issue and
Epic. It stored Epic membership in an Epic-side relation table. Event routing
also copied Issue and Epic lineage into Issue and WorkflowRun. One fact
therefore had several identities and write paths, with binding, revision, and
compensation protocols to synchronize copies.

The model must converge under these constraints:

- An aggregate is a strong-consistency and database-transaction boundary.
- Issue and Epic belong to one bounded context and may depend on each other.
- One business fact has one write authority.
- The model contains only concepts and properties required now.

## Decision

### 1. Number Is Issue and Epic Identity

Issue identity is `(ProjectId, IssueNumber)`. Epic identity is
`(ProjectId, EpicNumber)`. Remove random `IssueId` and `EpicId`; number is not
an alias that resolves to another identity. Orleans GrainKey and resource paths
derive from this domain identity.

### 2. Issue Is the Sole Write Authority for Current Epic Membership

Issue holds nullable `EpicNumber` directly. An Issue belongs to at most one Epic
at a time and replaces its old value in the Issue transaction. Epic stores no
independently writable membership row or member collection. Member list,
progress, and advancement candidates query current Issue state.

The entry point can remain `Epic.LinkIssue`. Epic first reads current Issue
membership. It returns idempotent success when the Issue already belongs to
that Epic. Otherwise, it validates that the link is acceptable and synchronously
commands Issue to run `AssignEpic`. Only the Issue transaction commits
membership. Unlink includes the expected Epic number so a delayed command from
an old Epic cannot clear newer membership.

### 3. One Context Can Have Mutual Dependency without a Shared Transaction

Epic can command Issue to link or start. Issue membership, start, and completion
events can asynchronously trigger Epic recomputation. This business loop is not
a synchronous call cycle. A call stack has only the `Epic -> Issue` direction.
The reverse path starts from a durable event after Issue commits.

One database transaction contains only one aggregate state and that aggregate's
domain events. Epic state, Issue membership, and WorkflowRun changes commit
separately. A failure between aggregates does not roll back committed state.
Event redelivery and idempotent commands converge the process.

### 4. WorkflowRun Stores Minimal Issue Context

WorkflowRun stores `{ ProjectId, IssueNumber, EpicNumber? }` for event stamping.
This is local run context, not membership authority. Issue supplies it when
starting the run. After membership changes, a handler rereads current Issue
state and idempotently refreshes the active WorkflowRun.

Do not add `AwaitingBinding`, `WorkflowBindingPending`, lineage revision, or a
binding protocol. `IssueWorkStarted` is the reliable handoff. Its handler uses
the persisted `WorkflowRunId` to call `WorkflowRun.EnsureStarted`. Run identity
makes repeated delivery idempotent.

### 5. Do Not Add a Generic Owner or Controller Model

Kubernetes `ownerReferences` and controllers solve generic resource lifecycle.
This domain has one specific fact: the current Epic of an Issue. `EpicNumber?`
expresses it completely. `OwnerRef { Type, Id }` would introduce unused
multiple ownership, cascading deletion, controller arbitration, and generic
protocol while hiding domain language.

## Failure Recovery

| Failure point | Committed fact | Recovery |
|---|---|---|
| After Epic validation and before Issue commit | No new membership | Retry `LinkIssue` |
| After Issue commit and before the Epic response | Issue has new `EpicNumber` | Retry reaches the idempotent `AssignEpic` result; `IssueEpicChanged` still delivers |
| Epic recomputation save fails | Issue membership is committed | Durable handler redelivers `Epic.Recompute` |
| Issue starts before WorkflowRun creation | Issue persisted `WorkflowRunId` | `IssueWorkStarted` redelivers `EnsureStarted` |
| Old membership event arrives late | Issue can have newer membership | Handler rereads current Issue state instead of applying the old payload |

No recovery needs a cross-aggregate transaction, distributed lock, or manual
repair of intermediate state.

## Consequences

- Issue and Epic commands, resource paths, and events use only Project-scoped
  numbers, matching user language.
- Epic queries can be briefly stale. Issue revalidates invariants at its command
  boundary, so staleness produces only a no-op, rejection, or retry and cannot
  corrupt consistency.
- Epic progress and automatic advancement are eventually consistent with Issue
  commit. Each Issue, Epic, and WorkflowRun remains strongly consistent.
- Remove old `EpicIssueRow`, `IssueId`, `EpicId`, and binding or revision state
  introduced to synchronize their copies after migration. Do not retain a
  dual-write compatibility path.

This decision preserves product semantics from
[`epic-status-revival.md`](epic-status-revival.md) but supersedes its design in
which revival and the membership row shared one transaction.
