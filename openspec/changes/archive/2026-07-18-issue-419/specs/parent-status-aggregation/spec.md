### Requirement: A parent issue's status SHALL be derived from its children

A parent issue's status SHALL be an automatically computed fact, not a user-settable value. The status SHALL be one of Backlog, InProgress, Done, or Cancelled, derived solely from the current states of the parent's children. The status SHALL be recomputed whenever a child is started, reaches a terminal state, is reopened, is attached, or is detached. The parent SHALL NOT enter any status other than these four as a side effect of child state changes.

#### Scenario: Parent is Backlog when no child has begun work
- **WHEN** parent P has only Backlog children and composite advancement has not been invoked
- **THEN** P's status SHALL be Backlog

#### Scenario: Parent is InProgress when any child has begun work
- **WHEN** parent P has at least one child that has started a workflow run
- **THEN** P's status SHALL be InProgress, regardless of whether the user explicitly started P

#### Scenario: Parent is Done when all children are terminal with at least one Done
- **WHEN** parent P has children A (Done), B (Done), and C (Cancelled), and no other children
- **THEN** P's status SHALL be Done

#### Scenario: Parent is Cancelled when all children are Cancelled
- **WHEN** parent P has children A (Cancelled) and B (Cancelled), and no other children
- **THEN** P's status SHALL be Cancelled

#### Scenario: Mixed-terminal children with at least one Done produce Done, not Cancelled
- **WHEN** parent P has children A (Done) and B (Cancelled), and no other children
- **THEN** P's status SHALL be Done, not Cancelled

### Requirement: Closing a parent SHALL require all children to be terminal

`mo issue close <parent>` SHALL be rejected unless every child of the parent has reached a terminal state (Done or Cancelled). Closing a parent SHALL NOT cancel, close, or otherwise mutate any non-terminal child (no cascade close). The caller MUST bring each non-terminal child to a terminal state before closing the parent.

#### Scenario: Closing a parent with a non-terminal child is rejected
- **WHEN** parent P has children A (in-progress) and B (Backlog), and a caller runs `mo issue close P`
- **THEN** the close SHALL be rejected with a typed error identifying the non-terminal child condition, and A and B SHALL remain unchanged

#### Scenario: Closing a parent does not cascade to non-terminal children
- **WHEN** parent P has children A (in-progress) and B (Done), and a caller runs `mo issue close P`
- **THEN** the close SHALL be rejected and A SHALL NOT be cancelled as a side effect

