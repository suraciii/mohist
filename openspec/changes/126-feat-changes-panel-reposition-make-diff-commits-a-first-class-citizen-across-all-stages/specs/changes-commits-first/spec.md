## MODIFIED Requirements

### Requirement: Commits tab is default view in Changes section

The Changes section SHALL default to the Commits tab instead of the Files tab. The tab state SHALL initialize to `'commits'`. The Changes section SHALL be visible in all workflow stages without stage-based gating.

#### Scenario: User opens issue detail page with changes

- **WHEN** user navigates to an issue detail page that has changes (commits or file diffs)
- **THEN** the Changes section displays the Commits tab by default
- **AND** the Commits tab button shows active/selected styling

#### Scenario: User switches to Files tab and back

- **WHEN** user clicks the Files tab button
- **THEN** the Files tab content is displayed
- **AND** clicking the Commits tab button returns to the Commits view

#### Scenario: Changes section visible regardless of stage

- **WHEN** user views an issue in any stage (Backlog, Explore, Plan, Build, Check, Done)
- **THEN** the Changes section is always rendered in the page layout
- **AND** no `DIFF_STAGES` or equivalent stage restriction gates visibility
