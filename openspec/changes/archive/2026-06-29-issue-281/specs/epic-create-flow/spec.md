## ADDED Requirements

### Requirement: Guided milestone description structure

The Create Epic description area SHALL guide the user toward a legible milestone by offering a structured template that covers Goal, Background, Non-goals, and Scope sections. The guided structure SHALL be surfaced when the description is empty or holds only a simple (non-templated) value, so the user can author a milestone description without having to recall the structure. The description field SHALL remain a free-form markdown editor: the template is a starting scaffold only, and the user SHALL be able to add, remove, reorder, or replace any section freely. The Create Epic form SHALL submit only the resulting markdown description; the Epic create API fields SHALL remain unchanged, and no hidden or non-editable structured payload SHALL be sent.

#### Scenario: Empty description shows the guided template
- **WHEN** the Create Epic dialog is opened with an empty description
- **THEN** the description area SHALL present a guided template containing Goal, Background, Non-goals, and Scope sections
- **AND** the user SHALL be able to edit each section as free-form markdown

#### Scenario: Simple non-templated description still offers the structure
- **WHEN** the Create Epic dialog is opened with a description that contains only a simple non-templated value
- **THEN** the form SHALL offer the Goal / Background / Non-goals / Scope guided structure
- **AND** SHALL NOT silently overwrite or destroy the user's existing text

#### Scenario: Only markdown is sent to the create API
- **WHEN** the user submits the Create Epic form
- **THEN** only the resulting markdown description SHALL be sent to the Epic create API
- **AND** no separate goal / background / non-goals / scope fields SHALL be added to the Epic create API payload

### Requirement: Quick create without forced template

A user SHALL be able to create an Epic quickly without being forced to fill out every section of the guided template. Empty sections in the guided description SHALL be permitted, and the user SHALL be able to clear the template entirely and write a plain description or no description. The Create Epic form SHALL NOT block submission on the presence or completeness of any specific Goal / Background / Non-goals / Scope section.

#### Scenario: Submission accepted with incomplete template sections
- **WHEN** the user submits the Create Epic form while the guided template still contains empty sections
- **THEN** the submission SHALL be accepted
- **AND** SHALL NOT be rejected for incomplete template sections

#### Scenario: Plain or empty description is accepted
- **WHEN** the user clears the guided template and writes a plain one-line description (or no description)
- **THEN** the submission SHALL be accepted
- **AND** the resulting stored description SHALL equal exactly what the user authored

### Requirement: Post-create navigation choice

After a successful Epic creation, the Create Epic dialog SHALL offer the user an explicit choice between navigating to the newly created Epic's detail page (to plan linked issues) and staying on the current page. Both paths SHALL be reachable; neither SHALL be the only option. The navigate-to-detail path SHALL navigate using the new Epic's identifier (id and/or number) returned by the create response, so the user lands on the correct new Epic.

#### Scenario: Success offers navigate-or-stay choice
- **WHEN** the create Epic mutation succeeds
- **THEN** the dialog SHALL present a choice to navigate to the new Epic detail page or to stay on the current page
- **AND** neither option SHALL be hidden or disabled

#### Scenario: Navigate to the new Epic detail
- **WHEN** the user chooses to navigate to the new Epic detail after creation
- **THEN** the app SHALL navigate to the new Epic's detail route using the identifier returned by the create response

#### Scenario: Stay on current page reflects the new Epic
- **WHEN** the user chooses to stay on the current page after creation
- **THEN** the app SHALL remain on the current page
- **AND** the newly created Epic SHALL be reflected in the Epic list

### Requirement: Idle-aware creation feedback

The create-success feedback SHALL convey that the newly created Epic is `idle` and ready to plan, and SHALL NOT imply the Epic has started executing or is advancing linked issues. The feedback SHALL make clear that an explicit Start Epic action is required to begin autonomous progression.

#### Scenario: Success message communicates idle / ready-to-plan
- **WHEN** the create Epic mutation succeeds
- **THEN** the success message SHALL state that the Epic was created as idle / ready to plan (or equivalent wording)

#### Scenario: Success message does not imply execution has started
- **WHEN** the create Epic mutation succeeds
- **THEN** the success message SHALL NOT contain wording implying execution has started
- **AND** SHALL NOT use phrasing such as "Epic started" or "Epic running"

### Requirement: Edit Epic preserves existing markdown

The Edit Epic description area SHALL preserve the Epic's existing markdown description verbatim and SHALL NOT force-rewrite, reformat, or override existing content with the Goal / Background / Non-goals / Scope template. The Edit Epic form MAY offer the guided template as an explicit opt-in affordance (e.g. an "insert template" action) for descriptions that are empty or that the user chooses to restructure, but SHALL NOT apply it automatically to existing content. Saving the Edit Epic form SHALL send exactly the description the user authored, preserving existing markdown.

#### Scenario: Existing markdown is loaded verbatim
- **WHEN** the Edit Epic dialog is opened for an Epic with an existing markdown description
- **THEN** the description area SHALL be populated with the existing markdown verbatim
- **AND** the content SHALL NOT be rewritten into the Goal / Background / Non-goals / Scope template

#### Scenario: Saving without edits preserves the description
- **WHEN** the user saves the Edit Epic form without editing the description
- **THEN** the submitted description SHALL equal the pre-existing markdown
- **AND** no template content SHALL be injected

#### Scenario: Template is opt-in for empty descriptions
- **WHEN** the Edit Epic dialog is opened for an Epic whose description is empty
- **THEN** the form MAY offer the guided template
- **AND** the template SHALL be inserted only when the user explicitly invokes the affordance

### Requirement: Create/Edit forms are mobile operable

The Create Epic and Edit Epic dialogs SHALL be usable on mobile viewport widths (320px, 390px, 430px). The forms SHALL NOT produce horizontal overflow (`documentElement.scrollWidth <= documentElement.clientWidth`), and the primary fields (title, description, priority, submit) SHALL remain visible and operable when the soft keyboard is open. Long content in the title or description SHALL wrap within the available width rather than causing horizontal scroll.

#### Scenario: Create Epic does not overflow on mobile widths
- **WHEN** the Create Epic dialog is rendered at viewport widths of 320px, 390px, and 430px
- **THEN** `documentElement.scrollWidth` SHALL be less than or equal to `documentElement.clientWidth`
- **AND** the title, description, priority, and submit fields SHALL be reachable without horizontal scrolling

#### Scenario: Edit Epic does not overflow on mobile widths
- **WHEN** the Edit Epic dialog is rendered at viewport widths of 320px, 390px, and 430px
- **THEN** `documentElement.scrollWidth` SHALL be less than or equal to `documentElement.clientWidth`
- **AND** the title, description, priority, and submit fields SHALL be reachable without horizontal scrolling

#### Scenario: Submit stays reachable with the soft keyboard open
- **WHEN** the soft keyboard is open on mobile and the user is editing the description
- **THEN** the submit action SHALL remain reachable (e.g. via scroll or a persistent footer)
- **AND** SHALL NOT be permanently obscured by the keyboard
