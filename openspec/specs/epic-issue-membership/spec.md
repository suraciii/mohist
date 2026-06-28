### Requirement: Issue–epic membership link

An issue MAY be linked to an epic to record that it is in scope of that epic. Each link is a single persistent membership record between one issue and one epic. Linking an issue to an epic it is already a member of SHALL be idempotent: it SHALL NOT error and SHALL NOT create a duplicate membership.

#### Scenario: Linking creates a membership
- **WHEN** an issue is linked to an epic it is not currently a member of
- **THEN** a single membership link SHALL be created between them
- **AND** the issue SHALL appear in that epic's linked-issue set

#### Scenario: Linking the same epic twice is idempotent
- **WHEN** an issue is linked to an epic it is already a member of
- **THEN** the request SHALL NOT error
- **AND** SHALL NOT create a duplicate membership

### Requirement: Explicit unlink of a single membership

A single issue↔epic membership link SHALL be removable via an explicit unlink operation. Unlink SHALL remove exactly one link and SHALL NOT affect any other membership of that issue or any other member of that epic.

#### Scenario: Unlink removes a single membership
- **WHEN** unlink is invoked for an issue that is a member of an epic
- **THEN** that single membership link SHALL be removed
- **AND** the issue's memberships in any other epic SHALL remain unchanged

#### Scenario: Unlink is scoped to one link
- **WHEN** unlink is invoked for one issue of an epic that has multiple members
- **THEN** only that issue's link SHALL be removed
- **AND** the other members of the epic SHALL remain linked

### Requirement: At most one non-terminal epic membership per issue

An issue SHALL be a member of at most one non-terminal (`idle`/`running`/`paused`) epic. The duplicate-memberships check SHALL be epic-status-aware: attempting to link an issue that is already a member of a non-terminal epic to a second non-terminal epic SHALL be rejected with `DUPLICATE_EPIC_MEMBERSHIP`. A membership whose owning epic is terminal (`done`/`closed`) SHALL NOT count toward this uniqueness invariant and SHALL NOT block linking the issue to a non-terminal epic.

#### Scenario: Second non-terminal epic membership is rejected
- **WHEN** an issue is already a member of a non-terminal (`idle`/`running`/`paused`) epic
- **AND** it is linked to a second non-terminal epic
- **THEN** the request SHALL be rejected with `DUPLICATE_EPIC_MEMBERSHIP`
- **AND** no second membership SHALL be created

#### Scenario: Terminal-epic membership does not block a new non-terminal link
- **WHEN** an issue is a member of one or more terminal (`done`/`closed`) epics only
- **AND** it is linked to a non-terminal (`idle`/`running`/`paused`) epic
- **THEN** the link SHALL succeed
- **AND** SHALL NOT raise `DUPLICATE_EPIC_MEMBERSHIP`
- **AND** the issue SHALL be a member of exactly one non-terminal epic

#### Scenario: Terminal membership held alongside non-terminal still enforces uniqueness
- **WHEN** an issue is a member of a terminal epic
- **AND** the issue is then linked to a non-terminal epic
- **AND** the issue is then linked to a second non-terminal epic
- **THEN** the second non-terminal link SHALL be rejected with `DUPLICATE_EPIC_MEMBERSHIP`
- **AND** the terminal-epic membership SHALL remain intact

### Requirement: Membership retained across epic close

Closing an epic SHALL NOT remove, clear, or otherwise modify its linked-issue memberships. The membership set observed immediately before close SHALL be identical to the set observed immediately after close, so that epic progress, history, and detail remain readable post-close. The terminal epic's retained memberships SHALL NOT serve as the issue's active epic (see "primaryEpic projection reflects non-terminal membership").

#### Scenario: Close preserves all links
- **WHEN** an epic with N linked issues is closed
- **THEN** the epic SHALL transition to `closed`
- **AND** all N linked-issue memberships SHALL remain present
- **AND** the epic's progress and member list SHALL remain readable

#### Scenario: Closed epic's links do not block re-homing
- **WHEN** an epic containing issue X is closed (links retained)
- **AND** issue X is subsequently linked to a non-terminal epic
- **THEN** the new link SHALL succeed per the non-terminal uniqueness rule

### Requirement: primaryEpic projection reflects non-terminal membership

The `primaryEpic` projection of an issue SHALL reference the issue's non-terminal (`idle`/`running`/`paused`) epic membership. An issue whose epic memberships are all terminal (`done`/`closed`) SHALL project no `primaryEpic` (null). When an issue is re-homed from a terminal epic to a new non-terminal epic, the projection SHALL follow the new non-terminal epic.

#### Scenario: Issue in a non-terminal epic projects that epic
- **WHEN** an issue is a member of exactly one non-terminal epic
- **THEN** the issue's `primaryEpic` SHALL reference that epic

#### Scenario: Issue only in terminal epics projects no primaryEpic
- **WHEN** an issue is a member of one or more terminal epics only
- **THEN** the issue's `primaryEpic` SHALL be null

#### Scenario: Re-homing updates primaryEpic to the new non-terminal epic
- **WHEN** an issue is a member of a terminal epic (links retained)
- **AND** the issue is linked to a new non-terminal epic
- **THEN** the issue's `primaryEpic` SHALL reference the new non-terminal epic
- **AND** SHALL NOT reference the terminal epic
