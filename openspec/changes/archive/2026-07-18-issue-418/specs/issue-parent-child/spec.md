### Requirement: A sub-issue SHALL be created with a parent back-reference via `--parent`

`mo issue create <title> --parent <parentNumber>` SHALL create a child issue that is otherwise a complete normal issue — with its own target repository, workflow profile, approval gates, and prerequisites — and SHALL record a single-direction back-reference from the child to the parent. The parent referenced by `--parent` MUST exist in the same project. A parent that has entered a workflow or has reached a terminal status (Done or Cancelled) SHALL NOT gain a child. This change does not alter how the child itself runs; it only establishes the organizational link.

#### Scenario: Creating a child against a valid Backlog parent succeeds
- **WHEN** a caller runs `mo issue create "Add web hook" --parent 42` and issue 42 exists, has not entered a workflow, and is not in a terminal status
- **THEN** the command SHALL create a new issue that carries `parentIssueNumber = 42`, and the new issue SHALL otherwise behave as a normal issue (own repository, workflow, approvals, prerequisites)

#### Scenario: Creating a child against a nonexistent parent is rejected
- **WHEN** a caller runs `mo issue create "Add web hook" --parent 999` and issue 999 does not exist in the project
- **THEN** the command SHALL be rejected and no issue SHALL be created

#### Scenario: A parent that has entered a workflow cannot gain a child
- **WHEN** a caller runs `mo issue create "Add web hook" --parent 42` and issue 42 has already started a workflow run
- **THEN** the command SHALL be rejected and no child SHALL be created

#### Scenario: A terminal parent cannot gain a child
- **WHEN** a caller runs `mo issue create "Add web hook" --parent 42` and issue 42 is Done or Cancelled
- **THEN** the command SHALL be rejected and no child SHALL be created

### Requirement: The hierarchy SHALL be exactly one level deep

A child issue SHALL NOT itself have children. A parent (an issue with one or more children) SHALL NOT be attachable as a child of another issue. These two guards together guarantee the parent-child relationship is a single, flat level beneath a parent and cannot form a chain or cycle.

#### Scenario: Creating a child whose parent is itself a child is rejected
- **WHEN** issue 10 is a child of issue 5, and a caller runs `mo issue create "grandchild" --parent 10`
- **THEN** the command SHALL be rejected because the designated parent is already a child, and no grandchild SHALL be created

#### Scenario: A parent cannot be attached as someone else's child
- **WHEN** issue 42 currently has one or more children, and a caller runs `mo issue update 42 --parent 7`
- **THEN** the command SHALL be rejected and issue 42 SHALL remain a parent of its existing children

### Requirement: A child created without an explicit priority SHALL inherit its parent's priority

When `--parent` is supplied to `mo issue create` and no explicit `--priority` is given, the child SHALL inherit the parent issue's priority at creation time. Supplying an explicit `--priority` SHALL override inheritance and use the given value.

#### Scenario: Child without an explicit priority inherits the parent's priority
- **WHEN** issue 42 has priority `p1`, and a caller runs `mo issue create "Add web hook" --parent 42` without `--priority`
- **THEN** the created child SHALL have priority `p1`

#### Scenario: Explicit priority overrides inheritance
- **WHEN** issue 42 has priority `p1`, and a caller runs `mo issue create "Add web hook" --parent 42 --priority p3`
- **THEN** the created child SHALL have priority `p3`

### Requirement: An existing issue SHALL be attachable as a child via `mo issue update --parent`

`mo issue update <number> --parent <parentNumber>` SHALL set the issue's parent to the given parent, replacing any previous parent. The operation SHALL be rejected when: the target issue has entered a workflow or reached a terminal status; the target issue currently belongs to an Epic (it MUST be unlinked from the Epic first); the target issue is the same as the designated parent (self-parenting); the designated parent does not exist; the designated parent has entered a workflow or reached a terminal status; or the designated parent is itself a child.

#### Scenario: Attaching a Backlog issue that is not in any Epic succeeds
- **WHEN** issue 7 is Backlog, has not entered a workflow, and does not belong to any Epic, and a caller runs `mo issue update 7 --parent 42` where 42 is an eligible parent
- **THEN** issue 7 SHALL have `parentIssueNumber = 42`

#### Scenario: An in-workflow or terminal issue cannot be attached as a child
- **WHEN** issue 7 has started a workflow run or is Done or Cancelled, and a caller runs `mo issue update 7 --parent 42`
- **THEN** the command SHALL be rejected and issue 7 SHALL remain unchanged

#### Scenario: An Epic member cannot be attached as a child without unlinking first
- **WHEN** issue 7 currently belongs to an Epic, and a caller runs `mo issue update 7 --parent 42`
- **THEN** the command SHALL be rejected; the caller MUST unlink issue 7 from its Epic before it can become a child

#### Scenario: Self-parenting is rejected
- **WHEN** a caller runs `mo issue update 42 --parent 42`
- **THEN** the command SHALL be rejected and issue 42 SHALL not become its own parent

#### Scenario: Attaching to a nonexistent parent is rejected
- **WHEN** a caller runs `mo issue update 7 --parent 999` and issue 999 does not exist
- **THEN** the command SHALL be rejected and issue 7 SHALL remain unchanged

### Requirement: A child SHALL be detachable via `mo issue update --parent none`

