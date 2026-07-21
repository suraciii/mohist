### Requirement: Approval shortcuts act only in an actionable approval view

On desktop, the active issue detail page SHALL bind `a` to the enabled approve action and `m` to opening the enabled send-back form while the issue awaits approval. These shortcuts SHALL use the same authorization, pending-state safeguards, and action paths as their visible controls. They MUST NOT approve or open send-back when the corresponding action is unavailable, when the issue is not awaiting approval, or when focus is in an input, textarea, select, or contenteditable element.

#### Scenario: Approve with the keyboard

- **WHEN** an issue awaits approval, approve is enabled, focus is outside an editable control, and the owner presses `a` on desktop
- **THEN** the same approval action as the visible Approve control SHALL be requested once

#### Scenario: Open send-back with the keyboard

- **WHEN** an issue awaits approval, send back is enabled, focus is outside an editable control, and the owner presses `m` on desktop
- **THEN** the send-back form SHALL open

#### Scenario: Approval shortcut is not applicable

- **WHEN** the issue is not awaiting approval or the matching approval action is unavailable
- **AND** the owner presses `a` or `m`
- **THEN** no approval or send-back action SHALL be requested

#### Scenario: Typing is not intercepted

- **WHEN** focus is in an input, textarea, select, or contenteditable element
- **AND** the owner types `a` or `m`
- **THEN** the approval shortcuts SHALL NOT run
- **AND** the focused editor SHALL receive the keystroke normally

### Requirement: Command+Enter submits issue textareas

On desktop, Command+Enter SHALL submit the send-back form when its textarea is focused and SHALL submit the issue comment form when its textarea is focused. Keyboard submission SHALL obey the same content validation and pending-state safeguards as the corresponding submit control and SHALL request each action at most once per keystroke. Enter without the Command modifier SHALL retain normal multiline text-entry behavior.

#### Scenario: Submit send-back feedback from its textarea

- **WHEN** the send-back textarea is focused, the form is valid and not pending, and the owner presses Command+Enter
- **THEN** the same feedback action as the visible submit control SHALL be requested once

#### Scenario: Submit a comment from its textarea

- **WHEN** the issue comment textarea is focused, the comment is non-empty and not pending, and the owner presses Command+Enter
- **THEN** the same comment action as the visible Comment control SHALL be requested once

#### Scenario: Keyboard submission is invalid or pending

- **WHEN** the focused send-back or comment form is invalid or already submitting
- **AND** the owner presses Command+Enter
- **THEN** no additional submission SHALL be requested

#### Scenario: Enter inserts text normally

- **WHEN** the send-back or comment textarea is focused
- **AND** the owner presses Enter without the Command modifier
- **THEN** the textarea SHALL retain normal multiline editing behavior
- **AND** the form SHALL NOT be submitted

### Requirement: Issue-detail shortcuts are discoverable where they apply

The desktop issue detail page SHALL show shortcut hints next to the controls or forms where each shortcut applies. An actionable approval package SHALL identify `a` for approve and `m` for send back, while the send-back and comment forms SHALL identify Command+Enter as their submit shortcut. A hint SHALL NOT claim an approval shortcut is available when its corresponding action is unavailable.

#### Scenario: Approval shortcut hints are visible

- **WHEN** an owner views an actionable approval package on desktop
- **THEN** the approval controls SHALL visibly identify `a` for approve and `m` for send back

#### Scenario: Textarea submit hints are visible

- **WHEN** the send-back form or issue comment form is displayed on desktop
- **THEN** that form SHALL visibly identify Command+Enter as its submit shortcut

#### Scenario: Unavailable approval shortcut is not advertised

- **WHEN** approve or send back is unavailable
- **THEN** the page SHALL NOT present its keyboard hint as an available action
