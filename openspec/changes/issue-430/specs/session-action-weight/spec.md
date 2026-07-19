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

### Requirement: Disabled Compact / Reset actions render a structured tooltip explaining the running-session block

When the `Compact` or `Reset` action in `SessionRecoveryActions` is disabled because the session is currently running (or otherwise not in a finished state), the disabled button SHALL be wrapped by the existing `Tooltip` primitive so a structured explanation is shown on hover or focus, and the rendered button SHALL NOT carry the native `title` attribute. The structured explanation SHALL convey both a short title and a longer reason sentence that names the running-session block as the cause and notes that finishing (or cancelling) the session would unblock the action. The `data-active="true"` attribute already exposed on the disabled button by `SessionRecoveryActions` is the trigger for the structured tooltip; no new contract attribute (no `data-disabled-reason` closed-set) is introduced, because the only currently-known disabled trigger is "session active / not finished".

#### Scenario: Disabled running-session Compact / Reset renders a structured tooltip
- **WHEN** either the Compact or the Reset action is rendered in a disabled state because the session is running
- **AND** a user hovers or focuses the disabled button
- **THEN** a structured tooltip SHALL render containing a short title and a longer reason sentence
- **AND** the reason sentence SHALL explain that the session is still running and that finishing (or cancelling) it would unblock the action

#### Scenario: Native title attribute is removed when structured tooltip is shown
- **WHEN** a Compact or Reset button is rendered in a disabled state because the session is running
- **THEN** the rendered button SHALL NOT carry a native `title` attribute
- **AND** only the structured tooltip SHALL be the source of the disabled reason

#### Scenario: Enabled Compact / Reset does not render the disabled tooltip
- **WHEN** either the Compact or the Reset action is rendered in an enabled state
- **THEN** no disabled-reason tooltip SHALL wrap the button

### Requirement: Action weight changes are presentational only

The action-weight changes (Cancel demotion, structured disabled-reason tooltip on Compact/Reset) SHALL be purely presentational. They SHALL NOT alter the cancel mutation, the compact mutation, the reset mutation, the recovery data fields, the liveness gate, or the underlying enabled/disabled decision for either action. The same input state SHALL yield the same disabled-vs-enabled classification before and after the change.

#### Scenario: Mutations and enabled/disabled logic are unchanged
- **WHEN** the header and recovery actions render with the same input state
- **THEN** the cancel, compact, and reset mutations SHALL behave identically (same API calls, same idempotency handling, same onSuccess/onError semantics)
- **AND** the enabled/disabled decision for Compact and Reset SHALL be derived from the same inputs (`status`, `recoveryAvailable`, `anyPending`)
- **AND** only the rendered variant, position, and disabled-reason tooltip change
