### Requirement: An approval-waiting transition on the viewed issue raises a toast while the page is open

When the viewed issue enters an approval-waiting state while the issue detail page is open, the page SHALL raise a toast that names the approval-waiting situation, so an owner with the tab in the background is nudged at the moment the issue needs them. This requirement applies to the issue currently being viewed and therefore overrides the prior behavior that suppressed such toasts for the viewed issue.

#### Scenario: A check completing into an approval wait nudges the owner

- **WHEN** the viewed issue enters an approval-waiting state (for example, a check stage completes and requests approval) while the issue detail page is open
- **THEN** a toast SHALL be raised that names the approval-waiting situation
- **AND** the toast SHALL be raised even though the issue is the one currently being viewed

### Requirement: A blocked transition on the viewed issue raises a toast while the page is open

When the viewed issue enters a blocked state while the issue detail page is open, the page SHALL raise a toast that names the blocked situation.

#### Scenario: The viewed issue becoming blocked nudges the owner

- **WHEN** the viewed issue enters a blocked state while the issue detail page is open
- **THEN** a toast SHALL be raised that names the blocked situation
- **AND** the toast SHALL be raised even though the issue is the one currently being viewed

### Requirement: The viewed-issue nudge is the only toast raised for that transition

While the issue detail page is open, the viewed-issue attention nudge SHALL be the only toast raised for the viewed issue's approval-waiting or blocked transition. The global cross-issue toast rule, which remains suppressed for the issue currently being viewed to avoid double-noticing, SHALL NOT also fire for the same transition, so the owner is nudged exactly once.

#### Scenario: No duplicate toast for a transition on the viewed issue

- **WHEN** the viewed issue enters an approval-waiting or blocked state while its detail page is open
- **THEN** exactly one toast SHALL be raised for that transition on the viewed issue
- **AND** the global cross-issue toast for the same transition SHALL remain suppressed for the viewed issue

### Requirement: Attention nudges are limited to meaningful transitions

Attention toasts SHALL be raised only for the viewed issue entering an approval-waiting state or a blocked state. Routine progress transitions — a task starting, a task completing, or a stage advancing without requiring the owner — SHALL NOT raise an attention toast while the page is open.

#### Scenario: Routine progress transitions do not raise an attention toast

- **WHEN** the viewed issue undergoes a routine progress transition (a task starting, a task completing, or a non-approval stage advance) while its detail page is open
- **THEN** no attention toast SHALL be raised for that transition

### Requirement: Attention nudges behave identically on a phone-width viewport

Attention nudges SHALL fire on a phone-width viewport identically to the desktop viewport: the same approval-waiting and blocked transitions SHALL raise the same toast.

#### Scenario: A nudge fires on a phone-width viewport for a meaningful transition

- **WHEN** the issue detail page renders at a phone-width viewport and the viewed issue enters an approval-waiting or blocked state
- **THEN** the same toast SHALL be raised as on the desktop viewport for that transition
