### Requirement: Batch link of multiple issues in a single request

A batch link operation SHALL accept an array of issue identifiers (issue numbers and/or internal ids) and link each resolvable issue to the epic in a single request, replacing the N-round-trip pattern required by the single-issue link today. The batch link SHALL be exposed as a grain operation and an HTTP endpoint accepting the issue-number/id array. The existing single-issue link endpoint SHALL remain unchanged.

#### Scenario: Batch link links multiple issues at once

- **WHEN** a batch link is invoked with an array of issue numbers [A, B, C] none of which are currently members
- **THEN** all three issues SHALL become linked members of the epic
- **AND** each SHALL appear in the epic's linked-issue set

#### Scenario: Batch link resolves mixed number and id identifiers

- **WHEN** a batch link is invoked with a mix of issue numbers and internal issue ids
- **THEN** each identifier SHALL be resolved to its issue
- **AND** all resolved issues SHALL be linked

### Requirement: Batch link honors the cross-epic active-membership uniqueness invariant per issue

Batch link SHALL apply the same cross-epic active-membership uniqueness invariant as the single-issue link to each issue independently: an issue already actively owned by another non-terminal (`idle`/`running`/`paused`) epic SHALL NOT be claimed, while an issue owned only by terminal (`done`/`closed`) epics, or by no epic, SHALL be claimed. Linking an issue that is already a member of this epic SHALL be idempotent (no error, no duplicate membership).

#### Scenario: Issue already in another non-terminal epic is skipped

- **WHEN** a batch link includes an issue that is actively owned by another non-terminal epic
- **THEN** that issue SHALL NOT be linked to this epic
- **AND** the response SHALL report that issue as a conflict/skip identifying the owning epic

#### Scenario: Issue already in this epic is idempotent

- **WHEN** a batch link includes an issue that is already a member of this epic
- **THEN** that issue SHALL remain linked with no duplicate created
- **AND** the response SHALL report that issue as already-linked (not an error)

#### Scenario: Issue only in terminal epics is claimed

- **WHEN** a batch link includes an issue whose epic memberships are all terminal
- **THEN** that issue SHALL be linked to this epic
- **AND** SHALL NOT raise a duplicate-memberships conflict

### Requirement: Batch link partial-failure semantics

Batch link SHALL use partial-failure semantics: a failure on one issue (already owned by another non-terminal epic, or not found) SHALL NOT roll back or prevent the successful link of the other issues in the same batch. The response SHALL report a per-issue outcome for every requested identifier (linked, already-linked, conflict with owning epic, or not-found), so the caller can see exactly which issues succeeded and which did not. A batch with at least one successful link SHALL NOT return a top-level error. Duplicate identifiers within the same batch request SHALL be de-duplicated (linked at most once).

#### Scenario: Partial failure does not roll back successes

- **WHEN** a batch link includes issues [A, B, C] where B is already owned by another non-terminal epic
- **THEN** A and C SHALL be linked successfully
- **AND** B SHALL be reported as a conflict
- **AND** the request SHALL NOT roll back A and C

#### Scenario: Unknown issue in batch is reported per-issue

- **WHEN** a batch link includes an identifier that resolves to no issue
- **THEN** that identifier SHALL be reported as not-found
- **AND** the other resolvable issues in the batch SHALL still be linked

#### Scenario: Duplicate identifiers in one batch are linked once

- **WHEN** a batch link request contains the same issue number twice
- **THEN** the issue SHALL be linked at most once
- **AND** the response SHALL not treat the duplicate as an error

### Requirement: Batch unlink of multiple issues in a single request

A batch unlink operation SHALL accept an array of issue identifiers and unlink each from the epic in a single request. Unlink SHALL be idempotent: unlinking an issue that is not a member SHALL NOT error. Unlinking SHALL remove exactly the requested memberships and SHALL NOT affect any other member of the epic. The batch unlink SHALL be exposed as a grain operation and an HTTP endpoint accepting the issue-number/id array, and the existing single-issue unlink endpoint SHALL remain unchanged.

#### Scenario: Batch unlink removes multiple members

- **WHEN** a batch unlink is invoked with members [A, B] of an epic
- **THEN** both memberships SHALL be removed
- **AND** the remaining members SHALL stay linked

#### Scenario: Batch unlink is idempotent for non-members

- **WHEN** a batch unlink includes an issue that is not a member of the epic
- **THEN** that identifier SHALL NOT cause an error
- **AND** the unlink of the actual members SHALL still proceed

### Requirement: Batch membership HTTP contract

The batch link endpoint SHALL accept a JSON body containing an array of issue identifiers and SHALL return a per-issue result list. The batch unlink endpoint SHALL accept a JSON body containing an array of issue identifiers and SHALL return a per-issue result list. Both endpoints SHALL resolve `{id}` (epic internal id or number) and each issue identifier (number or internal id) the same way the single-issue endpoints do today.

#### Scenario: Batch link endpoint returns per-issue results

- **WHEN** a client POSTs an issue-identifier array to the batch link endpoint
- **THEN** the response SHALL include one outcome entry per requested identifier
- **AND** each entry SHALL state whether the issue was linked, already-linked, conflicted, or not-found

#### Scenario: Batch unlink endpoint returns per-issue results

- **WHEN** a client POSTs an issue-identifier array to the batch unlink endpoint
- **THEN** the response SHALL include one outcome entry per requested identifier
- **AND** each entry SHALL state whether the issue was unlinked or was-not-a-member