#### Scenario: Closing a parent with all children terminal is accepted by the parent-aware guard
- **WHEN** parent P has children A (Done) and B (Cancelled), every child is terminal, and a caller runs `mo issue close P`
- **THEN** the parent-aware close guard SHALL accept the operation (subject to the normal "cannot close Done or archived" rule applied to the parent's current aggregated status)

### Requirement: Reopening a cancelled parent SHALL return it to Backlog

`mo issue reopen <parent>` on a parent whose aggregated status is Cancelled SHALL transition the parent back to Backlog. The user MAY then attach new children and invoke composite advancement again. Reopen SHALL NOT modify any child's state.

#### Scenario: Reopening a cancelled parent returns it to Backlog
- **WHEN** parent P is Cancelled via aggregation, and a caller runs `mo issue reopen P`
- **THEN** P SHALL transition to Backlog, P SHALL remain a parent of its existing children, and no child SHALL change state as a side effect

### Requirement: Archiving a parent SHALL archive every non-archived child

When a caller archives a parent, every non-archived child of the parent SHALL be archived in the same operation. A child that is already archived SHALL remain archived. Archiving SHALL NOT unarchive or otherwise mutate an already-archived child.

#### Scenario: Archiving a parent archives all non-archived children
- **WHEN** parent P has children A (Done, not archived) and B (Done, already archived), P is eligible for archive, and a caller runs `mo issue archive P`
- **THEN** P SHALL be archived, A SHALL be archived, and B SHALL remain archived

#### Scenario: Archiving a parent cascades to children in any terminal state
- **WHEN** parent P is Done via aggregation with children A (Done) and B (Cancelled), and a caller runs `mo issue archive P`
- **THEN** both P and every non-archived child SHALL be archived, regardless of whether the child is Done or Cancelled

### Requirement: Detaching a child SHALL trigger an immediate status recompute on the parent

When a child is detached from a parent via `mo issue update <child> --parent none`, the parent's aggregated status SHALL be recomputed immediately against the remaining children. When the last child is detached, the parent SHALL cease to be a parent, the next `mo issue start` on that issue SHALL start its own workflow run as a normal issue, and the aggregated-status rules SHALL no longer apply to it.

#### Scenario: Detaching a child recomputes the parent's status against the remaining children
- **WHEN** parent P is Done with children A (Done) and B (Done), and a caller detaches B via `mo issue update B --parent none`
- **THEN** P's status SHALL be recomputed against the remaining children; with A still Done and terminal, P SHALL remain Done

#### Scenario: Detaching the last child reverts the parent to a normal issue
- **WHEN** parent P has exactly one remaining child A, and a caller detaches A via `mo issue update A --parent none`
- **THEN** P SHALL cease to be a parent, its status SHALL follow normal (non-aggregated) lifecycle rules, and `mo issue start P` SHALL start P's own workflow run

### Requirement: A Done parent SHALL return to InProgress when any child is reopened

When a parent's aggregated status is Done and any child of that parent is reopened (returns from a terminal state to Backlog), the parent SHALL automatically transition from Done back to InProgress without user intervention. A Cancelled parent SHALL NOT be affected by a child reopen (the parent MUST first be reopened by the user).

#### Scenario: Reopening a Done child flips a Done parent back to InProgress
- **WHEN** parent P is Done via aggregation with children A (Done) and B (Done), and a caller runs `mo issue reopen B`
- **THEN** B SHALL return to Backlog and P SHALL transition from Done to InProgress

#### Scenario: Reopening a cancelled child of a Done parent flips the parent back
- **WHEN** parent P has children A (Done) and B (Cancelled), P is Done via aggregation, and a caller runs `mo issue reopen B`
- **THEN** B SHALL return to Backlog and P SHALL transition from Done to InProgress

#### Scenario: Reopening a child does not change a Cancelled parent
- **WHEN** parent P is Cancelled via aggregation (all children Cancelled), and a caller runs `mo issue reopen` on one of its children
- **THEN** the child SHALL return to Backlog and P SHALL remain Cancelled until the caller explicitly reopens P

### Requirement: Status recompute SHALL be event-driven, idempotent, and eventually consistent

Each child state change that affects aggregation (start, terminal transition, reopen, attach, detach) SHALL publish a durable event that triggers an idempotent recompute command against the parent. The recompute command SHALL produce the same parent state when redelivered, SHALL NOT emit duplicate side-effect events for the same computed transition, and SHALL converge the parent to its correct aggregated status regardless of event ordering or redelivery.

#### Scenario: A child completion event triggers parent recompute
- **WHEN** child A of parent P transitions to Done
- **THEN** a durable event SHALL be published that triggers a recompute command against P, and P SHALL converge to the aggregated status implied by the new children snapshot

#### Scenario: Redelivering the same recompute does not corrupt parent state
- **WHEN** parent P has been recomputed to Done, and the same recompute command is delivered again
- **THEN** P SHALL remain Done, no additional status-change events SHALL be emitted, and no child SHALL be mutated

#### Scenario: Out-of-order child events still converge the parent to the correct status
- **WHEN** parent P has children A and B, A's completion event and B's cancellation event are delivered in any order, possibly redelivered
- **THEN** P SHALL converge to Done (because A is Done and at least one child is Done), regardless of the delivery order or redelivery count
