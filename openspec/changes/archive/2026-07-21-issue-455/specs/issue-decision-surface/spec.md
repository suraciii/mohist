### Requirement: An approval wait presents one review package

When an issue is awaiting an approval decision, the issue decision surface SHALL present the approval context, required review evidence, and every action applicable to the current issue state as one review package at the top of the issue detail page. Approve and send back SHALL use the existing authorized workflow actions and SHALL NOT be duplicated elsewhere on the page. Delegation, transcript, or other secondary actions that apply during approval SHALL remain reachable from the package. An issue that is not awaiting approval SHALL retain its non-approval decision presentation and SHALL NOT show the approval review package.

#### Scenario: Approval wait replaces the generic decision presentation

- **WHEN** an owner opens an issue that is awaiting plan or check approval
- **THEN** the top decision surface SHALL present the approval review package
- **AND** the package SHALL include the approval context, required evidence, and every action applicable to the current issue state
- **AND** the page SHALL NOT render a second approve or send-back control

#### Scenario: Non-approval state remains unchanged

- **WHEN** an owner opens an issue that is not awaiting an approval decision
- **THEN** the issue SHALL use its applicable non-approval decision presentation
- **AND** the approval review package SHALL NOT be rendered

### Requirement: Plan approval evidence is readable inline

The plan approval review package SHALL render the recorded contents of `proposal.md` and `tasks.json` for the current workflow run directly on the issue detail page. Both artifacts SHALL be identifiable by name and readable without selecting a file or opening a dialog. Loading, missing, and failed artifact content SHALL be reported inline for the affected artifact rather than leaving an unexplained empty region.

#### Scenario: Plan artifacts are available

- **WHEN** an issue awaits plan approval and the current workflow run has recorded `proposal.md` and `tasks.json`
- **THEN** the contents of both artifacts SHALL be visible in the approval review package
- **AND** reading either artifact SHALL NOT require a file-selection action or dialog

#### Scenario: A plan artifact cannot be displayed

- **WHEN** an issue awaits plan approval and either required plan artifact is loading, missing, or fails to load
- **THEN** the review package SHALL identify that artifact and show its current loading or unavailable state inline
- **AND** the other available approval evidence SHALL remain readable

### Requirement: Check approval evidence is readable inline

The check approval review package SHALL render the recorded contents of `review.md` for the current workflow run and the current branch diff summary directly on the issue detail page. The diff summary SHALL identify the compared branches and report files changed, additions, and deletions. Neither the review nor the diff summary SHALL require opening a dialog.

#### Scenario: Check evidence is available

- **WHEN** an issue awaits check approval and `review.md` and the current diff summary are available
- **THEN** the review package SHALL show the contents of `review.md`
- **AND** it SHALL show the compared branches, files changed, additions, and deletions
- **AND** reading that evidence SHALL NOT require opening a dialog

#### Scenario: Check evidence cannot be displayed

- **WHEN** an issue awaits check approval and `review.md` or the diff summary is loading or unavailable
- **THEN** the review package SHALL identify the affected evidence and show its current loading or unavailable state inline
- **AND** the other available check evidence SHALL remain readable

### Requirement: The approval package is usable on a phone

At a phone-width viewport, approval artifact content SHALL wrap or otherwise fit within the page without causing horizontal page scrolling, including long JSON values and unbroken text. Approve and send back SHALL be visible as direct, thumb-reachable controls alongside the review package while the owner reads the evidence. Approve SHALL execute from one tap, Send back SHALL reveal its structured inline form from one tap, and neither action SHALL require opening the generic action drawer or another dialog. Every other applicable action SHALL remain reachable through a separate compact, non-modal secondary-action control that does not obscure or interpose itself before Approve or Send back. The controls SHALL account for the viewport edge and safe area.

#### Scenario: Plan approval is reviewed on a phone

- **WHEN** an owner opens a plan approval package at a phone-width viewport
- **THEN** `proposal.md` and `tasks.json` SHALL be readable without horizontal page scrolling
- **AND** direct approve and send-back controls SHALL remain visible and thumb-reachable alongside the package
- **AND** no generic action drawer or dialog SHALL be required to reach either action

#### Scenario: Check approval is reviewed on a phone

- **WHEN** an owner opens a check approval package at a phone-width viewport
- **THEN** `review.md` and the diff summary SHALL be readable without horizontal page scrolling
- **AND** direct approve and send-back controls SHALL remain visible without being obscured by the viewport edge or safe area
- **AND** no generic action drawer or dialog SHALL be required to reach either action

#### Scenario: Approve completes directly on a phone

- **WHEN** an owner taps the direct Approve control in a phone approval package
- **THEN** the authorized approval action SHALL be requested without an intervening disclosure

#### Scenario: Send back opens directly on a phone

- **WHEN** an owner taps the direct Send back control
- **THEN** the structured send-back form SHALL become available inline without an action drawer or dialog

#### Scenario: Secondary actions remain reachable on a phone

- **WHEN** Ask Agent, View transcript, or another secondary action applies while an issue awaits approval on a phone
- **THEN** each applicable secondary action SHALL be reachable through the package's non-modal secondary-action control
- **AND** opening or using that control SHALL NOT add a disclosure before direct Approve or Send back

### Requirement: Send-back feedback is guided without changing its workflow contract

The send-back form SHALL offer direction, scope, and detail as a single feedback-category choice and SHALL retain a free-text field for the requested change. A submitted send-back SHALL identify the selected category and preserve the owner's free text in one non-empty feedback body sent through the existing approval feedback action. The submitted feedback SHALL remain visible in workflow feedback history when the issue returns to plan.

#### Scenario: Structured feedback is sent back

- **WHEN** an owner selects direction, scope, or detail, enters non-empty feedback, and submits send-back
- **THEN** the workflow SHALL receive one feedback body that identifies the selected category and contains the entered text
- **AND** the issue SHALL return through the existing send-back workflow transition

#### Scenario: Free text is empty

- **WHEN** the send-back form contains only blank free text
- **THEN** submission SHALL remain unavailable
- **AND** no feedback action SHALL be requested

#### Scenario: Sent-back feedback is reviewed in the next plan round

- **WHEN** an issue re-enters plan after structured feedback was submitted
- **THEN** its workflow feedback history SHALL show the selected category and the owner's free-text request
