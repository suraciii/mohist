## ADDED Requirements

### Requirement: Done column footer shows archive summary

StageColumn component for the Done column SHALL display a footer section when there are archived issues. The footer SHALL show the count of archived issues, a "查看" link navigating to `/archived`, and an "归档所有已完成" button.

#### Scenario: No archived issues

- **WHEN** the Done column has zero archived issues
- **THEN** the footer section SHALL NOT be rendered

#### Scenario: Archived issues exist

- **WHEN** there are N > 0 archived issues
- **THEN** the Done column footer displays "📦 N 已归档"
- **AND** displays a "查看" link pointing to `/archived`
- **AND** displays an "归档所有已完成" button

#### Scenario: Click "查看" link

- **WHEN** user clicks the "查看" link in the archive summary
- **THEN** browser navigates to `/archived`

#### Scenario: Click "归档所有已完成" button

- **WHEN** user clicks "归档所有已完成"
- **THEN** the system calls `POST /api/issues/archive-completed`
- **AND** the Done column issue list refreshes (archived issues disappear)
- **AND** the archived count updates

#### Scenario: "归档所有已完成" with no completed issues

- **WHEN** user clicks "归档所有已完成"
- **AND** Done column has zero unarchived completed issues
- **THEN** the button is disabled or hidden

### Requirement: Issue card archive button in Done column

Each IssueCard rendered within the Done column SHALL display an archive button (📦 icon) in the top-right corner. Clicking the button SHALL archive the issue and refresh the list.

#### Scenario: Archive button visible on Done column cards

- **WHEN** an IssueCard is rendered inside the Done column
- **AND** the issue status is `completed`
- **THEN** an archive button (📦 icon) is visible in the card's top-right area
- **AND** the button uses a gray color that darkens on hover

#### Scenario: Archive button not visible outside Done column

- **WHEN** an IssueCard is rendered in any column other than Done
- **THEN** the archive button SHALL NOT be displayed

#### Scenario: Click archive button

- **WHEN** user clicks the archive button on an issue card
- **THEN** `POST /api/issues/:number/archive` is called
- **AND** the issue disappears from the Done column list
- **AND** the archived count in the footer increments by 1

#### Scenario: Archive button during request

- **WHEN** the archive API request is in flight
- **THEN** the archive button shows a loading/disabled state
- **AND** prevents duplicate clicks

### Requirement: Archive summary uses subtle styling

The archive summary footer SHALL use a subtle visual style that does not compete with the main card content.

#### Scenario: Archive footer styling

- **WHEN** the archive summary footer is rendered
- **THEN** it uses gray-toned text and background consistent with the existing gray-100/60 palette
- **AND** the "归档所有已完成" button uses a subdued style (not primary blue)
