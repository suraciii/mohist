## ADDED Requirements

### Requirement: Linked Issue Read Model Carries Prerequisite Edges

The Epic linked-issue read model (`LinkedIssueDto` returned by `EpicQuerier.GetLinkedIssuesAsync`, or an equivalent Epic-scoped projection) SHALL carry, for each linked issue, its `prerequisiteNumbers` so that a client can render the dependency graph without issuing a per-issue fetch. For each prerequisite that is not a member of the Epic (an external prerequisite), the read model SHALL include enough summary — at minimum the issue number, title, and status/delivery state — to render it as a distinct external node. The prerequisite-edge data on the read model SHALL be additive and SHALL NOT alter the existing `Projected Epic Progress` semantics: `deliveredCount`, `totalIssueCount`, the active and blocked sets, `nextIssue` selection, and `readyToMarkDone` SHALL continue to be derived exactly as before.

#### Scenario: Prerequisite numbers are available per linked issue

- **WHEN** the Epic linked-issue read model is returned for an Epic
- **THEN** each linked issue entry SHALL carry its `prerequisiteNumbers`
- **AND** a client SHALL be able to render prerequisite edges without an additional per-issue lookup

#### Scenario: External prerequisite summary is available

- **WHEN** a linked issue has a prerequisite that is not a member of the same Epic
- **THEN** the read model SHALL include a summary of that external prerequisite carrying at least its number, title, and status/delivery state
- **AND** the summary SHALL be sufficient to render the external prerequisite as a distinct node

#### Scenario: Prerequisite edges do not change progress semantics

- **WHEN** prerequisite-edge data is added to the linked-issue read model
- **THEN** `deliveredCount`, `totalIssueCount`, the active and blocked sets, `nextIssue`, and `readyToMarkDone` SHALL continue to be derived exactly as in `Projected Epic Progress`
- **AND** the additive field SHALL NOT change any existing progress, next-issue, or mark-done outcome
