# issue-detail-confirmation-drawer Specification

## Requirements

### Requirement: Bottom-Sliding Confirmation Drawer for Destructive Actions on Narrow Viewports

On narrow viewports, confirmation for destructive runtime actions — stop and send-back — SHALL be presented as a drawer that slides in from the bottom edge of the viewport, rather than as a centered or full-screen modal dialog or as an inline confirmation block appended to the action surface. The drawer SHALL consume the same `decision.stopRecoverable` and `decision.approvalStage` state and the same issue-detail mutations as the existing confirmation flow.

#### Scenario: Stop confirmation opens as a bottom drawer

- **WHEN** a narrow viewport user activates the Stop primary action
- **THEN** a confirmation drawer slides in from the bottom edge of the viewport
- **AND** no centered or full-screen modal dialog is rendered
- **AND** no inline confirmation block is appended to the status-header action surface

#### Scenario: Send-back confirmation opens as a bottom drawer

- **WHEN** a narrow viewport user activates the Send back primary action
- **THEN** a confirmation drawer containing the send-back feedback form slides in from the bottom edge of the viewport

### Requirement: Status Headline Stays Visible During Confirmation

While the confirmation drawer is open on a narrow viewport, the top sticky status headline SHALL remain visible and SHALL NOT be covered by the drawer, so the user can read the current runtime situation (including the current task and stage progress) at the exact moment they decide to confirm a destructive action.

#### Scenario: Status headline is not obscured while the stop drawer is open

- **WHEN** the stop confirmation drawer is open on a narrow viewport
- **THEN** the sticky status headline at the top of the page remains visible
- **AND** the drawer occupies a region that does not cover the status headline

### Requirement: Consequence Copy and Confirm/Cancel Controls Presented

The confirmation drawer SHALL present the consequence of the destructive action and SHALL expose explicit Confirm and Cancel controls. For stop, the consequence copy SHALL reflect `decision.stopRecoverable`: a recoverable stop SHALL state that progress will be preserved so the workflow can be resumed later, and an irreversible stop SHALL state that progress cannot be resumed. For send-back, the drawer SHALL present the feedback input required to submit.

#### Scenario: Recoverable stop presents recoverability consequence

- **WHEN** the stop confirmation drawer opens for an issue whose `decision.stopRecoverable` is true
- **THEN** the drawer states that stopping will preserve progress so the workflow can be resumed later

#### Scenario: Irreversible stop presents irreversibility consequence

- **WHEN** the stop confirmation drawer opens for an issue whose `decision.stopRecoverable` is false
- **THEN** the drawer states that stopping is irreversible and progress cannot be resumed

#### Scenario: Confirm and Cancel are both available

- **WHEN** the confirmation drawer is open
- **THEN** a Cancel control that dismisses the drawer without performing the action is available
- **AND** a Confirm control that performs the destructive action via the existing mutation is available

### Requirement: Narrow-Viewport-Only Drawer With Desktop Inline Confirmation Preserved

The bottom-sliding confirmation drawer SHALL apply only on narrow viewports (below `lg`/1024px). On tablet (`lg`/1024px) and wider viewports, destructive-action confirmation SHALL keep the existing desktop inline confirmation behavior and SHALL NOT render the mobile drawer.

#### Scenario: Desktop keeps inline stop confirmation

- **WHEN** a desktop-width viewport (at or above `lg`/1024px) user activates Stop
- **THEN** the existing inline stop confirmation renders within the status-header action surface
- **AND** no bottom-sliding drawer renders
