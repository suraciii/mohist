## ADDED Requirements

### Requirement: IssueDetailPage renders Changes panel without stage gating

IssueDetailPage SHALL NOT use a stage-based set (e.g., `DIFF_STAGES`) to gate the visibility of the Changes panel. The panel SHALL be rendered for all stages, with the stage-specific logic limited to displaying empty-state content when no changes exist.

#### Scenario: DIFF_STAGES constant removed or unused
- **WHEN** IssueDetailPage source code is inspected
- **THEN** no `DIFF_STAGES` set or equivalent stage-gating logic controls Changes panel visibility
- **AND** the Changes panel render block is not wrapped in a stage-dependent conditional

### Requirement: IssueDetailPage main content section order

The main content column (2/3 width) of IssueDetailPage SHALL render sections in this fixed order: BranchBar, Description (if present), Changes panel, TaskList (if applicable), Comments. The old position of the Changes panel after Comments SHALL be removed.

#### Scenario: Old diff section position removed
- **WHEN** IssueDetailPage source code is inspected
- **THEN** no Changes/Diff section appears after the Comments section in the main content column