`mo issue update <number> --parent none` SHALL clear the issue's parent reference. Detaching an issue that is not currently a child SHALL be a no-op (idempotent) and SHALL NOT be an error.

#### Scenario: Detaching a child clears its parent reference
- **WHEN** issue 7 is a child of 42, and a caller runs `mo issue update 7 --parent none`
- **THEN** issue 7 SHALL no longer carry a parent reference and SHALL no longer appear among 42's children

#### Scenario: Detaching a non-child is an idempotent no-op
- **WHEN** issue 7 is not a child of any issue, and a caller runs `mo issue update 7 --parent none`
- **THEN** the command SHALL succeed and issue 7 SHALL remain without a parent

### Requirement: "Is a parent" SHALL be a derived fact, not a stored flag

There SHALL be no persistent "parent" flag on an issue. An issue is a parent exactly when it currently has one or more children. When an issue's last child is detached, the issue SHALL cease to be a parent and SHALL be indistinguishable from an issue that never had children — in particular it SHALL become eligible to start its own workflow again, subject to the other start blockers. The Workflow subsystem SHALL have zero awareness of the parent-child relationship; a parent carries no workflow run of its own.

#### Scenario: An issue becomes a parent when its first child is added
- **WHEN** issue 42 has no children, and a child is created or attached pointing at 42
- **THEN** issue 42 SHALL be observed as a parent (it has one or more children) and SHALL NOT be eligible to start its own workflow

#### Scenario: An issue ceases to be a parent when its last child is detached
- **WHEN** issue 42 has children, and the last remaining child is detached via `--parent none`
- **THEN** issue 42 SHALL cease to be a parent and SHALL be eligible to start its own workflow again, subject only to the other start blockers

### Requirement: An issue that has children SHALL NOT start its own workflow

`mo issue start` on an issue that currently has one or more children SHALL be rejected with a typed start blocker that identifies the parent-has-children condition. The blocker SHALL be surfaced through the same start-blocker envelope used for draft and prerequisite blockers, with its own distinct code so callers can distinguish it. Composite advancement (starting a parent by driving its children) is out of scope for this change; for now starting a parent is rejected outright.

#### Scenario: Starting an issue that has children is rejected with the parent blocker
- **WHEN** issue 42 has one or more children, and a caller runs `mo issue start 42`
- **THEN** the start SHALL be rejected with a parent-has-children blocker (distinct from the draft and waiting-for-prerequisite blocker codes), and issue 42 SHALL NOT acquire a workflow run

#### Scenario: Starting an issue after its last child is detached is allowed
- **WHEN** issue 42 previously had children, all of its children have been detached, and a caller runs `mo issue start 42`
- **THEN** the start SHALL proceed subject only to the other start blockers

### Requirement: `mo issue list --parent <number>` SHALL return only that parent's direct children

A `--parent <number>` filter on `mo issue list` SHALL restrict the result set to issues whose parent is the given number. Because the hierarchy is one level deep, these are exactly the direct children. A parent with no children SHALL yield an empty list.

#### Scenario: Listing with a parent filter returns exactly its children
- **WHEN** issues 7, 8, and 9 are children of 42, and other issues are not, and a caller runs `mo issue list --parent 42`
- **THEN** the result SHALL contain issues 7, 8, and 9, and SHALL NOT contain any issue that is not a child of 42

#### Scenario: Listing with a parent filter for a parent with no children returns an empty list
- **WHEN** issue 42 has no children, and a caller runs `mo issue list --parent 42`
- **THEN** the result SHALL be empty

### Requirement: The parent-child relationship SHALL be visible on both the parent's and child's detail

The issue detail read model SHALL project the relationship in both directions: a child's detail SHALL expose its parent reference, and a parent's detail SHALL expose a summary of its children (at minimum the fact that children exist and a count). This is a read-model projection; it does not introduce new write behavior.

#### Scenario: A child's detail shows its parent
- **WHEN** issue 7 is a child of 42, and a caller reads the detail of issue 7
- **THEN** the detail SHALL identify issue 42 as 7's parent

#### Scenario: A parent's detail shows its children
- **WHEN** issue 42 has children 7, 8, and 9, and a caller reads the detail of issue 42
- **THEN** the detail SHALL show that 42 is a parent and SHALL report the presence and count of its children

### Requirement: A child SHALL be isolated from Epic membership in both directions

A child issue SHALL NOT belong to any Epic, and an issue that currently belongs to an Epic SHALL NOT become a child. Linking a child to an Epic SHALL be rejected at the Epic link entry. A parent issue (an issue with children) is a normal issue from the Epic subsystem's perspective and MAY belong to an Epic; only the child side is isolated.

#### Scenario: Linking a child to an Epic is rejected
- **WHEN** issue 7 is a child of 42, and a caller runs `mo epic link <epic> 7` to link the child to an Epic
- **THEN** the link SHALL be rejected because a sub-issue cannot belong to an Epic, and issue 7 SHALL remain outside the Epic

#### Scenario: Attaching an Epic member as a child is rejected
- **WHEN** issue 7 currently belongs to an Epic, and a caller runs `mo issue update 7 --parent 42`
- **THEN** the command SHALL be rejected; issue 7 MUST be unlinked from its Epic before it can become a child

#### Scenario: A parent issue can belong to an Epic
- **WHEN** issue 42 has one or more children, and a caller links 42 to an Epic
- **THEN** the link SHALL succeed because a parent is a normal issue from the Epic subsystem's perspective
