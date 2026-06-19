## ADDED Requirements

### Requirement: Epic List Group Collapse

The Epic list page SHALL group Epics by status into Active, Done, and Closed sections. The Active section SHALL be expanded by default. The Done and Closed sections SHALL be collapsed by default. Each section SHALL be independently expandable and collapsible on user action, and the collapsed/expanded choice SHALL NOT change underlying Epic data.

#### Scenario: Active section is expanded by default

- **WHEN** the Epic list page is opened and there is at least one Active Epic
- **THEN** the Active section SHALL display its Epic cards without requiring a user action

#### Scenario: Done and Closed sections are collapsed by default

- **WHEN** the Epic list page is opened and there are Done or Closed Epics
- **THEN** those sections SHALL be collapsed so their cards are not visible
- **AND** each SHALL provide a control to expand it

#### Scenario: A collapsed section can be expanded and collapsed again

- **WHEN** a user activates the expand control on a collapsed Done or Closed section
- **THEN** the Epic cards in that section SHALL become visible
- **AND** the user SHALL be able to collapse it again

### Requirement: Status-Conditional Epic Card Text

The status text on an Epic list card SHALL branch by the Epic's own status. Active Epics SHALL show next-step guidance (the next issue or a ready-to-mark-done indication). Done Epics SHALL show a completion phrase. Closed Epics SHALL show a closed phrase. The "Ready to mark done" indication SHALL appear only for Active Epics and SHALL NOT appear on Done or Closed Epics.

#### Scenario: Active Epic shows next-step guidance

- **WHEN** an Active Epic card is rendered
- **THEN** its status text SHALL present the next issue or a ready-to-mark-done indication
- **AND** it SHALL NOT present a closed or completion phrase

#### Scenario: Done Epic shows a completion phrase

- **WHEN** a Done Epic card is rendered
- **THEN** its status text SHALL present a completion phrase
- **AND** it SHALL NOT present "Ready to mark done" or a next-issue prompt

#### Scenario: Closed Epic shows a closed phrase

- **WHEN** a Closed Epic card is rendered
- **THEN** its status text SHALL present a closed phrase
- **AND** it SHALL NOT present "Ready to mark done" or a next-issue prompt

### Requirement: Epic Card In-Progress and Next Display

An Epic list card SHALL surface both the in-flight issue(s) and the next issue to advance, so a user can see at a glance what is running and what comes next. When an Epic has an active in-flight issue, the card SHALL indicate it is in progress. When an Epic has a startable next issue, the card SHALL indicate the next issue. When either is absent, the card SHALL degrade gracefully without implying work exists that does not.

#### Scenario: Card shows both in-progress and next

- **WHEN** an Epic has an active in-flight issue and a distinct startable next issue
- **THEN** the card SHALL display both the in-progress indication and the next issue

#### Scenario: Card degrades when no in-flight issue

- **WHEN** an Epic has a startable next issue but no active in-flight issue
- **THEN** the card SHALL display the next issue
- **AND** it SHALL NOT imply an issue is currently in progress

#### Scenario: Card degrades when no next issue

- **WHEN** an Epic has an active in-flight issue but no startable next issue
- **THEN** the card SHALL display the in-progress indication
- **AND** it SHALL NOT imply a next issue exists

### Requirement: Epic Detail Current Activity Listing

The Epic detail page "Current Activity" surface SHALL list the concrete in-flight issues (active and blocked) for the Epic, each rendered with its health indication and a way to navigate to that issue. It SHALL NOT present only an aggregate count. When there are no in-flight issues, the surface SHALL state that clearly.

#### Scenario: Current Activity lists concrete in-flight issues

- **WHEN** an Epic has active or blocked linked issues
- **THEN** the "Current Activity" surface SHALL list those issues with their number, title, and health coloring
- **AND** each entry SHALL offer navigation to the corresponding issue

#### Scenario: Current Activity reflects real activity instead of a constant zero

- **WHEN** an Epic has linked issues whose `Health` is `active` or `blocked`
- **THEN** the "Current Activity" surface SHALL show those issues
- **AND** it SHALL NOT read as zero active and zero blocked

#### Scenario: Current Activity is empty when nothing is in flight

- **WHEN** an Epic has no active or blocked linked issues
- **THEN** the "Current Activity" surface SHALL state that there is no current activity

### Requirement: Epic Description Rendered as Markdown

The Epic detail page SHALL render the Epic description as Markdown through the shared `MarkdownReader` component, so headings, lists, emphasis, and code render as formatted content. Raw Markdown markers (such as `##`, list prefixes, or `**`) SHALL NOT be displayed as plain text.

#### Scenario: Markdown structures render as formatted content

- **WHEN** an Epic description contains Markdown headings, lists, and emphasis
- **THEN** the detail page SHALL render them as formatted document content via `MarkdownReader`
- **AND** the raw Markdown markers SHALL NOT appear as literal text in the reading experience

#### Scenario: Plain descriptions still render readably

- **WHEN** an Epic description contains no Markdown syntax
- **THEN** the detail page SHALL render the text readably through `MarkdownReader`
- **AND** the rendering SHALL NOT introduce spurious formatting
