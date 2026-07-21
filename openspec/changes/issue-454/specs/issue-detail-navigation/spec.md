### Requirement: Core issue sections have stable URL fragments

The issue detail page SHALL provide stable fragment identifiers for its Workflow, Artifacts, Activity, and Comments destinations. The canonical fragments SHALL be `#workflow`, `#artifacts`, `#activity`, and `#comments`, and links to these destinations SHALL preserve the issue's existing route and project scope.

#### Scenario: User obtains links to issue sections

- **WHEN** the issue detail page exposes navigation or shareable links for Workflow, Artifacts, Activity, or Comments
- **THEN** each link SHALL retain the current issue detail path
- **AND** it SHALL use the corresponding `#workflow`, `#artifacts`, `#activity`, or `#comments` fragment

### Requirement: Direct section URLs reveal their destination

Opening an issue detail URL with a supported section fragment SHALL reveal the requested destination after the issue data and conditional page content have rendered. Workflow, Artifacts, and Comments fragments SHALL bring their section into view. The Activity fragment SHALL make the Activity content visible, whether Activity is represented inline or in an addressable dialog.

#### Scenario: Direct workflow link is opened

- **WHEN** the user opens an issue detail URL ending in `#workflow`
- **THEN** the page SHALL bring the Workflow section into view after it renders
- **AND** the workflow destination SHALL be identifiable as the target of the fragment

#### Scenario: Direct artifacts link is opened

- **WHEN** the user opens an issue detail URL ending in `#artifacts`
- **THEN** the page SHALL bring the Artifacts section into view after it renders
- **AND** the artifacts destination SHALL remain represented by `#artifacts` in the URL

#### Scenario: Direct comments link is opened

- **WHEN** the user opens an issue detail URL ending in `#comments`
- **THEN** the page SHALL bring the Comments section into view after it renders
- **AND** the comments destination SHALL remain represented by `#comments` in the URL

#### Scenario: Direct activity link is opened

- **WHEN** the user opens an issue detail URL ending in `#activity`
- **THEN** the Activity view and its event timeline SHALL become visible after the issue renders
- **AND** the user SHALL NOT need to activate the ordinary Activity trigger first

### Requirement: Fragment navigation works within an already open issue

Changing between supported issue section fragments while the issue detail page is already open SHALL reveal the newly requested destination without navigating away from the issue.

#### Scenario: User follows a section link on the open page

- **WHEN** the current issue detail URL changes from one supported section fragment to another
- **THEN** the page SHALL reveal the destination represented by the new fragment
- **AND** it SHALL retain the same project and issue identity
