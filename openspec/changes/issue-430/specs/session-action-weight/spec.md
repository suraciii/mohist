### Requirement: Cancel session is not the highest-weight action in the header

The `Cancel session` action SHALL NOT be rendered as the highest visual weight element in the session header. The Cancel trigger SHALL use a secondary visual style (for example `outline`, `ghost`, or icon-only), SHALL be positioned outside the primary metadata row (for example in a dedicated secondary action slot such as a kebab/menu or trailing icon button), and SHALL NOT appear alongside the session metadata as a destructive primary call-to-action. The Cancel action SHALL remain reachable (focusable, activatable via keyboard, labelled for assistive tech), and confirming a cancel SHALL still trigger the existing destructive confirm dialog and call the cancel mutation as before.

#### Scenario: Cancel trigger uses a secondary visual style
- **WHEN** the session is running and the Cancel trigger is rendered in the header
- **THEN** the Cancel trigger SHALL NOT use the `destructive` primary variant
- **AND** SHALL be rendered with a secondary style (outline / ghost / icon-only)

#### Scenario: Cancel trigger is positioned outside the metadata row
- **WHEN** the session is running and the Cancel trigger is rendered in the header
- **THEN** the Cancel trigger SHALL be placed outside the header metadata row
- **AND** SHALL NOT be interleaved with session metadata items (status badge, stage chip, model, turn count, time, duration, session id)

#### Scenario: Cancel trigger remains reachable for keyboard and assistive tech
- **WHEN** the Cancel trigger renders with a secondary style
- **THEN** the trigger SHALL remain focusable via Tab
- **AND** SHALL expose an accessible name (for example `aria-label="Cancel session"`)
- **AND** activating it SHALL open the existing cancel confirmation dialog and SHALL call the cancel mutation

### Requirement: Disabled Compact / Reset actions expose a structured disabled reason

When the `Compact` or `Reset` action in `SessionRecoveryActions` is disabled, the disabled button SHALL expose a stable `data-disabled-reason` attribute identifying why the action is unavailable. The reason SHALL be drawn from a fixed closed set: `"active"` (session is currently running or otherwise not in a finished state), `"prereq"` (the session state does not meet a prerequisite the action requires), and `"unknown"` (the session status could not be confirmed and the action is temporarily unavailable). A parent tooltip affordance SHALL render a structured explanation (a title plus a longer reason line) on hover or focus, derived from that attribute, so users learn why the action is unavailable rather than seeing only a brief browser tooltip.

#### Scenario: Disabled buttons carry a closed-set reason attribute
- **WHEN** either the Compact or the Reset action is rendered in a disabled state
- **THEN** that button SHALL expose a `data-disabled-reason` attribute whose value is one of `"active"`, `"prereq"`, or `"unknown"`
- **AND** SHALL NOT leave the attribute unset while disabled

#### Scenario: Disabled reason renders a structured tooltip
- **WHEN** a user hovers or focuses a disabled Compact or Reset button
- **THEN** a structured tooltip SHALL render containing at least a short title and a longer reason sentence derived from the `data-disabled-reason` value
- **AND** the tooltip SHALL differ across the three reasons so that each reason conveys its specific cause

#### Scenario: Active reason explains the running-session block
- **WHEN** the session is currently running (or otherwise not in a finished state) and Compact or Reset is disabled
- **THEN** `data-disabled-reason="active"` SHALL be set
- **AND** the tooltip SHALL explain that the session is still running and must finish (or be cancelled) before the action becomes available

#### Scenario: Prerequisite reason explains what is missing
- **WHEN** the session is not running but is not yet in a state where Compact or Reset is allowed (for example prerequisite data is missing)
- **THEN** `data-disabled-reason="prereq"` SHALL be set
- **AND** the tooltip SHALL identify the missing prerequisite

#### Scenario: Unknown reason explains the temporary unavailability
- **WHEN** the session status could not be confirmed (for example a network or status query is pending)
- **THEN** `data-disabled-reason="unknown"` SHALL be set
- **AND** the tooltip SHALL indicate that the action is temporarily unavailable and may be retried shortly

### Requirement: Action weight changes are presentational only

The action-weight changes (Cancel demotion, structured disabled-reason tooltips on Compact/Reset) SHALL be purely presentational. They SHALL NOT alter the cancel mutation, the compact mutation, the reset mutation, the recovery data fields, the liveness gate, or the underlying enabled/disabled decision for either action. The same input state SHALL yield the same disabled reason classification before and after the change.

#### Scenario: Mutations and enabled/disabled logic are unchanged
- **WHEN** the header and recovery actions render with the same input state
- **THEN** the cancel, compact, and reset mutations SHALL behave identically (same API calls, same idempotency handling, same onSuccess/onError semantics)
- **AND** the enabled/disabled decision for Compact and Reset SHALL be derived from the same inputs (`status`, `recoveryAvailable`, `anyPending`)
- **AND** only the rendered variant, position, and disabled-reason tooltip change
